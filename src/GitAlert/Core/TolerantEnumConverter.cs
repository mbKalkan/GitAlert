using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitAlert.Core;

/// <summary>
/// Reads an enum by name, and turns a name it does not know into a fallback instead of an
/// exception.
/// </summary>
/// <remarks>
/// <see cref="JsonStringEnumConverter"/> throws on an unknown name, and one unknown name was the
/// whole file: settings.json set aside as corrupt and every setting back to its default, or the
/// entire alert history gone. A name this build does not know is exactly what a newer build wrote
/// before the user went back a version, and that is not a reason to forget their accounts.
/// </remarks>
public abstract class TolerantEnumConverter<TEnum>(TEnum fallback) : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TryRead(ref reader, out var value) ? value : fallback;

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());

    /// <summary>Reads the token under the reader as a member of the enum, by name or by number.</summary>
    public static bool TryRead(ref Utf8JsonReader reader, out TEnum value)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse(reader.GetString(), ignoreCase: true, out value) && Enum.IsDefined(value);

            case JsonTokenType.Number when reader.TryGetInt32(out var number):
                value = (TEnum)Enum.ToObject(typeof(TEnum), number);
                return Enum.IsDefined(value);

            default:
                value = default;
                return false;
        }
    }
}

/// <summary>An unknown kind of alert is still an alert; it is shown as the generic one.</summary>
public sealed class AlertKindConverter() : TolerantEnumConverter<AlertKind>(AlertKind.Other);

public sealed class AlertSeverityConverter() : TolerantEnumConverter<AlertSeverity>(AlertSeverity.Normal);

/// <summary>
/// A set of alert kinds from which the names this build does not know are dropped, rather than
/// the set - or the file around it - being refused.
/// </summary>
public sealed class AlertKindSetConverter : JsonConverter<HashSet<AlertKind>>
{
    public override HashSet<AlertKind>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            // Left null on purpose: the settings decide what a missing list means.
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return [];
        }

        var kinds = new HashSet<AlertKind>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (TolerantEnumConverter<AlertKind>.TryRead(ref reader, out var kind))
            {
                kinds.Add(kind);
            }
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        return kinds;
    }

    public override void Write(Utf8JsonWriter writer, HashSet<AlertKind> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();

        foreach (var kind in value)
        {
            writer.WriteStringValue(kind.ToString());
        }

        writer.WriteEndArray();
    }
}
