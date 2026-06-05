namespace ApiForge.Api.Domain;

public class ApiKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = [];
    public int RateLimit { get; set; } = 60;
    public DateTime? ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }

    public User User { get; set; } = null!;
    public ICollection<UsageEvent> UsageEvents { get; set; } = [];
}
