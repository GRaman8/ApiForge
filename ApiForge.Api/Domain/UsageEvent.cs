namespace ApiForge.Api.Domain;

public class UsageEvent
{
    public long Id { get; set; }
    public Guid ApiKeyId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int LatencyMs { get; set; }
    public DateTime RequestedAt { get; set; }

    public ApiKey ApiKey { get; set; } = null!;
}
