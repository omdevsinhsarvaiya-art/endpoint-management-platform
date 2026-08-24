using System.Text.Json;
using EndpointPlatform.Domain.Tasks;

namespace EndpointPlatform.Domain.Tests.Tasks;

/// <summary>
/// Pins the payload wire format to what the deployed agents parse.
/// </summary>
/// <remarks>
/// The server can be redeployed in a minute; the agents on two hundred
/// endpoints cannot. Their executors read payload fields with
/// <c>JsonDocument</c> — camelCase names, and enum values as strings — so the
/// serialization here is a compatibility contract with binaries already in the
/// field, not a style choice. This suite exists because the ServiceAction enum
/// shipped serialised as a number, every deployed agent rejected it as a
/// malformed payload, and nothing failed until a live task hit a real machine.
/// </remarks>
public sealed class TaskPayloadWireFormatTests
{
    /// <summary>The exact options DeviceTaskService serialises payloads with.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, Options);

    /// <summary>Reads a property the way the agent executors do.</summary>
    private static string AgentReadString(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(property).GetString() ?? "";
    }

    [Theory]
    [InlineData(TaskPayloads.ServiceAction.Start, "Start")]
    [InlineData(TaskPayloads.ServiceAction.Stop, "Stop")]
    [InlineData(TaskPayloads.ServiceAction.Restart, "Restart")]
    public void Service_action_travels_as_the_string_the_agent_switches_on(
        TaskPayloads.ServiceAction action, string expected)
    {
        var json = Serialize(new TaskPayloads.ControlService("Spooler", action));

        // GetString() throws on a JSON number — this call IS the agent's parse.
        AgentReadString(json, "action").ShouldBe(expected);
        AgentReadString(json, "serviceName").ShouldBe("Spooler");
    }

    [Fact]
    public void Terminate_process_payload_matches_the_agent_field_names()
    {
        var json = Serialize(new TaskPayloads.TerminateProcess(4242, "notepad.exe"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("processId").GetInt32().ShouldBe(4242);
        doc.RootElement.GetProperty("expectedImageName").GetString().ShouldBe("notepad.exe");
    }

    [Fact]
    public void Update_agent_payload_matches_the_agent_field_names()
    {
        var releaseId = Guid.CreateVersion7();
        var json = Serialize(new TaskPayloads.UpdateAgent(releaseId, "1.1.0", new string('a', 64)));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("releaseId").GetGuid().ShouldBe(releaseId);
        doc.RootElement.GetProperty("version").GetString().ShouldBe("1.1.0");
        doc.RootElement.GetProperty("sha256").GetString().ShouldBe(new string('a', 64));
    }

    [Fact]
    public void Restart_payload_matches_the_agent_field_names()
    {
        var json = Serialize(new TaskPayloads.RestartOrShutdown(30, "maintenance"));

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("graceSeconds").GetInt32().ShouldBe(30);
        doc.RootElement.GetProperty("message").GetString().ShouldBe("maintenance");
    }
}
