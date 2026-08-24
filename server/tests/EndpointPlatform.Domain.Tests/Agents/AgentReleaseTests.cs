using EndpointPlatform.Domain.Agents;

namespace EndpointPlatform.Domain.Tests.Agents;

public sealed class AgentVersionNumberTests
{
    [Theory]
    [InlineData("1.0.10", "1.0.9")]   // the lexicographic trap
    [InlineData("1.1.0", "1.0.9")]
    [InlineData("2.0.0", "1.9.9")]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("10.0.0", "9.99.99")]
    public void Newer_versions_order_numerically_not_lexicographically(string newer, string older)
    {
        AgentVersionNumber.IsNewer(newer, older).ShouldBeTrue();
        AgentVersionNumber.IsNewer(older, newer).ShouldBeFalse();
    }

    [Fact]
    public void A_version_is_never_newer_than_itself()
    {
        AgentVersionNumber.IsNewer("1.1.0", "1.1.0").ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.x")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.-1")]
    [InlineData("1.0.+1")]
    public void Garbage_is_never_newer_in_either_direction(string? garbage)
    {
        // An update decision based on a version nobody can read fails closed.
        AgentVersionNumber.IsNewer(garbage, "1.0.0").ShouldBeFalse();
        AgentVersionNumber.IsNewer("99.0.0", garbage).ShouldBeFalse();
    }

    [Fact]
    public void Normalize_trims_and_canonicalises()
    {
        AgentVersionNumber.Normalize(" 1.2.3 ").ShouldBe("1.2.3");
        Should.Throw<ArgumentException>(() => AgentVersionNumber.Normalize("1.2"));
    }
}

public sealed class AgentReleaseTests
{
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static AgentRelease NewRelease(string? signer = null) => new(
        "1.1.0", "Windows", "X64", "EndpointPlatformAgent-1.1.0-x64.msi",
        new string('a', 64), signer, "notes", contentSizeBytes: 1234, Actor, "admin@test");

    [Fact]
    public void A_new_release_is_a_draft_with_normalised_target()
    {
        var release = NewRelease();

        release.Status.ShouldBe(AgentReleaseStatus.Draft);
        release.Platform.ShouldBe("windows");
        release.Architecture.ShouldBe("x64");
        release.IsPublished.ShouldBeFalse();
    }

    [Fact]
    public void Lifecycle_is_one_way_draft_publish_revoke()
    {
        var release = NewRelease();

        release.Publish(Now);
        release.IsPublished.ShouldBeTrue();
        release.PublishedAt.ShouldBe(Now);

        release.Revoke(Now.AddHours(1));
        release.Status.ShouldBe(AgentReleaseStatus.Revoked);

        // A revoked build never comes back; publish a fresh row instead.
        Should.Throw<InvalidOperationException>(() => release.Publish(Now.AddHours(2)));
    }

    [Fact]
    public void A_published_release_cannot_be_published_again()
    {
        var release = NewRelease();
        release.Publish(Now);

        Should.Throw<InvalidOperationException>(() => release.Publish(Now));
    }

    [Fact]
    public void Revoking_twice_is_idempotent()
    {
        var release = NewRelease();
        release.Revoke(Now);
        release.Revoke(Now.AddMinutes(1));

        release.Status.ShouldBe(AgentReleaseStatus.Revoked);
        release.RevokedAt.ShouldBe(Now);
    }

    [Theory]
    [InlineData("..\\evil.msi")]
    [InlineData("dir/evil.msi")]
    [InlineData("con:evil.msi")]
    [InlineData("evil\r\n.msi")]
    public void A_file_name_with_path_or_header_characters_is_rejected(string name)
    {
        Should.Throw<ArgumentException>(() => new AgentRelease(
            "1.1.0", "windows", "x64", name, new string('a', 64), null, null, 1, Actor, "a@b"));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public void A_malformed_hash_is_rejected(string sha)
    {
        Should.Throw<ArgumentException>(() => new AgentRelease(
            "1.1.0", "windows", "x64", "a.msi", sha, null, null, 1, Actor, "a@b"));
    }

    [Fact]
    public void Null_signer_is_recorded_as_deliberately_unsigned()
    {
        // Null is a statement, not an omission: the release row says in the open
        // that this build carries no signature to pin.
        NewRelease(signer: null).SignerSubject.ShouldBeNull();
        NewRelease(signer: "CN=Endpoint Platform").SignerSubject.ShouldBe("CN=Endpoint Platform");
    }
}
