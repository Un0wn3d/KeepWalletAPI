using System.Net;
using System.Net.Http.Json;
using KeepWalletAPI.Contracts;

namespace KeepWalletAPI.Tests.Infrastructure;

[Collection(ApiCollection.Name)]
public abstract class ApiTestBase(ApiFactory factory)
{
    protected const string ExpenseCategoryName = "Їжа";
    protected const string IncomeCategoryName = "Зарплата";

    protected ApiFactory Factory { get; } = factory;

    protected ApiUser Admin => Factory.Admin;

    protected Task<ApiUser> NewUserAsync(string? fullName = null) => Factory.RegisterUserAsync(fullName);

    protected static async Task<T> GetAsync<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await ApiAssert.ReadAsync<T>(response);
    }

    protected static async Task<HttpStatusCode> StatusOfAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }

    protected async Task<int> CategoryIdAsync(string name)
    {
        using var anonymous = Factory.CreateAnonymousClient();
        var categories = await GetAsync<List<CategoryResponse>>(anonymous, "/api/categories");
        return categories.Single(c => c.Name == name).Id;
    }

    protected static async Task<CategoryResponse> CreateCategoryAsync(ApiUser user, string type = "expense")
    {
        using var response = await user.Client.PostAsJsonAsync(
            "/api/categories",
            new CreateCategoryRequest($"Категорія {Guid.NewGuid():N}", type));
        return await ApiAssert.ReadAsync<CategoryResponse>(response, HttpStatusCode.Created);
    }

    protected static async Task<BankAccountResponse> CreateAccountAsync(
        ApiUser user,
        decimal balance = 1000m,
        string currency = "UAH",
        bool isDefault = false,
        Guid? groupId = null)
    {
        using var response = await user.Client.PostAsJsonAsync(
            "/api/bank-accounts",
            new CreateBankAccountRequest($"Рахунок {Guid.NewGuid():N}", currency, balance, isDefault, groupId));
        return await ApiAssert.ReadAsync<BankAccountResponse>(response, HttpStatusCode.Created);
    }

    protected static Task<BankAccountResponse> GetAccountAsync(ApiUser user, Guid accountId) =>
        GetAsync<BankAccountResponse>(user.Client, $"/api/bank-accounts/{accountId}");

    protected static async Task<TransactionResponse> CreateTransactionAsync(
        ApiUser user,
        Guid accountId,
        int categoryId,
        decimal amount,
        string? description = null,
        int? savingId = null)
    {
        using var response = await user.Client.PostAsJsonAsync(
            "/api/transactions",
            new CreateTransactionRequest(accountId, null, categoryId, savingId, null, amount, description, DateTimeOffset.UtcNow));
        return await ApiAssert.ReadAsync<TransactionResponse>(response, HttpStatusCode.Created);
    }

    protected static async Task<SavingResponse> CreateSavingAsync(ApiUser user, decimal? targetAmount = 5000m, decimal currentAmount = 0m)
    {
        using var response = await user.Client.PostAsJsonAsync(
            "/api/savings",
            new CreateSavingRequest($"Накопичення {Guid.NewGuid():N}", targetAmount, currentAmount, new DateOnly(2030, 1, 1), "UAH", "car", "#10B981"));
        return await ApiAssert.ReadAsync<SavingResponse>(response, HttpStatusCode.Created);
    }

    protected static async Task<GroupResponse> CreateGroupAsync(ApiUser owner)
    {
        using var response = await owner.Client.PostAsJsonAsync(
            "/api/groups",
            new CreateGroupRequest($"Група {Guid.NewGuid():N}", "home", "#3B82F6"));
        return await ApiAssert.ReadAsync<GroupResponse>(response, HttpStatusCode.Created);
    }

    protected static async Task AddMemberAsync(ApiUser owner, Guid groupId, ApiUser member, string roleName = "member")
    {
        using var response = await owner.Client.PostAsJsonAsync(
            $"/api/groups/{groupId}/members",
            new AddGroupMemberRequest(member.Username, roleName));
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);
    }
}
