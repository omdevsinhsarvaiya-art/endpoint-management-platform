namespace EndpointAgent.Core.Abstractions;

/// <summary>Reads the effective screen-lock (interactive idle) timeout.</summary>
/// <remarks>
/// Read-only. Returns null when no timeout is configured or it cannot be read; the
/// evaluator reports Unknown rather than guessing.
/// </remarks>
public interface IScreenLockPolicyReader
{
    /// <summary>Effective seconds of idle before the screen locks, or null if unknown/disabled.</summary>
    ValueTask<int?> GetScreenLockTimeoutSecondsAsync(CancellationToken cancellationToken = default);
}
