using System.Net;
using System.Net.Http.Json;
using KeepWalletAPI.Contracts;
using KeepWalletAPI.Tests.Infrastructure;

namespace KeepWalletAPI.Tests;

public sealed class SavingsTests(ApiFactory factory) : ApiTestBase(factory)
{
    [Fact(DisplayName = "Накопичення: POST створює накопичення, GET повертає його")]
    public async Task Post_and_get_saving()
    {
        var user = await NewUserAsync();

        using var response = await user.Client.PostAsJsonAsync(
            "/api/savings",
            new CreateSavingRequest("  Відпустка  ", 30000m, 5000m, new DateOnly(2027, 7, 1), "eur", "plane", "#F59E0B"));
        var saving = await ApiAssert.ReadAsync<SavingResponse>(response, HttpStatusCode.Created);

        Assert.Equal($"/api/savings/{saving.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Відпустка", saving.Name);
        Assert.Equal("EUR", saving.Currency);
        Assert.Equal(30000m, saving.TargetAmount);
        Assert.Equal(5000m, saving.CurrentAmount);
        Assert.Equal(new DateOnly(2027, 7, 1), saving.Deadline);
        Assert.Equal("plane", saving.IconKey);
        Assert.Equal("#F59E0B", saving.Color);
        Assert.False(saving.IsCompleted);

        var single = await GetAsync<SavingResponse>(user.Client, $"/api/savings/{saving.Id}");
        Assert.Equal(saving with { OwnerDisplay = user.Username }, single);

        Assert.Equal([saving.Id], (await GetAsync<List<SavingResponse>>(user.Client, "/api/savings")).Select(s => s.Id));
    }

    [Fact(DisplayName = "Накопичення: досягнута ціль одразу позначається виконаною")]
    public async Task Post_saving_completed()
    {
        var user = await NewUserAsync();

        var saving = await CreateSavingAsync(user, targetAmount: 1000m, currentAmount: 1000m);

        Assert.True(saving.IsCompleted);
    }

    [Fact(DisplayName = "Накопичення: невалідна валюта замінюється на UAH")]
    public async Task Post_saving_invalid_currency()
    {
        var user = await NewUserAsync();

        using var response = await user.Client.PostAsJsonAsync(
            "/api/savings", new CreateSavingRequest("Подарунок", null, 0m, null, "XX"));
        var saving = await ApiAssert.ReadAsync<SavingResponse>(response, HttpStatusCode.Created);

        Assert.Equal("UAH", saving.Currency);
        Assert.Equal("other", saving.IconKey);
        Assert.Null(saving.TargetAmount);
    }

    [Fact(DisplayName = "Накопичення: PUT оновлює всі поля")]
    public async Task Put_saving()
    {
        var user = await NewUserAsync();
        var saving = await CreateSavingAsync(user);

        using var response = await user.Client.PutAsJsonAsync(
            $"/api/savings/{saving.Id}",
            new UpdateSavingRequest("Ноутбук", 40000m, 40000m, new DateOnly(2026, 12, 31), true, "USD", "laptop", "#111111"));
        var updated = await ApiAssert.ReadAsync<SavingResponse>(response);

        Assert.Equal("Ноутбук", updated.Name);
        Assert.Equal(40000m, updated.TargetAmount);
        Assert.Equal(40000m, updated.CurrentAmount);
        Assert.True(updated.IsCompleted);
        Assert.Equal("USD", updated.Currency);

        var single = await GetAsync<SavingResponse>(user.Client, $"/api/savings/{saving.Id}");
        Assert.Equal("Ноутбук", single.Name);
        Assert.Equal("laptop", single.IconKey);
        Assert.Equal(new DateOnly(2026, 12, 31), single.Deadline);
    }

    [Fact(DisplayName = "Накопичення: DELETE видаляє накопичення разом зі списком бажань")]
    public async Task Delete_saving()
    {
        var user = await NewUserAsync();
        var saving = await CreateSavingAsync(user);
        await ApiAssert.StatusAsync(
            await user.Client.PostAsJsonAsync($"/api/savings/{saving.Id}/items", new CreateSavingItemRequest("Мишка", 800m, 1, false)),
            HttpStatusCode.Created);

        using var response = await user.Client.DeleteAsync($"/api/savings/{saving.Id}");
        await ApiAssert.StatusAsync(response, HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.GetAsync($"/api/savings/{saving.Id}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.GetAsync($"/api/savings/{saving.Id}/items")));
        Assert.Empty(await GetAsync<List<SavingResponse>>(user.Client, "/api/savings"));
    }

    [Fact(DisplayName = "Накопичення: чуже накопичення недоступне")]
    public async Task Foreign_saving_not_found()
    {
        var owner = await NewUserAsync();
        var stranger = await NewUserAsync();
        var saving = await CreateSavingAsync(owner);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.GetAsync($"/api/savings/{saving.Id}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.PutAsJsonAsync(
            $"/api/savings/{saving.Id}", new UpdateSavingRequest("Hacked", 1m, 0m, null, false))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.DeleteAsync($"/api/savings/{saving.Id}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.GetAsync($"/api/savings/{saving.Id}/items")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(stranger.Client.PostAsJsonAsync(
            $"/api/savings/{saving.Id}/items", new CreateSavingItemRequest("x", 1m, null, false))));
        Assert.Empty(await GetAsync<List<SavingResponse>>(stranger.Client, "/api/savings"));

        Assert.Equal(saving.Name, (await GetAsync<SavingResponse>(owner.Client, $"/api/savings/{saving.Id}")).Name);
    }

    [Fact(DisplayName = "Список бажань: POST, GET, PUT і DELETE")]
    public async Task Saving_items_crud()
    {
        var user = await NewUserAsync();
        var saving = await CreateSavingAsync(user);

        using var createKeyboard = await user.Client.PostAsJsonAsync(
            $"/api/savings/{saving.Id}/items", new CreateSavingItemRequest("  Клавіатура  ", 1200m, 2, false));
        var keyboard = await ApiAssert.ReadAsync<SavingItemResponse>(createKeyboard, HttpStatusCode.Created);
        Assert.Equal($"/api/savings/{saving.Id}/items/{keyboard.Id}", createKeyboard.Headers.Location?.OriginalString);
        Assert.Equal("Клавіатура", keyboard.Name);

        using var createMouse = await user.Client.PostAsJsonAsync(
            $"/api/savings/{saving.Id}/items", new CreateSavingItemRequest("Миша", 800m, 1, true));
        var mouse = await ApiAssert.ReadAsync<SavingItemResponse>(createMouse, HttpStatusCode.Created);

        var items = await GetAsync<List<SavingItemResponse>>(user.Client, $"/api/savings/{saving.Id}/items");
        Assert.Equal([keyboard.Id, mouse.Id], items.Select(i => i.Id));

        using var update = await user.Client.PutAsJsonAsync(
            $"/api/savings/{saving.Id}/items/{keyboard.Id}", new UpdateSavingItemRequest("Механічна клавіатура", 2500m, 1, true));
        var updated = await ApiAssert.ReadAsync<SavingItemResponse>(update);
        Assert.Equal("Механічна клавіатура", updated.Name);
        Assert.Equal(2500m, updated.Price);
        Assert.True(updated.IsPurchased);

        using var delete = await user.Client.DeleteAsync($"/api/savings/{saving.Id}/items/{mouse.Id}");
        await ApiAssert.StatusAsync(delete, HttpStatusCode.NoContent);

        var remaining = Assert.Single(await GetAsync<List<SavingItemResponse>>(user.Client, $"/api/savings/{saving.Id}/items"));
        Assert.Equal(updated, remaining);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.DeleteAsync($"/api/savings/{saving.Id}/items/{mouse.Id}")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.PutAsJsonAsync(
            $"/api/savings/{saving.Id}/items/999999", new UpdateSavingItemRequest("x", 1m, null, false))));
    }

    [Fact(DisplayName = "Список бажань: елемент іншого накопичення не змінюється")]
    public async Task Saving_item_wrong_saving()
    {
        var user = await NewUserAsync();
        var first = await CreateSavingAsync(user);
        var second = await CreateSavingAsync(user);
        using var create = await user.Client.PostAsJsonAsync(
            $"/api/savings/{first.Id}/items", new CreateSavingItemRequest("Навушники", 3000m, null, false));
        var item = await ApiAssert.ReadAsync<SavingItemResponse>(create, HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(user.Client.DeleteAsync($"/api/savings/{second.Id}/items/{item.Id}")));
        Assert.Single(await GetAsync<List<SavingItemResponse>>(user.Client, $"/api/savings/{first.Id}/items"));
    }

    [Fact(DisplayName = "Накопичення: спільне накопичення — учасник редагує, глядач лише читає")]
    public async Task Shared_saving_by_role()
    {
        var owner = await NewUserAsync();
        var member = await NewUserAsync();
        var viewer = await NewUserAsync();
        var group = await CreateGroupAsync(owner);
        await AddMemberAsync(owner, group.Id, member);
        await AddMemberAsync(owner, group.Id, viewer, "viewer");
        var saving = await CreateSavingAsync(owner);

        using var share = await owner.Client.PutAsJsonAsync($"/api/savings/{saving.Id}/groups", new ReplaceResourceGroupsRequest([group.Id]));
        await ApiAssert.StatusAsync(share, HttpStatusCode.OK);

        var memberSaving = Assert.Single(await GetAsync<List<SavingResponse>>(member.Client, "/api/savings"));
        Assert.Equal(group.Id, memberSaving.GroupId);
        Assert.Equal(owner.Username, memberSaving.OwnerDisplay);

        await ApiAssert.StatusAsync(
            await member.Client.PostAsJsonAsync($"/api/savings/{saving.Id}/items", new CreateSavingItemRequest("Від учасника", 10m, null, false)),
            HttpStatusCode.Created);
        await ApiAssert.StatusAsync(
            await member.Client.PutAsJsonAsync($"/api/savings/{saving.Id}", new UpdateSavingRequest("Спільна мета", 5000m, 100m, null, false)),
            HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(member.Client.DeleteAsync($"/api/savings/{saving.Id}")));

        Assert.Single(await GetAsync<List<SavingItemResponse>>(viewer.Client, $"/api/savings/{saving.Id}/items"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(viewer.Client.PostAsJsonAsync(
            $"/api/savings/{saving.Id}/items", new CreateSavingItemRequest("Від глядача", 10m, null, false))));
        Assert.Equal(HttpStatusCode.NotFound, await StatusOfAsync(viewer.Client.PutAsJsonAsync(
            $"/api/savings/{saving.Id}", new UpdateSavingRequest("Глядач", 1m, 0m, null, false))));

        Assert.Equal("Спільна мета", (await GetAsync<SavingResponse>(owner.Client, $"/api/savings/{saving.Id}")).Name);

        using var unshare = await owner.Client.PutAsJsonAsync($"/api/savings/{saving.Id}/group", new ShareResourceWithGroupRequest(null));
        await ApiAssert.StatusAsync(unshare, HttpStatusCode.OK);
        Assert.Empty(await GetAsync<List<SavingResponse>>(member.Client, "/api/savings"));
    }
}
