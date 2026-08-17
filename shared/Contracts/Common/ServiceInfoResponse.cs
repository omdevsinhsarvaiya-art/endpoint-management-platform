namespace EndpointPlatform.Contracts.Common;

/// <summary>
/// Non-sensitive identification of a running API instance.
/// </summary>
/// <remarks>
/// Returned by the root endpoint of each API so an operator can confirm which
/// service and which build they have reached. It carries no configuration, no
/// connection details, no hostname and no dependency status - that information
/// belongs behind authentication, and the health endpoints already cover
/// dependency state.
/// </remarks>
/// <param name="Service">Stable service identifier, e.g. <c>admin-api</c>.</param>
/// <param name="Version">Informational assembly version of the running build.</param>
/// <param name="Environment">ASP.NET Core environment name.</param>
public sealed record ServiceInfoResponse(string Service, string Version, string Environment);
