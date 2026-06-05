namespace ApiForge.Api.Domain;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public ICollection<ApiKey> ApiKeys { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}
