namespace EndpointPlatform.Contracts.Agent;

/// <summary>
/// Redeems a one-time secret reference issued with a local-account task.
/// </summary>
/// <param name="SecretReference">The reference carried in the task payload.</param>
public sealed record RedeemSecretRequest(string SecretReference);

/// <summary>
/// The redeemed secret. Returned once and only to the device the reference was
/// issued for; the server deletes it atomically on read.
/// </summary>
/// <param name="Secret">Plaintext secret. Never logged, never persisted by the agent.</param>
public sealed record RedeemSecretResponse(string Secret);
