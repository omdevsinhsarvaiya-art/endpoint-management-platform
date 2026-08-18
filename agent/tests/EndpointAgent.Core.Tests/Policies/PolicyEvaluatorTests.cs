using EndpointAgent.Core.Abstractions;
using EndpointAgent.Core.Policies;
using EndpointPlatform.Contracts.Agent;
using Microsoft.Extensions.Logging.Abstractions;

namespace EndpointAgent.Core.Tests.Policies;

public sealed class PolicyEvaluatorTests
{
    private sealed class FakeReader(int? seconds) : IScreenLockPolicyReader
    {
        public ValueTask<int?> GetScreenLockTimeoutSecondsAsync(CancellationToken c = default) =>
            ValueTask.FromResult(seconds);
    }

    private static AgentPolicy Policy(int maxSeconds) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "ScreenLockTimeout",
            $$"""{"maxTimeoutSeconds":{{maxSeconds}}}""");

    private static PolicyEvaluator Evaluator(int? actual) =>
        new(new FakeReader(actual), NullLogger<PolicyEvaluator>.Instance);

    [Fact]
    public async Task Compliant_when_actual_within_the_limit()
    {
        var result = await Evaluator(300).EvaluateAsync(Policy(600));
        result.State.ShouldBe("Compliant");
        result.Deviations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Non_compliant_when_actual_exceeds_the_limit()
    {
        var result = await Evaluator(900).EvaluateAsync(Policy(600));
        result.State.ShouldBe("NonCompliant");
        result.Deviations.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Unknown_when_the_timeout_cannot_be_read()
    {
        (await Evaluator(null).EvaluateAsync(Policy(600))).State.ShouldBe("Unknown");
    }

    [Fact]
    public async Task Non_compliant_when_the_screen_never_locks()
    {
        var result = await Evaluator(0).EvaluateAsync(Policy(600));
        result.State.ShouldBe("NonCompliant");
        result.Deviations.Single().ShouldContain("never");
    }

    [Fact]
    public async Task An_unknown_policy_type_evaluates_to_unknown()
    {
        var policy = new AgentPolicy(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, "MysteryPolicy", "{}");
        (await Evaluator(300).EvaluateAsync(policy)).State.ShouldBe("Unknown");
    }
}
