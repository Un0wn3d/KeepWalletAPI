using System.Net;
using System.Net.Http.Json;

namespace KeepWalletAPI.Tests.Infrastructure;

public static class ApiAssert
{
    /// <summary>Перевіряє код відповіді; при невідповідності показує тіло відповіді сервера.</summary>
    public static async Task StatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        Assert.Fail($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.PathAndQuery}: " +
            $"очікувався {(int)expected} {expected}, отримано {(int)response.StatusCode} {response.StatusCode}. Тіло: {body}");
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        await StatusAsync(response, expected);
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
