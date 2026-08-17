using EndpointPlatform.Infrastructure.Persistence;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

public sealed class SnakeCaseNamingConventionTests
{
    [Theory]
    [InlineData("Id", "id")]
    [InlineData("Name", "name")]
    [InlineData("OrganizationId", "organization_id")]
    [InlineData("NormalizedEmail", "normalized_email")]
    [InlineData("IsHighRisk", "is_high_risk")]
    [InlineData("FailedSignInCount", "failed_sign_in_count")]
    [InlineData("AuditLogEntries", "audit_log_entries")]
    [InlineData("PlatformUserRoles", "platform_user_roles")]
    public void Converts_pascal_case_to_snake_case(string input, string expected)
    {
        SnakeCaseNamingConvention.ToSnakeCase(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("SourceIP", "source_ip")]
    [InlineData("IPAddress", "ip_address")]
    [InlineData("UserAgentUA", "user_agent_ua")]
    public void Treats_an_acronym_run_as_a_single_word(string input, string expected)
    {
        // Naive per-capital splitting would give "source_i_p", which is unreadable
        // and would make hand-written SQL differ from what everyone expects.
        SnakeCaseNamingConvention.ToSnakeCase(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("already_snake", "already_snake")]
    [InlineData("id", "id")]
    [InlineData("", "")]
    public void Leaves_names_that_are_already_snake_case_unchanged(string input, string expected)
    {
        SnakeCaseNamingConvention.ToSnakeCase(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Address1", "address1")]
    [InlineData("Line2Text", "line2_text")]
    public void Handles_digits_without_inserting_spurious_separators(string input, string expected)
    {
        SnakeCaseNamingConvention.ToSnakeCase(input).ShouldBe(expected);
    }

    [Fact]
    public void Never_produces_a_double_underscore()
    {
        string[] samples = ["Already_Snake", "Mixed_CaseName", "A_BCd"];

        foreach (var sample in samples)
        {
            SnakeCaseNamingConvention.ToSnakeCase(sample).ShouldNotContain("__");
        }
    }
}
