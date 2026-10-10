using System.Text.Json;
using Nachos.Abstractions;
using Nachos.Abstractions.Contracts;

namespace Nachos.Core.Validation;

internal static class MessageEnvelopeReader
{
    private static readonly string[] RequiredFields = ["content", "peer_id"];
    public static MessageCreate[] Read(JsonElement requestBody, JsonSerializerOptions options)
    {
        if (requestBody.ValueKind != JsonValueKind.Object ||
            !requestBody.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            throw Invalid(["body", "messages"], "Messages must be an array.", "list_type");
        }

        var index = 0;
        foreach (var message in messages.EnumerateArray())
        {
            ValidateMessage(message, index++);
        }

        try
        {
            return messages.Deserialize<MessageCreate[]>(options)!;
        }
        catch (JsonException error)
        {
            throw new RequestValidationException(
                [new ValidationError(MessageJsonErrorLocation.Find(messages, error),
                    "A message contains invalid JSON data.", "value_error")], error);
        }
    }

    private static void ValidateMessage(JsonElement message, int index)
    {
        object[] location = ["body", "messages", index];
        RequireKind(message, JsonValueKind.Object, location, "model_type");
        foreach (var required in RequiredFields)
        {
            if (!message.TryGetProperty(required, out _))
                throw Invalid([.. location, required], "Field required.", "missing");
        }

        // Non-null type errors fail even when overwritten. An earlier null may be replaced by a valid final string.
        foreach (var property in message.EnumerateObject())
        {
            object[] field = [.. location, property.Name];
            switch (property.Name)
            {
                case "content" when property.Value.ValueKind != JsonValueKind.Null:
                case "peer_id" when property.Value.ValueKind != JsonValueKind.Null:
                    RequireKind(property.Value, JsonValueKind.String, field, "string_type");
                    break;
                case "metadata" when property.Value.ValueKind != JsonValueKind.Null:
                    RequireKind(property.Value, JsonValueKind.Object, field, "dict_type");
                    break;
                case "configuration" when property.Value.ValueKind != JsonValueKind.Null:
                    ValidateConfiguration(property.Value, field);
                    break;
                case "created_at" when property.Value.ValueKind != JsonValueKind.Null:
                    RequireKind(property.Value, JsonValueKind.String, field, "string_type");
                    if (!property.Value.TryGetDateTimeOffset(out _))
                        throw Invalid(field, "Input must be a valid datetime.", "datetime_parsing");
                    break;
            }
        }

        foreach (var required in RequiredFields)
            RequireKind(message.GetProperty(required), JsonValueKind.String, [.. location, required], "string_type");
    }

    private static void ValidateConfiguration(JsonElement configuration, object[] location)
    {
        RequireKind(configuration, JsonValueKind.Object, location, "model_type");
        foreach (var property in configuration.EnumerateObject())
        {
            if (property.Name != "reasoning" || property.Value.ValueKind == JsonValueKind.Null) continue;
            object[] reasoning = [.. location, "reasoning"];
            RequireKind(property.Value, JsonValueKind.Object, reasoning, "model_type");
            foreach (var instruction in property.Value.EnumerateObject())
            {
                if (instruction.Value.ValueKind == JsonValueKind.Null) continue;
                object[] field = [.. reasoning, instruction.Name];
                if (instruction.Name == "enabled" &&
                    instruction.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Invalid(field, "Input must be a boolean.", "bool_type");
                if (instruction.Name == "custom_instructions")
                    RequireKind(instruction.Value, JsonValueKind.String, field, "string_type");
            }
        }
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind, object[] location, string code)
    {
        if (value.ValueKind != kind)
            throw Invalid(location, $"Input must be a JSON {kind.ToString().ToLowerInvariant()}.", code);
    }

    private static RequestValidationException Invalid(object[] location, string message, string code) =>
        new([new ValidationError(location, message, code)]);
}
