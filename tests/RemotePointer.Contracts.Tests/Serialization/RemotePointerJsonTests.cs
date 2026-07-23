using System.Text.Json;
using RemotePointer.Contracts.Messages;
using RemotePointer.Contracts.Serialization;

namespace RemotePointer.Contracts.Tests.Serialization;

public sealed class RemotePointerJsonTests
{
    [Fact]
    public void Options_UseCamelCasePropertiesAndStringEnums()
    {
        var message = new PointerEventMessage(
            Guid.Parse("a1e59338-aa04-44c6-b23d-8c2f1f5b859c"),
            "session",
            1,
            0.5d,
            0.25d,
            PointerKind.Attention,
            1_000,
            2_000);

        var json = JsonSerializer.Serialize(message, RemotePointerJson.CreateOptions());

        Assert.Contains("\"normalizedX\":0.5", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"attention\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_RejectIntegerEnumValues()
    {
        const string json = """
            {"eventId":"a1e59338-aa04-44c6-b23d-8c2f1f5b859c","sessionId":"session","sequenceNumber":1,"normalizedX":0.5,"normalizedY":0.25,"kind":0,"sentAtUnixMilliseconds":1000,"timeToLiveMilliseconds":2000}
            """;

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<PointerEventMessage>(json, RemotePointerJson.CreateOptions()));
    }

    [Fact]
    public void Options_RejectUnknownMembers()
    {
        const string json = """
            {"eventId":"a1e59338-aa04-44c6-b23d-8c2f1f5b859c","sessionId":"session","sequenceNumber":1,"normalizedX":0.5,"normalizedY":0.25,"kind":"click","sentAtUnixMilliseconds":1000,"timeToLiveMilliseconds":2000,"unexpected":true}
            """;

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<PointerEventMessage>(json, RemotePointerJson.CreateOptions()));
    }
}
