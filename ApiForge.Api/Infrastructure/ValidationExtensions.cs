using FluentValidation;

namespace ApiForge.Api.Infrastructure;

public static class ValidationExtensions
{
    // Validates a request with its registered FluentValidation validator.
    // Returns a 400 ValidationProblem result when invalid, otherwise null.
    public static async Task<IResult?> ToProblemAsync<T>(this IValidator<T> validator, T instance)
    {
        var result = await validator.ValidateAsync(instance);
        if (result.IsValid) return null;

        var errors = result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        return Results.ValidationProblem(errors);
    }
}
