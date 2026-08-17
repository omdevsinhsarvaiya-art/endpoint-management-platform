using EndpointPlatform.Domain.Identity;

namespace EndpointPlatform.Domain.Tests.Identity;

public sealed class OrganizationTests
{
    [Fact]
    public void New_organization_is_active()
    {
        var organization = new Organization("Contoso Ltd", "contoso");

        organization.Name.ShouldBe("Contoso Ltd");
        organization.Slug.ShouldBe("contoso");
        organization.IsActive.ShouldBeTrue();
        organization.Id.ShouldNotBe(Guid.Empty);
    }

    [Theory]
    [InlineData("Contoso", "contoso")]
    [InlineData("  CONTOSO  ", "contoso")]
    [InlineData("acme-corp", "acme-corp")]
    [InlineData("acme_corp", "acme_corp")]
    [InlineData("team01", "team01")]
    public void Slug_is_normalised_to_lowercase(string input, string expected)
    {
        new Organization("Name", input).Slug.ShouldBe(expected);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has.dot")]
    [InlineData("has:colon")]
    [InlineData("../traversal")]
    [InlineData("semi;colon")]
    public void Slug_rejects_characters_that_are_unsafe_in_a_url_or_a_token(string slug)
    {
        // The slug appears in API routes and in enrollment token scopes, so anything
        // that could change how a path or token is parsed is refused at construction.
        Should.Throw<ArgumentException>(() => new Organization("Name", slug));
    }

    [Fact]
    public void Slug_length_is_bounded()
    {
        Should.Throw<ArgumentException>(() => new Organization("Name", new string('a', 65)));
    }

    [Fact]
    public void Name_length_is_bounded()
    {
        Should.Throw<ArgumentException>(() => new Organization(new string('a', 201), "slug"));
    }

    [Fact]
    public void Deactivate_and_activate_toggle_state()
    {
        var organization = new Organization("Contoso", "contoso");

        organization.Deactivate();
        organization.IsActive.ShouldBeFalse();

        organization.Activate();
        organization.IsActive.ShouldBeTrue();
    }
}
