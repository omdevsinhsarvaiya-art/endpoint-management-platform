namespace EndpointPlatform.Infrastructure.Agents;

/// <summary>
/// Platform-wide settings for agent releases.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ExpectedSignerSubject"/> is the publisher identity every published
/// agent build must be Authenticode-signed by. It is deliberately <em>not</em>
/// stored on the release row and <em>not</em> accepted from an upload: the whole
/// point of a signer pin is that the party uploading the artifact does not get to
/// say who is trusted to have signed it.
/// </para>
/// <para>
/// When it is unset, publishing is refused. That is fail-closed on purpose. A
/// platform that pushes installers onto machines as SYSTEM should not fall back to
/// "any signature will do", and certainly not to "no signature will do", because
/// nobody got round to configuring the publisher.
/// </para>
/// <para>
/// Matched as a case-insensitive substring of the signing certificate's subject
/// -- <c>CN=Techsara Solutions</c> matches
/// <c>CN=Techsara Solutions, O=Techsara Solutions, L=..., C=IN</c> -- which is
/// exactly how the agent's own pin works, so server and endpoint cannot disagree
/// about what "the expected publisher" means.
/// </para>
/// </remarks>
public sealed class AgentReleaseOptions
{
    public const string SectionName = "AgentReleases";

    /// <summary>
    /// Substring the Authenticode signer's certificate subject must contain for a
    /// release to be publishable. Null or blank: publishing is refused.
    /// </summary>
    public string? ExpectedSignerSubject { get; init; }

    /// <summary>Whether a publisher has been configured at all.</summary>
    public bool IsSignerConfigured => !string.IsNullOrWhiteSpace(ExpectedSignerSubject);
}
