using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ApiForge.Api.Middleware;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ApiForge.Api.Features.Auth;

public class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _opt = options.Value;

    public string IssueAccessToken(Guid userId, string email)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, email)
            ],
            expires: DateTime.UtcNow.AddMinutes(_opt.AccessTokenMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // Returns the plaintext refresh token (shown to the client once) and its SHA-256 hash (stored).
    public (string plaintext, string hash) NewRefreshToken()
    {
        var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (plaintext, ApiKeyMiddleware.Hash(plaintext));
    }

    public DateTime RefreshExpiry() => DateTime.UtcNow.AddDays(_opt.RefreshTokenDays);
}
