using System.Runtime.CompilerServices;

namespace EndpointPlatform.Domain.Common;

/// <summary>
/// Small argument-validation helpers used by domain constructors so that an
/// invalid entity can never be constructed in the first place.
/// </summary>
internal static class Guard
{
    public static string NotNullOrWhiteSpace(
        string? value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null,
        int? maxLength = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be null, empty or whitespace.", paramName);
        }

        var trimmed = value.Trim();

        if (maxLength is { } limit && trimmed.Length > limit)
        {
            throw new ArgumentException(
                $"Value must be at most {limit} characters; was {trimmed.Length}.",
                paramName);
        }

        return trimmed;
    }

    public static string? OptionalMaxLength(
        string? value,
        int maxLength,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.Length > maxLength)
        {
            throw new ArgumentException(
                $"Value must be at most {maxLength} characters; was {trimmed.Length}.",
                paramName);
        }

        return trimmed;
    }

    public static Guid NotEmpty(Guid value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be the empty GUID.", paramName);
        }

        return value;
    }
}
