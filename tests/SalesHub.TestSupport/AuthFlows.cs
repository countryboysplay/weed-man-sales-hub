using System.Net.Http.Json;
using System.Text.Json;
using SalesHub.Contracts.Auth;
using SalesHub.Contracts.Users;

namespace SalesHub.TestSupport;

/// <summary>HTTP flows every test suite repeats: login, CSRF, user creation.</summary>
public static class AuthFlows
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<LoginResponse> LoginAsync(
        HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest(username, password), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(Json))!;
    }

    /// <summary>Fetches an antiforgery token; the partner cookie lands in the jar.</summary>
    public static async Task<string> GetCsrfTokenAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/auth/csrf");
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()!;
    }

    public static async Task<HttpResponseMessage> PostWithCsrfAsync<T>(
        HttpClient client, string url, T payload)
    {
        var token = await GetCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> PatchWithCsrfAsync<T>(
        HttpClient client, string url, T payload)
    {
        var token = await GetCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(payload, options: Json),
        };
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> DeleteWithCsrfAsync(
        HttpClient client, string url)
    {
        var token = await GetCsrfTokenAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    /// <summary>Logs the owner in on a fresh client and creates a user with the given role.</summary>
    public static async Task<UserResponse> CreateUserAsUnownedAdminAsync(
        SalesHubApiFactory factory, string username, string password, string role)
    {
        var admin = factory.CreateCookieClient();
        await LoginAsync(admin, SalesHubApiFactory.OwnerUsername, SalesHubApiFactory.OwnerPassword);
        var response = await PostWithCsrfAsync(admin, "/api/v1/users",
            new CreateUserRequest(username, password, $"Test {username}", role, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserResponse>(Json))!;
    }
}
