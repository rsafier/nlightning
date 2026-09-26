using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Abcd;
using Daemon.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Utils;

/// <summary>
/// One hop of a route our node's <c>getroute</c> returned, reduced to what the proofs compare with LND.
/// </summary>
/// <param name="ShortChannelId">The channel of the hop (BOLT 7 <c>u64</c>).</param>
/// <param name="NodeIdHex">The node the hop reaches (lowercase hex).</param>
/// <param name="AmountMsat">What the hop carries (the amount the next node forwards or receives).</param>
/// <param name="FeeMsat">The fee charged for the hop, when the response reports it per hop.</param>
/// <param name="CltvExpiry">The hop's CLTV (delta or absolute, as the response reports it).</param>
public sealed record RouteHopView(ulong? ShortChannelId, string? NodeIdHex, ulong? AmountMsat, ulong? FeeMsat,
                                  ulong? CltvExpiry);

/// <summary>
/// A route our node's <c>getroute</c> returned (IPC 19, plan G4-T4), or why it returned none.
/// </summary>
/// <param name="Hops">The hops, first (our channel) first; empty when there is no route.</param>
/// <param name="TotalFeeMsat">The route's total fee, when the response reports it.</param>
/// <param name="Error">Why no route came back (the handler's exception or error text), or null.</param>
/// <param name="Raw">Every property of the response, for the log.</param>
public sealed record RouteView(IReadOnlyList<RouteHopView> Hops, ulong? TotalFeeMsat, string? Error, string Raw)
{
    public bool Found => Error is null && Hops.Count > 0;

    public IReadOnlyList<ulong?> ShortChannelIds => Hops.Select(h => h.ShortChannelId).ToList();

    public override string ToString() =>
        Found
            ? $"{Hops.Count} hops [{string.Join(" -> ", Hops.Select(h => $"{h.ShortChannelId}:{h.NodeIdHex?[..16]}… amt {h.AmountMsat} fee {h.FeeMsat} cltv {h.CltvExpiry}"))}], total fee {TotalFeeMsat}"
            : $"no route ({Error ?? "empty"})";
}

/// <summary>
/// Calls our node's <c>getroute</c> (<c>ClientCommand</c> 19, plan G4-T4, contract
/// <c>GetRouteIpcRequest { destination node id, amount_msat }</c>) through the daemon's client command handler, the
/// same path as <c>nltg getroute &lt;node&gt; &lt;amount_msat&gt;</c>.
/// </summary>
/// <remarks>
/// <para>TODO(G-C integrator): lane C2 adds the request/response types in parallel with this lane (C3), so the call
/// is made by reflection: it looks in the Domain assembly for a <c>GetRoute*ClientRequest</c> and
/// <c>GetRoute*ClientResponse</c>, fills the request's destination (a <c>CompactPubKey</c>, <c>byte[]</c> or hex
/// string property or constructor parameter) and amount (<c>LightningMoney</c> or an integer in msat), and reads the
/// hops from the first list of the response (or of its route property) by property names (short channel id, node id,
/// amount, fee, CLTV). Once the names are known, replace this with a typed call like
/// <see cref="GossipGraphProbe.TryGetOurGraphChannelAsync"/>; the proofs only use <see cref="GetRouteAsync"/> and
/// <see cref="RouteView"/>.</para>
/// <para>A handler that throws (e.g. "no route") is reported as <see cref="RouteView.Error"/>, never rethrown, so a
/// proof can wait for a route to appear or disappear. A build without the handler fails the calling test with a
/// message naming this TODO.</para>
/// </remarks>
public static class GetRouteProbe
{
    private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// <c>getroute <paramref name="destination"/> <paramref name="amountMsat"/></c> on <paramref name="node"/>; logs the
    /// result.
    /// </summary>
    /// <exception cref="InvalidOperationException">This build has no <c>getroute</c> client handler.</exception>
    public static async Task<RouteView> GetRouteAsync(NLightningTestNode node, CompactPubKey destination,
                                                      ulong amountMsat, CancellationToken cancellationToken)
    {
        var (requestType, responseType) = FindContractTypes();
        var request = BuildRequest(requestType, destination, amountMsat);

        using var scope = node.Services.CreateScope();
        var handlerType = typeof(IClientCommandHandler<,>).MakeGenericType(requestType, responseType);
        var handler = scope.ServiceProvider.GetService(handlerType)
                   ?? throw new InvalidOperationException(
                          $"No {handlerType.Name}<{requestType.Name}, {responseType.Name}> is registered "
                        + "(TODO(G-C integrator): getroute, IPC 19)");
        var handle = handlerType.GetMethod("HandleAsync")!;

        RouteView view;
        try
        {
            var task = (Task)handle.Invoke(handler, [request, cancellationToken])!;
            await task;
            var response = task.GetType().GetProperty("Result")!.GetValue(task);
            view = ReadResponse(response);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
            view = new RouteView([], null, $"{inner.GetType().Name}: {inner.Message}", string.Empty);
        }

        Console.WriteLine($"[{node.Name}] getroute {Convert.ToHexStringLower(destination)[..16]}… {amountMsat} msat: "
                        + $"{view}");
        return view;
    }

    /// <summary>
    /// Polls <see cref="GetRouteAsync"/> until <paramref name="accept"/> holds for the route; returns it.
    /// </summary>
    public static Task<RouteView> WaitForRouteAsync(NLightningTestNode node, CompactPubKey destination,
                                                    ulong amountMsat, Func<RouteView, bool> accept, TimeSpan timeout,
                                                    string description, CancellationToken cancellationToken) =>
        Poll.ForAsync(async () =>
        {
            var route = await GetRouteAsync(node, destination, amountMsat, cancellationToken);
            return accept(route) ? route : null;
        }, timeout, description, cancellationToken, GossipGraphProbe.PollInterval);

    private static (Type Request, Type Response) FindContractTypes()
    {
        var domain = typeof(ListGraphChannelsClientRequest).Assembly;
        var types = domain.GetExportedTypes();
        var request = types.FirstOrDefault(t => t.Name.StartsWith("GetRoute", StringComparison.Ordinal)
                                             && t.Name.EndsWith("ClientRequest", StringComparison.Ordinal));
        var response = types.FirstOrDefault(t => t.Name.StartsWith("GetRoute", StringComparison.Ordinal)
                                              && t.Name.EndsWith("ClientResponse", StringComparison.Ordinal));
        if (request is null || response is null)
            throw new InvalidOperationException(
                "This build has no getroute client request/response (GetRoute*ClientRequest/GetRoute*ClientResponse "
              + $"in {domain.GetName().Name}): TODO(G-C integrator), IPC 19 from lane C2");

        return (request, response);
    }

    private static object BuildRequest(Type requestType, CompactPubKey destination, ulong amountMsat)
    {
        var constructor = requestType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var arguments = constructor.GetParameters()
                                   .Select(p => ConvertArgument(p.Name ?? string.Empty, p.ParameterType, destination,
                                                                amountMsat, out var value)
                                                    ? value
                                                    : p.HasDefaultValue
                                                        ? p.DefaultValue
                                                        : DefaultOf(p.ParameterType))
                                   .ToArray();
        var request = constructor.Invoke(arguments);

        // Properties the constructor did not take (init-only setters are settable through reflection)
        foreach (var property in requestType.GetProperties(PublicInstance).Where(p => p.SetMethod is not null))
            if (ConvertArgument(property.Name, property.PropertyType, destination, amountMsat, out var value)
             && IsUnset(property.GetValue(request)))
                property.SetValue(request, value);

        return request;
    }

    private static bool ConvertArgument(string name, Type type, CompactPubKey destination, ulong amountMsat,
                                        out object? value)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        var isDestination = name.Contains("Destination", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("NodeId", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("PubKey", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("Target", StringComparison.OrdinalIgnoreCase)
                         || name.Contains("Payee", StringComparison.OrdinalIgnoreCase);
        var isAmount = name.Contains("Amount", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Amt", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Msat", StringComparison.OrdinalIgnoreCase);
        value = null;
        if (target == typeof(CompactPubKey))
            value = destination;
        else if (target == typeof(LightningMoney) && isAmount)
            value = LightningMoney.MilliSatoshis(amountMsat);
        else if (target == typeof(byte[]) && isDestination)
            value = (byte[])destination;
        else if (target == typeof(string) && isDestination)
            value = Convert.ToHexStringLower(destination);
        else if (isAmount && target == typeof(ulong))
            value = amountMsat;
        else if (isAmount && target == typeof(long))
            value = (long)amountMsat;

        return value is not null;
    }

    private static RouteView ReadResponse(object? response)
    {
        if (response is null)
            return new RouteView([], null, "null response", string.Empty);

        var raw = Describe(response, 0);
        var error = ReadError(response);
        var routeHolder = response;
        var hops = FindHops(response);
        if (hops is null)
        {
            // e.g. { Route: { Hops: [...] } }
            foreach (var property in response.GetType().GetProperties(PublicInstance))
            {
                var value = property.GetValue(response);
                if (value is null || IsScalar(value.GetType()))
                    continue;

                hops = FindHops(value);
                if (hops is null)
                    continue;

                routeHolder = value;
                error ??= ReadError(value);
                break;
            }
        }

        var views = hops?.Cast<object?>().Where(h => h is not null).Select(h => ReadHop(h!)).ToList() ?? [];
        var totalFee = ReadNumber(routeHolder, n => n.Contains("Fee", StringComparison.OrdinalIgnoreCase))
                    ?? (routeHolder == response
                            ? null
                            : ReadNumber(response, n => n.Contains("Fee", StringComparison.OrdinalIgnoreCase)));
        return new RouteView(views, totalFee, views.Count == 0 ? error ?? "no hops" : null, raw);
    }

    private static IEnumerable? FindHops(object holder) =>
        holder.GetType()
              .GetProperties(PublicInstance)
              .Where(p => p.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(p.PropertyType)
                       && !IsBytes(p.PropertyType))
              .Select(p => p.GetValue(holder) as IEnumerable)
              .FirstOrDefault(e => e is not null);

    private static RouteHopView ReadHop(object hop)
    {
        var scid = ReadNumber(hop, n => n.Contains("ShortChannelId", StringComparison.OrdinalIgnoreCase)
                                     || n.Contains("Scid", StringComparison.OrdinalIgnoreCase)
                                     || n.Equals("ChannelId", StringComparison.OrdinalIgnoreCase)
                                     || n.Equals("ChanId", StringComparison.OrdinalIgnoreCase));
        var nodeId = hop.GetType()
                        .GetProperties(PublicInstance)
                        .Where(p => p.Name.Contains("NodeId", StringComparison.OrdinalIgnoreCase)
                                 || p.Name.Contains("PubKey", StringComparison.OrdinalIgnoreCase))
                        .Select(p => p.GetValue(hop) switch
                         {
                             CompactPubKey key => Convert.ToHexStringLower(key),
                             byte[] bytes => Convert.ToHexStringLower(bytes),
                             string text => text.ToLowerInvariant(),
                             _ => null
                         })
                        .FirstOrDefault(v => v is not null);
        var amount = ReadNumber(hop, n => n.Contains("AmountToForward", StringComparison.OrdinalIgnoreCase)
                                       || n.Contains("AmtToForward", StringComparison.OrdinalIgnoreCase))
                  ?? ReadNumber(hop, n => (n.Contains("Amount", StringComparison.OrdinalIgnoreCase)
                                        || n.Contains("Amt", StringComparison.OrdinalIgnoreCase))
                                       && !n.Contains("Fee", StringComparison.OrdinalIgnoreCase));
        var fee = ReadNumber(hop, n => n.Contains("Fee", StringComparison.OrdinalIgnoreCase));
        var cltv = ReadNumber(hop, n => n.Contains("Cltv", StringComparison.OrdinalIgnoreCase)
                                     || n.Contains("Expiry", StringComparison.OrdinalIgnoreCase));
        return new RouteHopView(scid, nodeId, amount, fee, cltv);
    }

    private static ulong? ReadNumber(object holder, Func<string, bool> nameMatches) =>
        holder.GetType()
              .GetProperties(PublicInstance)
              .Where(p => nameMatches(p.Name))
              .Select(p => ToNumber(p.GetValue(holder)))
              .FirstOrDefault(v => v is not null);

    private static ulong? ToNumber(object? value) => value switch
    {
        null => null,
        LightningMoney money => money.MilliSatoshi,
        ShortChannelId scid => scid.ToUInt64(),
        ulong u => u,
        long l when l >= 0 => (ulong)l,
        uint u => u,
        int i when i >= 0 => (ulong)i,
        ushort s => s,
        _ => null
    };

    private static string? ReadError(object holder) =>
        holder.GetType()
              .GetProperties(PublicInstance)
              .Where(p => p.PropertyType == typeof(string)
                       && (p.Name.Contains("Error", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Failure", StringComparison.OrdinalIgnoreCase)
                        || p.Name.Contains("Reason", StringComparison.OrdinalIgnoreCase)))
              .Select(p => p.GetValue(holder) as string)
              .FirstOrDefault(s => !string.IsNullOrEmpty(s));

    private static string Describe(object? value, int depth)
    {
        switch (value)
        {
            case null:
                return "null";
            case CompactPubKey key:
                return Convert.ToHexStringLower(key);
            case byte[] bytes:
                return Convert.ToHexStringLower(bytes);
            case string text:
                return $"\"{text}\"";
            case IEnumerable list when depth < 3:
                return "[" + string.Join(", ", list.Cast<object?>().Select(v => Describe(v, depth + 1))) + "]";
        }

        var type = value.GetType();
        if (IsScalar(type) || depth >= 3)
            return value.ToString() ?? string.Empty;

        return "{" + string.Join(", ", type.GetProperties(PublicInstance)
                                           .Where(p => p.GetIndexParameters().Length == 0)
                                           .Select(p => $"{p.Name}: {Describe(p.GetValue(value), depth + 1)}")) + "}";
    }

    private static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
     || type == typeof(LightningMoney) || type == typeof(ShortChannelId) || type == typeof(CompactPubKey)
     || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || IsBytes(type);

    private static bool IsBytes(Type type) =>
        type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>) || type == typeof(Memory<byte>);

    private static bool IsUnset(object? value) => value switch
    {
        null => true,
        LightningMoney money => money.MilliSatoshi == 0,
        // A struct: a default key cannot be told apart safely, and the destination is the only key the request takes
        CompactPubKey => true,
        ulong u => u == 0,
        long l => l == 0,
        string s => s.Length == 0,
        _ => false
    };

    private static object? DefaultOf(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;
}