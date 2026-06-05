using FluentValidation;

namespace ApiForge.Api.Features.Keys;

public record CreateKeyRequest(string Name, string[]? Scopes, int? RateLimit, DateTime? ExpiresAt);

public class CreateKeyRequestValidator : AbstractValidator<CreateKeyRequest>
{
    private static readonly string[] AllowedScopes = ["read", "write"];

    public CreateKeyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.RateLimit).GreaterThan(0).LessThanOrEqualTo(10_000)
            .When(x => x.RateLimit.HasValue);
        RuleForEach(x => x.Scopes).Must(s => AllowedScopes.Contains(s))
            .When(x => x.Scopes is not null)
            .WithMessage("Scope must be one of: read, write");
        RuleFor(x => x.ExpiresAt).GreaterThan(DateTime.UtcNow)
            .When(x => x.ExpiresAt.HasValue)
            .WithMessage("ExpiresAt must be in the future");
    }
}
