using System.Net;
using System.Net.Http.Json;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class CategoriesTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact(DisplayName = "Категорії: GET повертає системні категорії без авторизації")]
    public async Task Get_categories()
    {
        using var anonymous = Factory.CreateAnonymousClient();
        var categories = await GetAsync<List<CategoryResponse>>(anonymous, "/api/categories");

        Assert.Contains(categories, c => c.Name == ExpenseCategoryName && c.Type == "expense" && c.IconKey == "other");
        Assert.Contains(categories, c => c.Name == IncomeCategoryName && c.Type == "income" && c.IconKey == "income");
    }

    [Fact(DisplayName = "Категорії: популярні категорії користувача йдуть першими")]
    public async Task Get_categories_popular_first()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user);
        var category = await CreateCategoryAsync(user);
        await CreateTransactionAsync(user, account.Id, category.Id, 10m);

        var categories = await GetAsync<List<CategoryResponse>>(user.Client, "/api/categories");

        Assert.Equal(category.Id, categories[0].Id);
    }

    [Fact(DisplayName = "Категорії: POST створює категорію, повтор повертає існуючу")]
    public async Task Post_category()
    {
        var user = await NewUserAsync();
        var name = $"Хобі {Guid.NewGuid():N}";

        using var created = await user.Client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest($"  {name}  ", "EXPENSE"));
        var category = await ApiAssert.ReadAsync<CategoryResponse>(created, HttpStatusCode.Created);
        Assert.Equal(name, category.Name);
        Assert.Equal("expense", category.Type);

        using var duplicate = await user.Client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest(name.ToUpperInvariant(), "expense"));
        var existing = await ApiAssert.ReadAsync<CategoryResponse>(duplicate);
        Assert.Equal(category.Id, existing.Id);

        using var sameNameIncome = await user.Client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest(name, "income"));
        var income = await ApiAssert.ReadAsync<CategoryResponse>(sameNameIncome, HttpStatusCode.Created);
        Assert.NotEqual(category.Id, income.Id);

        using var anonymous = Factory.CreateAnonymousClient();
        var all = await GetAsync<List<CategoryResponse>>(anonymous, "/api/categories");
        Assert.Contains(all, c => c.Id == category.Id);
    }

    [Fact(DisplayName = "Категорії: POST з невалідним типом, порожньою назвою або без токена")]
    public async Task Post_category_invalid()
    {
        var user = await NewUserAsync();
        using var anonymous = Factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(
            user.Client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest("Назва", "transfer"))));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(
            user.Client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest("   ", "expense"))));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(
            anonymous.PostAsJsonAsync("/api/categories", new CreateCategoryRequest("Назва", "expense"))));
    }

    [Fact(DisplayName = "Категорії: PATCH перейменовує категорію")]
    public async Task Patch_category()
    {
        var user = await NewUserAsync();
        var category = await CreateCategoryAsync(user);
        var newName = $"Перейменована {Guid.NewGuid():N}";

        using var response = await user.Client.PatchAsJsonAsync($"/api/categories/{category.Id}", new UpdateCategoryRequest(newName));
        var updated = await ApiAssert.ReadAsync<CategoryResponse>(response);
        Assert.Equal(newName, updated.Name);

        var all = await GetAsync<List<CategoryResponse>>(user.Client, "/api/categories");
        Assert.Equal(newName, all.Single(c => c.Id == category.Id).Name);

        using var blank = await user.Client.PatchAsJsonAsync($"/api/categories/{category.Id}", new UpdateCategoryRequest("  "));
        Assert.Equal(newName, (await ApiAssert.ReadAsync<CategoryResponse>(blank)).Name);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(
            user.Client.PatchAsJsonAsync("/api/categories/999999", new UpdateCategoryRequest("x"))));
    }

    [Fact(DisplayName = "Категорії: DELETE невикористаної категорії")]
    public async Task Delete_category()
    {
        var user = await NewUserAsync();
        var category = await CreateCategoryAsync(user);
        await ApiAssert.StatusAsync(
            await user.Client.PutAsJsonAsync("/api/user-categories", new UpdateUserCategoryPreferencesRequest([category.Id])),
            HttpStatusCode.NoContent);

        using var response = await user.Client.DeleteAsync($"/api/categories/{category.Id}");
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        var all = await GetAsync<List<CategoryResponse>>(user.Client, "/api/categories");
        Assert.DoesNotContain(all, c => c.Id == category.Id);
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.DeleteAsync($"/api/categories/{category.Id}")));
    }

    [Fact(DisplayName = "Категорії: DELETE категорії з транзакціями — 409")]
    public async Task Delete_used_category_conflict()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user);
        var category = await CreateCategoryAsync(user);
        await CreateTransactionAsync(user, account.Id, category.Id, 50m);

        Assert.Equal(HttpStatusCode.Conflict, await StatusOfAsync(user.Client.DeleteAsync($"/api/categories/{category.Id}")));
    }

    [Fact(DisplayName = "Категорії: об'єднання переносить транзакції і видаляє джерело")]
    public async Task Merge_categories()
    {
        var user = await NewUserAsync();
        var account = await CreateAccountAsync(user, balance: 1000m);
        var source = await CreateCategoryAsync(user);
        var target = await CreateCategoryAsync(user);
        var transaction = await CreateTransactionAsync(user, account.Id, source.Id, 100m);
        await ApiAssert.StatusAsync(
            await user.Client.PutAsJsonAsync("/api/budgets/category/" + source.Id, new UpsertBudgetRequest(null, 300m, null, new DateOnly(2026, 1, 1), true)),
            HttpStatusCode.OK);

        using var response = await user.Client.PostAsJsonAsync($"/api/categories/{source.Id}/merge", new MergeCategoryRequest(target.Id));
        var merged = await ApiAssert.ReadAsync<CategoryResponse>(response);
        Assert.Equal(target.Id, merged.Id);

        var moved = await GetAsync<TransactionResponse>(user.Client, $"/api/transactions/{transaction.Id}");
        Assert.Equal(target.Id, moved.CategoryId);
        var budgets = await GetAsync<List<BudgetResponse>>(user.Client, "/api/budgets");
        Assert.Contains(budgets, b => b.CategoryId == target.Id && b.Amount == 300m);
        Assert.DoesNotContain(budgets, b => b.CategoryId == source.Id);

        var all = await GetAsync<List<CategoryResponse>>(user.Client, "/api/categories");
        Assert.DoesNotContain(all, c => c.Id == source.Id);
        Assert.Equal(900m, (await GetAccountAsync(user, account.Id)).Balance);
    }

    [Fact(DisplayName = "Категорії: об'єднання з собою, різних типів або неіснуючих — помилка")]
    public async Task Merge_categories_invalid()
    {
        var user = await NewUserAsync();
        var expense = await CreateCategoryAsync(user, "expense");
        var income = await CreateCategoryAsync(user, "income");

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(
            user.Client.PostAsJsonAsync($"/api/categories/{expense.Id}/merge", new MergeCategoryRequest(expense.Id))));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOfAsync(
            user.Client.PostAsJsonAsync($"/api/categories/{expense.Id}/merge", new MergeCategoryRequest(income.Id))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(
            user.Client.PostAsJsonAsync($"/api/categories/{expense.Id}/merge", new MergeCategoryRequest(999999))));
    }

    [Fact(DisplayName = "Категорії користувача: без налаштувань усі вибрані")]
    public async Task Get_user_categories_default()
    {
        var user = await NewUserAsync();

        var categories = await GetAsync<List<UserCategoryPreferenceResponse>>(user.Client, "/api/user-categories");

        Assert.NotEmpty(categories);
        Assert.All(categories, c => Assert.True(c.IsSelected));

        using var anonymous = Factory.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOfAsync(anonymous.GetAsync("/api/user-categories")));
    }

    [Fact(DisplayName = "Категорії користувача: PUT зберігає вибір, іконку і колір")]
    public async Task Put_user_categories()
    {
        var user = await NewUserAsync();
        var other = await NewUserAsync();
        var food = await CategoryIdAsync(ExpenseCategoryName);
        var salary = await CategoryIdAsync(IncomeCategoryName);

        using var response = await user.Client.PutAsJsonAsync(
            "/api/user-categories",
            new UpdateUserCategoryPreferencesRequest(
                [food, salary],
                [new UserCategoryPreferenceUpdate(food, "groceries", "#FF0000")]));
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        var categories = await GetAsync<List<UserCategoryPreferenceResponse>>(user.Client, "/api/user-categories");
        var foodPreference = categories.Single(c => c.Id == food);
        Assert.True(foodPreference.IsSelected);
        Assert.Equal("groceries", foodPreference.IconKey);
        Assert.Equal("#FF0000", foodPreference.Color);
        Assert.True(categories.Single(c => c.Id == salary).IsSelected);
        Assert.Equal([food, salary], categories.Where(c => c.IsSelected).Select(c => c.Id).Order());

        var otherCategories = await GetAsync<List<UserCategoryPreferenceResponse>>(other.Client, "/api/user-categories");
        Assert.All(otherCategories, c => Assert.True(c.IsSelected));
        Assert.Null(otherCategories.Single(c => c.Id == food).Color);

        await ApiAssert.StatusAsync(
            await user.Client.PutAsJsonAsync("/api/user-categories", new UpdateUserCategoryPreferencesRequest([salary])),
            HttpStatusCode.NoContent);
        var reselected = await GetAsync<List<UserCategoryPreferenceResponse>>(user.Client, "/api/user-categories");
        Assert.Equal([salary], reselected.Where(c => c.IsSelected).Select(c => c.Id));
    }
}
