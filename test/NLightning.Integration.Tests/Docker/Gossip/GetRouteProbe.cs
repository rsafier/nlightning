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
/// One hop of a route our node's <c>getroute</c> returned: the node it reaches and the HTLC that node receives.
/// </summary>
/// <param name="ShortChannelId">The channel the HTLC arrives on (BOLT 7 <c>u64</c>; our channel for the first hop).
/// </param>
/// <param name="NodeIdHex">The node the hop reaches (lowercase hex).</param>
/// <param name="AmountMsat">What the HTLC arriving at that node carries (LND: <c>AmtToForwardMsat + FeeMsat</c> of
/// the same hop).</param>
/// <param name="FeeMsat">What that node keeps for forwarding (zero for the destination; LND's <c>FeeMsat</c>).</param>
/// <param name="CltvExpiry">The arriving HTLC's absolute <c>cltv_expiry</c>.</param>
public sealed record RouteHopView(ulong ShortChannelId, string NodeIdHex, ulong AmountMsat, ulong FeeMsat,
                                  uint CltvExpiry);

/// <summary>
/// A route our node's <c>getroute</c> returned (IPC 19, plan G4-T4), or why it returned none.
/// </summary>
/// <param name="Hops">The hops, our peer first and the destination last; empty when there is no route.</param>
/// <param name="TotalFeeMsat">The route's total fee (0 when there is no route).</param>
/// <param name="Error">Why no route came back (the handler's exception), or null.</param>
public sealed record RouteView(IReadOnlyList<RouteHopView> Hops, ulong TotalFeeMsat, string? Error)
{
    public bool Found => Error is null && Hops.Count > 0;

    public IReadOnlyList<ulong> ShortChannelIds => Hops.Select(h => h.ShortChannelId).ToList();

    public override string ToString() =>
        Found
            ? $"{Hops.Count} hops [{string.Join(" -> ", Hops.Select(h => $"{new ShortChannelId(h.ShortChannelId)}:{h.NodeIdHex[..16]}… amt {h.AmountMsat} fee {h.FeeMsat} cltv {h.CltvExpiry}"))}], total fee {TotalFeeMsat}"
            : $"no route ({Error ?? "empty"})";
}

/// <summary>
/// Calls our node's <c>getroute</c> (<c>ClientCommand.GetRoute</c> = 19, plan G4-T4) through the daemon's client
/// command handler, the same path as <c>nltg getroute &lt;node&gt; &lt;amount_msat&gt;</c>.
/// </summary>
/// <remarks>
/// <para>TODO(G-C integrate): lane C2 adds the contract in parallel with this lane (C3), so it is bound here by its
/// exact names through reflection, and only here: <c>NLightning.Domain.Client.Requests.GetRouteClientRequest(
/// CompactPubKey nodeId, LightningMoney amount)</c>, <c>NLightning.Domain.Client.Responses.GetRouteClientResponse</c>
/// (<c>Hops</c>: <c>IReadOnlyList&lt;GetRouteHop&gt;</c>, <c>Fee</c>: <c>LightningMoney</c>) and <c>GetRouteHop</c>
/// (<c>NodeId</c>: <c>CompactPubKey</c>, <c>ShortChannelId</c>: <c>ShortChannelId</c> (the channel the hop's HTLC
/// arrives on), <c>Amount</c>: <c>LightningMoney</c> (what that HTLC carries), <c>CltvExpiry</c>: <c>uint</c>,
/// <c>Fee</c>: <c>LightningMoney</c> (what the node keeps)), handled by
/// <c>IClientCommandHandler&lt;GetRouteClientRequest, GetRouteClientResponse&gt;</c>, which throws a
/// <c>ClientException</c> when there is no route. Once lane C2 is merged, replace <see cref="InvokeAsync"/> and
/// <see cref="ReadResponse"/> with the typed call (<c>handler.HandleAsync(new GetRouteClientRequest(destination,
/// LightningMoney.MilliSatoshis(amountMsat)), ct)</c> and a plain mapping of the response); the proofs only use
/// <see cref="GetRouteAsync"/>, <see cref="WaitForRouteAsync"/> and <see cref="RouteView"/>.</para>
/// <para>A handler that throws (e.g. no route) is reported as <see cref="RouteView.Error"/>, never rethrown, so a
/// proof can wait for a route to appear or disappear. A build without the contract throws
/// <see cref="InvalidOperationException"/> naming this TODO (never read as "no route").</para>
/// </remarks>
public static class GetRouteProbe
{
    private const string RequestTypeName = "NLightning.Domain.Client.Requests.GetRouteClientRequest";
    private const string ResponseTypeName = "NLightning.Domain.Client.Responses.GetRouteClientResponse";

    /// <summary>
    /// <c>getroute <paramref name="destination"/> <paramref name="amountMsat"/></c> on <paramref name="node"/>; logs the
    /// result.
    /// </summary>
    /// <exception cref="InvalidOperationException">This build has no <c>getroute</c> contract or handler, or the
    /// response does not have its shape.</exception>
    public static async Task<RouteView> GetRouteAsync(NLightningTestNode node, CompactPubKey destination,
                                                      ulong amountMsat, CancellationToken cancellationToken)
    {
        RouteView view;
        try
        {
            view = ReadResponse(await InvokeAsync(node, destination, amountMsat, cancellationToken));
        }
        catch (HandlerFailedException e)
        {
            // Only the handler's own failure (e.g. no route) is a route answer; a contract mismatch propagates
            view = new RouteView([], 0, $"{e.InnerException!.GetType().Name}: {e.InnerException.Message}");
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

    /// <summary>
    /// Builds the request and calls the registered handler; returns the response.
    /// </summary>
    private static async Task<object> InvokeAsync(NLightningTestNode node, CompactPubKey destination,
                                                  ulong amountMsat, CancellationToken cancellationToken)
    {
        var domain = typeof(ListGraphChannelsClientRequest).Assembly;
        var requestType = domain.GetType(RequestTypeName);
        var responseType = domain.GetType(ResponseTypeName);
        var constructor = requestType?.GetConstructor([typeof(CompactPubKey), typeof(LightningMoney)]);
        if (requestType is null || responseType is null || constructor is null)
            throw Missing($"{RequestTypeName}(CompactPubKey, LightningMoney) and {ResponseTypeName}");

        var request = constructor.Invoke([destination, LightningMoney.MilliSatoshis(amountMsat)]);
        using var scope = node.Services.CreateScope();
        var handlerType = typeof(IClientCommandHandler<,>).MakeGenericType(requestType, responseType);
        var handler = scope.ServiceProvider.GetService(handlerType)
                   ?? throw Missing($"a registered IClientCommandHandler<{requestType.Name}, {responseType.Name}>");
        var task = (Task)handlerType.GetMethod("HandleAsync")!.Invoke(handler, [request, cancellationToken])!;
        try
        {
            await task;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new HandlerFailedException(e);
        }

        return task.GetType().GetProperty("Result")!.GetValue(task)
            ?? throw Missing("a non-null GetRouteClientResponse");
    }

    private static RouteView ReadResponse(object response)
    {
        var hops = ((IEnumerable)Read(response, "Hops")).Cast<object>().Select(hop => new RouteHopView(
                       ((ShortChannelId)Read(hop, "ShortChannelId")).ToUInt64(),
                       Convert.ToHexStringLower((CompactPubKey)Read(hop, "NodeId")),
                       ((LightningMoney)Read(hop, "Amount")).MilliSatoshi,
                       ((LightningMoney)Read(hop, "Fee")).MilliSatoshi,
                       (uint)Read(hop, "CltvExpiry"))).ToList();
        var fee = ((LightningMoney)Read(response, "Fee")).MilliSatoshi;
        return new RouteView(hops, fee, hops.Count == 0 ? "no hops" : null);
    }

    private static object Read(object holder, string property) =>
        holder.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(holder)
     ?? throw Missing($"{holder.GetType().Name}.{property}");

    private static InvalidOperationException Missing(string what) =>
        new($"This build has no getroute contract ({what}): TODO(G-C integrate), IPC 19 from lane C2");

    /// <summary>
    /// The <c>getroute</c> handler itself threw (<see cref="Exception.InnerException"/>).
    /// </summary>
    private sealed class HandlerFailedException(Exception inner) : Exception(inner.Message, inner);
}