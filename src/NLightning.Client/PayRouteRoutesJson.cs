using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NLightning.Client;

/// <summary>
/// The <c>--routes</c> input of <c>payroute</c> (NL-1082): a JSON array of routes, camelCase keys read
/// case-insensitively, from a file or standard input (<c>-</c>). Every validation error names the route (and hop) it
/// rejects, 1-based like the daemon's own route errors, and the offending field.
/// </summary>
internal static class PayRouteRoutesJson
{
    /// <summary>
    /// Parses and validates the routes JSON.
    /// </summary>
    /// <returns>The routes, or null with <paramref name="error"/> set.</returns>
    internal static IReadOnlyList<PayRouteRouteArguments>? Parse(string json, out string? error)
    {
        List<PayRouteRouteJson>? routes;
        try
        {
            routes = JsonSerializer.Deserialize(json, PayRouteRoutesJsonContext.Default.ListPayRouteRouteJson);
        }
        catch (JsonException e)
        {
            error = $"the routes are not valid JSON: {e.Message}";
            return null;
        }

        if (routes is not { Count: > 0 })
        {
            error = "the routes are an empty set: give one route per payment part, our peer first and the payee last.";
            return null;
        }

        if (routes.Count > ClientApp.MaxPayParts)
        {
            error = $"{routes.Count} routes: at most {ClientApp.MaxPayParts} (the payment part limit).";
            return null;
        }

        var parsed = new List<PayRouteRouteArguments>(routes.Count);
        for (var i = 0; i < routes.Count; i++)
        {
            if (ParseRoute(routes[i], i, out error) is not { } route)
                return null;

            parsed.Add(route);
        }

        error = null;
        return parsed;
    }

    /// <summary>
    /// Reads the routes from <paramref name="path"/> (a file, or <c>-</c> for standard input via
    /// <paramref name="stdin"/>) and parses them.
    /// </summary>
    /// <exception cref="ArgumentException">The input is not a valid route set.</exception>
    internal static async Task<IReadOnlyList<PayRouteRouteArguments>> ReadAsync(string path, TextReader stdin,
                                                                               CancellationToken ct)
    {
        var json = string.Equals(path, "-", StringComparison.Ordinal)
                       ? await stdin.ReadToEndAsync(ct)
                       : await File.ReadAllTextAsync(path, ct);
        return Parse(json, out var error) ?? throw new ArgumentException(error);
    }

    /// <summary>
    /// Checks one route of the set: the first-hop channel, what that HTLC carries, and the hops after ours.
    /// </summary>
    /// <returns>The route, or null with <paramref name="error"/> set.</returns>
    private static PayRouteRouteArguments? ParseRoute(PayRouteRouteJson route, int index, out string? error)
    {
        var name = $"route {index + 1}";
        if (string.IsNullOrWhiteSpace(route.FirstHopChannel)
         || !(ClientApp.TryParseChannelId(route.FirstHopChannel, out _)
           || ClientApp.TryParseShortChannelId(route.FirstHopChannel, out _)))
        {
            error = $"{name}: firstHopChannel '{route.FirstHopChannel}' is neither a channel id (64 hex characters) "
                  + "nor a short channel id (BLOCKxTXxOUTPUT).";
            return null;
        }

        if (route.FirstHopAmountMsat is not > 0)
        {
            error = $"{name}: firstHopAmountMsat {route.FirstHopAmountMsat} must be a positive number of msat.";
            return null;
        }

        if (route.FirstHopCltv is not > 0)
        {
            error = $"{name}: firstHopCltv {route.FirstHopCltv} must be a positive block height.";
            return null;
        }

        if (route.Hops is not { Count: > 0 })
        {
            error = $"{name}: hops is empty; a route has our peer first and the payee last.";
            return null;
        }

        var hops = new List<PayRouteHopArguments>(route.Hops.Count);
        for (var i = 0; i < route.Hops.Count; i++)
        {
            if (ParseHop(route.Hops[i], name, i, i == route.Hops.Count - 1, out error) is not { } hop)
                return null;

            hops.Add(hop);
        }

        error = null;
        return new PayRouteRouteArguments(route.FirstHopChannel, route.FirstHopAmountMsat.Value,
                                          route.FirstHopCltv.Value, hops);
    }

    /// <summary>
    /// Checks one hop: the node, the channel it forwards over (only the payee's final hop omits it), the amount it
    /// forwards onward and that HTLC's absolute <c>outgoing_cltv_value</c>.
    /// </summary>
    /// <returns>The hop, or null with <paramref name="error"/> set.</returns>
    private static PayRouteHopArguments? ParseHop(PayRouteHopJson hop, string routeName, int index, bool isFinal,
                                                  out string? error)
    {
        var name = $"{routeName}, hop {index + 1}";
        if (!ClientApp.TryParseNodeId(hop.NodeId ?? string.Empty, out var nodeId))
        {
            error = $"{name}: nodeId '{hop.NodeId}' is not a node id (66 hex characters).";
            return null;
        }

        if (isFinal && hop.OutgoingShortChannelId is not null)
        {
            error = $"{name} is the payee's final hop and omits outgoingShortChannelId "
                  + $"({hop.OutgoingShortChannelId.Text} given).";
            return null;
        }

        if (!isFinal && hop.OutgoingShortChannelId is null)
        {
            error = $"{name}: outgoingShortChannelId is missing; only the payee's final hop omits it.";
            return null;
        }

        if (hop.OutgoingShortChannelId is { Value: null } malformed)
        {
            error = $"{name}: outgoingShortChannelId '{malformed.Text}' is neither a short channel id "
                  + "(BLOCKxTXxOUTPUT) nor its 64-bit number.";
            return null;
        }

        if (hop.AmountToForwardMsat is not > 0)
        {
            error = $"{name}: amountToForwardMsat {hop.AmountToForwardMsat} must be a positive number of msat.";
            return null;
        }

        if (hop.OutgoingCltvValue is not > 0)
        {
            error = $"{name}: outgoingCltvValue {hop.OutgoingCltvValue} must be a positive block height.";
            return null;
        }

        error = null;
        return new PayRouteHopArguments(nodeId, hop.OutgoingShortChannelId?.Value, hop.AmountToForwardMsat.Value,
                                        hop.OutgoingCltvValue.Value);
    }
}

/// <summary>One route of the <c>payroute</c> routes JSON, before validation.</summary>
internal sealed class PayRouteRouteJson
{
    public string? FirstHopChannel { get; set; }

    public ulong? FirstHopAmountMsat { get; set; }

    public uint? FirstHopCltv { get; set; }

    public List<PayRouteHopJson>? Hops { get; set; }
}

/// <summary>One hop of the <c>payroute</c> routes JSON, before validation.</summary>
internal sealed class PayRouteHopJson
{
    public string? NodeId { get; set; }

    [JsonConverter(typeof(PayRouteShortChannelIdJsonConverter))]
    public PayRouteShortChannelIdJson? OutgoingShortChannelId { get; set; }

    public ulong? AmountToForwardMsat { get; set; }

    public uint? OutgoingCltvValue { get; set; }
}

/// <summary>
/// A hop's <c>outgoingShortChannelId</c> as given (NL-1085): <see cref="Text"/> is the JSON value, <see cref="Value"/>
/// the short channel id, or null when the value is neither form.
/// </summary>
internal sealed record PayRouteShortChannelIdJson(string Text, ulong? Value);

/// <summary>
/// Reads a short channel id as the 64-bit JSON number or as a <c>BLOCKxTXxOUTPUT</c> string (the form every other CLI
/// command and <c>getroute --json</c> use). A malformed value is kept with a null <see cref="PayRouteShortChannelIdJson.Value"/>
/// so the validation can name its route and hop; any other JSON type is not a short channel id at all.
/// </summary>
internal sealed class PayRouteShortChannelIdJsonConverter : JsonConverter<PayRouteShortChannelIdJson>
{
    public override PayRouteShortChannelIdJson Read(ref Utf8JsonReader reader, Type typeToConvert,
                                                     JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                var number = System.Text.Encoding.UTF8.GetString(reader.HasValueSequence
                                                                     ? reader.ValueSequence.ToArray()
                                                                     : reader.ValueSpan);
                return new PayRouteShortChannelIdJson(number,
                                                      reader.TryGetUInt64(out var value) ? value : null);
            case JsonTokenType.String:
                var text = reader.GetString() ?? string.Empty;
                return new PayRouteShortChannelIdJson(text,
                                                      ClientApp.TryParseShortChannelId(text, out var scid)
                                                          ? scid
                                                          : null);
            default:
                throw new JsonException(
                    $"outgoingShortChannelId must be a BLOCKxTXxOUTPUT string or a number, not {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, PayRouteShortChannelIdJson value,
                               JsonSerializerOptions options) =>
        throw new NotSupportedException("The routes JSON is only read.");
}

/// <summary>
/// The source-generated JSON contract of the <c>payroute</c> routes file (reflection-free, for NativeAOT), with the
/// camelCase keys read case-insensitively.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<PayRouteRouteJson>))]
internal partial class PayRouteRoutesJsonContext : JsonSerializerContext;