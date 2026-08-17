using EndpointPlatform.Infrastructure.Hosting;

namespace EndpointPlatform.Infrastructure.Tests.Hosting;

/// <summary>
/// Covers the validation applied to a client-supplied correlation id.
/// </summary>
/// <remarks>
/// The value is echoed into a response header and pushed into the log context, so
/// accepting arbitrary client input here would be a response-splitting and
/// log-forging vulnerability. These cases cannot be exercised through an HTTP
/// client because <c>HttpClient</c> rejects CRLF in a header value before it
/// leaves the process, which is exactly why they are unit tests.
/// </remarks>
public sealed class CorrelationIdMiddlewareTests
{
    [Theory]
    [InlineData("simple")]
    [InlineData("my-trace-123")]
    [InlineData("trace_id.42")]
    [InlineData("0HNNSLNI0UFAH")]
    [InlineData("a1B2c3")]
    public void Accepts_well_formed_identifiers(string value)
    {
        CorrelationIdMiddleware.IsAcceptable(value).ShouldBeTrue();
    }

    [Theory]
    [InlineData("abc\r\nX-Injected: pwned")]
    [InlineData("abc\rdef")]
    [InlineData("abc\ndef")]
    public void Rejects_values_containing_crlf_response_splitting_payloads(string value)
    {
        CorrelationIdMiddleware.IsAcceptable(value).ShouldBeFalse(
            "a CR or LF in an echoed header value permits response splitting and log forging");
    }

    [Theory]
    [InlineData("abc\0def")]
    [InlineData("abc\tdef")]
    [InlineData("abcdef")]
    public void Rejects_control_characters(string value)
    {
        CorrelationIdMiddleware.IsAcceptable(value).ShouldBeFalse();
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("quote\"mark")]
    [InlineData("angle<bracket>")]
    [InlineData("percent%41")]
    [InlineData("unicode-é")]
    public void Rejects_characters_outside_the_allowed_set(string value)
    {
        CorrelationIdMiddleware.IsAcceptable(value).ShouldBeFalse();
    }

    [Fact]
    public void Rejects_values_longer_than_the_limit()
    {
        CorrelationIdMiddleware.IsAcceptable(new string('a', CorrelationId.MaxLength)).ShouldBeTrue();
        CorrelationIdMiddleware.IsAcceptable(new string('a', CorrelationId.MaxLength + 1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Rejects_null_and_empty(string? value)
    {
        CorrelationIdMiddleware.IsAcceptable(value).ShouldBeFalse();
    }

    [Fact]
    public void Accessor_generates_an_identifier_when_none_was_set()
    {
        // Background work (seeding, scheduled jobs) has no HTTP request, but its
        // audit entries still need to be traceable.
        var accessor = new CorrelationIdAccessor();

        var first = accessor.CorrelationId;

        first.ShouldNotBeNullOrWhiteSpace();
        accessor.CorrelationId.ShouldBe(first, "the generated id must be stable within one scope");
    }
}
