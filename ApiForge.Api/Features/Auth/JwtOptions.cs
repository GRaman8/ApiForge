namespace ApiForge.Api.Features.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "ApiForge";
    public string Audience { get; set; } = "ApiForgeClients";
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}
