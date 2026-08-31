using System.Text.Json;
using System.Text.Json.Serialization;

namespace EndpointPlatform.Domain.BitLocker;

/// <summary>Why an offered envelope was refused.</summary>
public enum SealedEnvelopeError
{
    None = 0,
    Missing = 1,
    NotJson = 2,
    UnknownScheme = 3,
    MissingField = 4,
    NotBase64 = 5,
    WrongSize = 6,
    BadFingerprint = 7,
    TooLarge = 8,
}

/// <summary>
/// Structural validation of a sealed recovery envelope, without opening it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here decrypts, and nothing here can.</b> This type runs in the Agent
/// API, which holds no key capable of unwrapping an envelope; it exists so that
/// process can still refuse obvious rubbish before storing it. What it checks is
/// shape: the scheme it claims, that every component is base64, and that each one
/// is the exact size the algorithms produce. An envelope that passes is
/// well-formed, not proven decryptable -- only the Admin API can establish that,
/// and only at reveal time.
/// </para>
/// <para>
/// The size checks are worth stating plainly because they are what stops this
/// endpoint being used as a general-purpose blob store by anything holding a device
/// credential. An RSA-3072 wrapped key is exactly 384 bytes, a GCM nonce exactly
/// 12, a tag exactly 16, and the ciphertext of a 48-digit recovery password is a
/// little over fifty. Anything else is not a sealed recovery password whatever it
/// claims to be.
/// </para>
/// <para>
/// <b>A plaintext recovery password cannot pass.</b> It is not JSON, so it fails at
/// the first gate -- and the validated shape has nowhere to put one even if it
/// were.
/// </para>
/// </remarks>
public sealed record SealedRecoveryEnvelope(
    [property: JsonPropertyName("scheme")] string? Scheme,
    [property: JsonPropertyName("wrappedKey")] string? WrappedKey,
    [property: JsonPropertyName("nonce")] string? Nonce,
    [property: JsonPropertyName("tag")] string? Tag,
    [property: JsonPropertyName("ciphertext")] string? Ciphertext,
    [property: JsonPropertyName("keyFingerprint")] string? KeyFingerprint)
{
    /// <summary>RSA-3072 output. Exact, not a maximum.</summary>
    public const int WrappedKeyBytes = 384;

    public const int NonceBytes = 12;
    public const int TagBytes = 16;

    /// <summary>
    /// A recovery password is 55 characters. The window allows for a differently
    /// padded future encoding without admitting an arbitrary payload.
    /// </summary>
    public const int MinCiphertextBytes = 16;

    public const int MaxCiphertextBytes = 256;

    public const int FingerprintLength = 64;

    /// <summary>
    /// Column limit for the serialised envelope. Checked before parsing so an
    /// oversized body is rejected without being examined.
    /// </summary>
    public const int MaxSerialisedLength = 4096;

    /// <summary>
    /// Validates <paramref name="json"/> as a sealed envelope.
    /// </summary>
    /// <remarks>
    /// Returns a reason rather than throwing. A parse failure here is an ordinary
    /// outcome -- a malformed or hostile request -- and an exception carrying the
    /// offending body would be a poor thing to have on a path that handles sealed
    /// credentials.
    /// </remarks>
    public static SealedEnvelopeError Validate(string? json, out SealedRecoveryEnvelope? envelope)
    {
        envelope = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return SealedEnvelopeError.Missing;
        }

        if (json.Length > MaxSerialisedLength)
        {
            return SealedEnvelopeError.TooLarge;
        }

        SealedRecoveryEnvelope? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SealedRecoveryEnvelope>(json);
        }
        catch (JsonException)
        {
            // Where a plaintext recovery password lands: it is not JSON.
            return SealedEnvelopeError.NotJson;
        }

        if (parsed is null)
        {
            return SealedEnvelopeError.NotJson;
        }

        if (parsed.Scheme != BitLockerSealScheme.HybridRsaV1)
        {
            // Only the endpoint-sealed scheme is accepted here. A row claiming the
            // symmetric scheme would assert that this process sealed it, which it
            // cannot have done -- it holds no key.
            return SealedEnvelopeError.UnknownScheme;
        }

        if (string.IsNullOrWhiteSpace(parsed.WrappedKey)
            || string.IsNullOrWhiteSpace(parsed.Nonce)
            || string.IsNullOrWhiteSpace(parsed.Tag)
            || string.IsNullOrWhiteSpace(parsed.Ciphertext)
            || string.IsNullOrWhiteSpace(parsed.KeyFingerprint))
        {
            return SealedEnvelopeError.MissingField;
        }

        if (!IsHexFingerprint(parsed.KeyFingerprint))
        {
            return SealedEnvelopeError.BadFingerprint;
        }

        if (!TryDecode(parsed.WrappedKey, out var wrapped)
            || !TryDecode(parsed.Nonce, out var nonce)
            || !TryDecode(parsed.Tag, out var tag)
            || !TryDecode(parsed.Ciphertext, out var ciphertext))
        {
            return SealedEnvelopeError.NotBase64;
        }

        if (wrapped != WrappedKeyBytes
            || nonce != NonceBytes
            || tag != TagBytes
            || ciphertext < MinCiphertextBytes
            || ciphertext > MaxCiphertextBytes)
        {
            return SealedEnvelopeError.WrongSize;
        }

        envelope = parsed;
        return SealedEnvelopeError.None;
    }

    /// <summary>
    /// A message safe to return to an agent. Names the rule, never the value.
    /// </summary>
    public static string Describe(SealedEnvelopeError error) => error switch
    {
        SealedEnvelopeError.None => "The envelope is well formed.",
        SealedEnvelopeError.Missing => "A sealed envelope is required.",
        SealedEnvelopeError.NotJson =>
            "The sealed envelope is not valid JSON. This endpoint accepts only a sealed envelope; "
            + "a recovery password must never be sent to it.",
        SealedEnvelopeError.UnknownScheme =>
            $"Only the '{BitLockerSealScheme.HybridRsaV1}' sealing scheme is accepted here.",
        SealedEnvelopeError.MissingField => "The sealed envelope is missing a required field.",
        SealedEnvelopeError.NotBase64 => "Every envelope component must be base64.",
        SealedEnvelopeError.WrongSize =>
            "An envelope component is not the size the sealing algorithms produce.",
        SealedEnvelopeError.BadFingerprint =>
            "The envelope's key fingerprint is not a hex SHA-256 digest.",
        SealedEnvelopeError.TooLarge => "The sealed envelope is larger than the accepted maximum.",
        _ => "The sealed envelope could not be validated.",
    };

    private static bool IsHexFingerprint(string value) =>
        value.Length == FingerprintLength && value.All(Uri.IsHexDigit);

    /// <summary>Decoded length, or false when the value is not base64.</summary>
    private static bool TryDecode(string value, out int length)
    {
        length = 0;

        // Sized from the encoded length rather than allocating on a hostile input.
        var buffer = new byte[(value.Length / 4 * 3) + 3];

        if (!Convert.TryFromBase64String(value, buffer, out var written))
        {
            return false;
        }

        length = written;
        return true;
    }
}
