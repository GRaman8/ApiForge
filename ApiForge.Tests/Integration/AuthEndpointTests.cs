using System.Net;
using System.Net.Http.Json;

namespace ApiForge.Tests.Integration;

[Collection("api")]
public class AuthEndpointTests(ApiForgeFixture fixture)
{
    private readonly HttpClient _client = fixture.CreateClient();

    private static string Email() => $"user-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Register_then_login_returns_tokens()
    {
        var email = Email();
        var reg = await _client.PostAsJsonAsync("/auth/register", new { email, password = "password123" });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        var login = await _client.PostAsJsonAsync("/auth/login", new { email, password = "password123" });
        login.EnsureSuccessStatusCode();

        var body = await login.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body!.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(body.RefreshToken));
    }

    [Fact]
    public async Task Register_duplicate_email_conflicts()
    {
        var email = Email();
        await _client.PostAsJsonAsync("/auth/register", new { email, password = "password123" });
        var second = await _client.PostAsJsonAsync("/auth/register", new { email, password = "password123" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Register_invalid_password_is_rejected()
    {
        var res = await _client.PostAsJsonAsync("/auth/register", new { email = Email(), password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Login_wrong_password_is_unauthorized()
    {
        var email = Email();
        await _client.PostAsJsonAsync("/auth/register", new { email, password = "password123" });
        var login = await _client.PostAsJsonAsync("/auth/login", new { email, password = "wrongpassword" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_and_old_token_is_revoked()
    {
        var email = Email();
        await _client.PostAsJsonAsync("/auth/register", new { email, password = "password123" });
        var login = await _client.PostAsJsonAsync("/auth/login", new { email, password = "password123" });
        var first = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;

        // First refresh succeeds and returns a NEW refresh token.
        var refresh1 = await _client.PostAsJsonAsync("/auth/refresh", new { refreshToken = first.RefreshToken });
        refresh1.EnsureSuccessStatusCode();
        var rotated = (await refresh1.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.NotEqual(first.RefreshToken, rotated.RefreshToken);

        // Re-using the now-revoked original token must fail.
        var reuse = await _client.PostAsJsonAsync("/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    private record TokenResponse(string AccessToken, string RefreshToken);
}
