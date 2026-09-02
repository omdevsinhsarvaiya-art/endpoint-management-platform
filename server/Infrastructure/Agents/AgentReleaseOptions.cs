namespace EndpointPlatform.Infrastructure.Agents;

/// <summary>
/// Platform-wide settings for agent releases.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TrustMode"/> states what kind of deployment this platform is, and
/// therefore what a release must prove before it is published. It defaults to
/// <see cref="AgentReleaseTrustMode.Internal"/> because Techsara is one company on
/// a private network distributing to its own machines. See
/// <see cref="AgentReleaseTrustMode"/> for what each mode does and does not check;
/// the SHA-256, authorization, audit and HTTPS requirements are not modal.
/// </para>
/// <para>
/// <see cref="ExpectedSignerSubject"/> applies to <see cref="AgentReleaseTrustMode.Public"/>
/// only: the publisher identity every published build must be Authenticode-signed
/// by. It is deliberately <em>not</em> stored on the release row and <em>not</em>
/// accepted from an upload -- the party supplying a build does not get to say who
/// is trusted to have signed it. In Public mode it is required and validated on
/// start; in Internal mode it is ignored and may be left unset without effect.
/// </para>
/// <para>
/// Matched as a case-insensitive substring of the signing certificate's subject,
/// exactly as the agent's own pin works, so server and endpoint cannot disagree
/// about what "the expected publisher" means.
/// </para>
/// </remarks>
public sealed class AgentReleaseOptions
{
    public const string SectionName = "AgentReleases";

    /// <summary>The deployment model this platform publishes releases under.</summary>
    public AgentReleaseTrustMode TrustMode { get; init; } = AgentReleaseTrustMode.Internal;

    /// <summary>
    /// Public mode only. Substring the Authenticode signer's certificate subject
    /// must contain for a release to be publishable.
    /// </summary>
    public string? ExpectedSignerSubject { get; init; }

    /// <summary>Whether a publisher has been configured at all.</summary>
    public bool IsSignerConfigured => !string.IsNullOrWhiteSpace(ExpectedSignerSubject);

    /// <summary>
    /// Whether this configuration is coherent: Public mode without a publisher is a
    /// gate with nothing to compare against, and is refused at startup rather than
    /// discovered at the first publish.
    /// </summary>
    public bool IsValid => TrustMode != AgentReleaseTrustMode.Public || IsSignerConfigured;
}
