namespace ApiForge.Api.Domain;

// A small demo resource that is protected by API keys (X-API-Key), used to exercise
// the middleware, scopes, rate limiting and usage tracking end-to-end.
public class Item
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
