namespace ApiForge.Api.Middleware;

// Marks an endpoint as requiring a valid X-API-Key. The middleware skips any endpoint
// that does not carry this metadata.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireApiKeyAttribute : Attribute;

// Marks an endpoint as requiring a specific scope on the presented API key.
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireScopeAttribute(string scope) : Attribute
{
    public string Scope { get; } = scope;
}
