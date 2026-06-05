using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ApiForge.Tests.Integration;

[Collection("api")]
public class KeyAndItemTests(ApiForgeFixture fixture)
{
    private readonly ApiForgeFixture _fixture = fixture;

    private async Task<string> NewUserTokenAsync()
    {
        var client = _fixture.CreateClient();
        var email = $"user-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/auth/register", new { email, password = "password123" });
        var login = await client.PostAsJsonAsync("/auth/login", new { email, password = "password123" });
        var body = await login.Content.ReadFromJsonAsync<TokenResponse>();
        return body!.AccessToken;
    }

    private HttpClient JwtClient(string token)
    {
        var c = _fixture.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task Create_key_returns_plaintext_once_and_lists_it()
    {
        var client = JwtClient(await NewUserTokenAsync());

        var create = await client.PostAsJsonAsync("/keys",
            new { name = "Prod", scopes = new[] { "read", "write" }, rateLimit = 100 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreateKeyResponse>();
        Assert.StartsWith("apf_live_", created!.Key);

        var list = await client.GetFromJsonAsync<List<KeyListItem>>("/keys");
        Assert.Single(list!);
        Assert.Equal("Prod", list![0].Name);
    }

    [Fact]
    public async Task Keys_endpoint_requires_jwt()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/keys");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task ApiKey_can_create_and_read_items()
    {
        var jwt = JwtClient(await NewUserTokenAsync());
        var created = await (await jwt.PostAsJsonAsync("/keys",
            new { name = "k", scopes = new[] { "read", "write" } })).Content.ReadFromJsonAsync<CreateKeyResponse>();

        var apiClient = _fixture.CreateClient();
        apiClient.DefaultRequestHeaders.Add("X-API-Key", created!.Key);

        var post = await apiClient.PostAsJsonAsync("/items", new { name = "Widget" });
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var items = await apiClient.GetFromJsonAsync<List<ItemDto>>("/items");
        Assert.Contains(items!, i => i.Name == "Widget");
    }

    [Fact]
    public async Task Missing_api_key_is_unauthorized()
    {
        var client = _fixture.CreateClient();
        var res = await client.GetAsync("/items");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Read_only_key_cannot_write()
    {
        var jwt = JwtClient(await NewUserTokenAsync());
        var created = await (await jwt.PostAsJsonAsync("/keys",
            new { name = "readonly", scopes = new[] { "read" } })).Content.ReadFromJsonAsync<CreateKeyResponse>();

        var apiClient = _fixture.CreateClient();
        apiClient.DefaultRequestHeaders.Add("X-API-Key", created!.Key);

        var post = await apiClient.PostAsJsonAsync("/items", new { name = "Nope" });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task Revoked_key_is_rejected()
    {
        var jwt = JwtClient(await NewUserTokenAsync());
        var created = await (await jwt.PostAsJsonAsync("/keys",
            new { name = "temp", scopes = new[] { "read" } })).Content.ReadFromJsonAsync<CreateKeyResponse>();

        var del = await jwt.DeleteAsync($"/keys/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var apiClient = _fixture.CreateClient();
        apiClient.DefaultRequestHeaders.Add("X-API-Key", created.Key);
        var res = await apiClient.GetAsync("/items");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    private record TokenResponse(string AccessToken, string RefreshToken);
    private record CreateKeyResponse(Guid Id, string Key, string Prefix);
    private record KeyListItem(Guid Id, string Name, string[] Scopes, bool IsRevoked);
    private record ItemDto(Guid Id, string Name);
}
