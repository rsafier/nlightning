using System.Text;
using System.Text.Json;

namespace NLightning.Domain.Accounting.Services;

/// <summary>
/// The stored form of an accounting event's details: a flat JSON object of strings, keys in ordinal order.
/// </summary>
/// <remarks>Written and read with <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/> only (no reflection), so
/// it works in the AOT build.</remarks>
public static class AccountingDetailsCodec
{
    public static string? Encode(IReadOnlyDictionary<string, string> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.Count == 0)
            return null;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in details.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                writer.WriteString(key, value);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <exception cref="FormatException">The text is not a flat JSON object of strings.</exception>
    public static IReadOnlyDictionary<string, string> Decode(string? json)
    {
        var details = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json))
            return details;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new FormatException("Accounting details must be a JSON object");

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new FormatException($"Accounting detail '{property.Name}' is not a string");

                details[property.Name] = property.Value.GetString()!;
            }
        }
        catch (JsonException e)
        {
            throw new FormatException("Accounting details are not valid JSON", e);
        }

        return details;
    }

    /// <summary>A sorted details dictionary from pairs (later pairs win; null values are skipped).</summary>
    public static IReadOnlyDictionary<string, string> Create(params (string Key, string? Value)[] pairs)
    {
        var details = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
        {
            if (value is not null)
                details[key] = value;
        }

        return details;
    }
}