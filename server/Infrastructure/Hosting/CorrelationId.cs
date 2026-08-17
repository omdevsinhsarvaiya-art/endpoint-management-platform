namespace EndpointPlatform.Infrastructure.Hosting;

/// <summary>Names shared by the correlation-id middleware and by clients.</summary>
public static class CorrelationId
{
    /// <summary>Request and response header carrying the correlation identifier.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Key under which the correlation id is pushed into the log context.</summary>
    public const string LogPropertyName = "CorrelationId";

    /// <summary>Maximum accepted length of a client-supplied correlation id.</summary>
    public const int MaxLength = 128;
}

/// <summary>
/// Exposes the current request's correlation id to services that are not on the
/// HTTP pipeline (the audit writer in particular).
/// </summary>
public interface ICorrelationIdAccessor
{
    string CorrelationId { get; }
}
