using System.Text.Json;
using System.Text.Json.Serialization;
using BlueRelay.Models;

namespace BlueRelay.Persistence;

/// <summary>
/// Keeps legacy Prompt/Result readable while omitting their duplicated body from
/// new state files when a RelayPayload reference is present.
/// </summary>
public sealed class RelayTaskJsonConverter : JsonConverter<RelayTask>
{
    public override RelayTask Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var task = new RelayTask
        {
            Id = ReadGuid(root, "Id") ?? Guid.NewGuid(),
            WorkstreamId = ReadGuid(root, "WorkstreamId") ?? Guid.Empty,
            Prompt = ReadString(root, "Prompt") ?? string.Empty,
            Result = ReadString(root, "Result"),
            SourceTabKey = ReadString(root, "SourceTabKey") ?? string.Empty,
            SourceTabId = ReadString(root, "SourceTabId"),
            SourceChatGPTUrl = ReadString(root, "SourceChatGPTUrl"),
            CapturedAt = ReadDateTimeOffset(root, "CapturedAt") ?? DateTimeOffset.UtcNow,
            UpdatedAt = ReadDateTimeOffset(root, "UpdatedAt") ?? DateTimeOffset.UtcNow,
            Status = ReadEnum(root, "Status", RelayTaskStatus.Captured),
            DeliveryStatus = ReadEnum(root, "DeliveryStatus", RelayCommandDeliveryStatus.None),
            DeliveryErrorCode = ReadString(root, "DeliveryErrorCode"),
            UserNote = ReadString(root, "UserNote"),
            ResultNote = ReadString(root, "ResultNote"),
            CodexTurnId = ReadString(root, "CodexTurnId"),
            CodexError = ReadString(root, "CodexError")
        };

        if (TryGet(root, "Payload", out var payload) && payload.ValueKind is not JsonValueKind.Null)
        {
            task.Payload = JsonSerializer.Deserialize<RelayPayload>(payload.GetRawText(), options);
        }

        if (TryGet(root, "ResultPayload", out var resultPayload) && resultPayload.ValueKind is not JsonValueKind.Null)
        {
            task.ResultPayload = JsonSerializer.Deserialize<RelayPayload>(resultPayload.GetRawText(), options);
        }

        return task;
    }

    public override void Write(Utf8JsonWriter writer, RelayTask value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Id", value.Id);
        writer.WriteString("WorkstreamId", value.WorkstreamId);
        if (value.Payload is null)
        {
            writer.WriteString("Prompt", value.Prompt);
        }

        if (value.ResultPayload is null)
        {
            writer.WriteString("Result", value.Result);
        }

        writer.WriteString("SourceTabKey", value.SourceTabKey);
        writer.WriteString("SourceTabId", value.SourceTabId);
        writer.WriteString("SourceChatGPTUrl", value.SourceChatGPTUrl);
        writer.WriteString("CapturedAt", value.CapturedAt);
        writer.WriteString("UpdatedAt", value.UpdatedAt);
        writer.WriteString("Status", value.Status.ToString());
        writer.WriteString("DeliveryStatus", value.DeliveryStatus.ToString());
        writer.WriteString("DeliveryErrorCode", value.DeliveryErrorCode);
        writer.WritePropertyName("Payload");
        JsonSerializer.Serialize(writer, value.Payload, options);
        writer.WritePropertyName("ResultPayload");
        JsonSerializer.Serialize(writer, value.ResultPayload, options);
        writer.WriteString("UserNote", value.UserNote);
        writer.WriteString("ResultNote", value.ResultNote);
        writer.WriteString("CodexTurnId", value.CodexTurnId);
        writer.WriteString("CodexError", value.CodexError);
        writer.WriteEndObject();
    }

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return TryGet(root, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static Guid? ReadGuid(JsonElement root, string name)
    {
        var value = ReadString(root, name);
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement root, string name)
    {
        return TryGet(root, name, out var value) && value.ValueKind == JsonValueKind.String &&
               value.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;
    }

    private static T ReadEnum<T>(JsonElement root, string name, T fallback)
        where T : struct, Enum
    {
        var value = ReadString(root, name);
        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
    }
}
