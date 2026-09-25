using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComdirectFetch.Api;

/// <summary>
/// comdirect dokumentiert bookingDate als "$DateString"-Objekt ({"date": "yyyy-MM-dd"}), die
/// echte Antwort der Live-API liefert für AccountTransaction.bookingDate aber einen einfachen
/// String. Dieser Converter akzeptiert defensiv beide Formen, statt sich auf die Doku zu verlassen.
/// </summary>
public sealed class FlexibleDateConverter : JsonConverter<DateOnly?>
{
    public override DateOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return DateOnly.TryParse(reader.GetString(), out var date) ? date : null;
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            string? dateText = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == "date")
                {
                    reader.Read();
                    dateText = reader.GetString();
                }
            }

            return DateOnly.TryParse(dateText, out var date) ? date : null;
        }

        throw new JsonException($"Unerwartetes JSON-Token für ein Datum: {reader.TokenType}.");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
    {
        if (value is { } date)
        {
            writer.WriteStringValue(date.ToString("yyyy-MM-dd"));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
