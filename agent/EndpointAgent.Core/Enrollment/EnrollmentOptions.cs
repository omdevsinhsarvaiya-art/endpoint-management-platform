namespace EndpointAgent.Core.Enrollment;

/// <summary>
/// Enrollment bootstrap configuration.
/// </summary>
/// <remarks>
/// The token is supplied once, at install time, via the
/// <c>ENDPOINTAGENT_Enrollment__Token</c> environment variable or an installer
/// parameter. It is consumed on successful enrollment and never persisted by the
/// agent — after enrollment the device credential is the identity, and the token
/// value in this options object is cleared from memory.
/// </remarks>
public sealed class EnrollmentOptions
{
    public const string SectionName = "Enrollment";

    /// <summary>The one-time enrollment token. Null once enrolled (or never provided).</summary>
    public string? Token { get; set; }
}
