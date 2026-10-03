using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Abcd;
using Daemon.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Client.Responses;
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
/// command handler (<c>IClientCommandHandler&lt;GetRouteClientRequest, GetRouteClientResponse&gt;</c>), the same path
/// as <c>nltg getroute &lt;node&gt; &lt;amount_msat&gt;</c>.
/// </summary>
/// <remarks>
/// A handler that throws (a <c>ClientException</c> when there is no route) is reported as
/// <see cref="RouteView.Error"/>, never rethrown, so a proof can wait for a route to appear or disappear.
/// </remarks>
public static class GetRouteProbe
{
    /// <summary>
    /// <c>getroute <paramref name="destination"/> <paramref name="amountMsat"/></c> on <paramref name="node"/>; logs the
    /// result.
    /// </summary>
    public static async Task<RouteView> GetRouteAsync(NLightningTestNode node, CompactPubKey destination,
                                                      ulong amountMsat, CancellationToken cancellationToken)
    {
        RouteView view;
        try
        {
            using var scope = node.Services.CreateScope();
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<GetRouteClientRequest,
                                    GetRouteClientResponse>>();
            var response = await handler.HandleAsync(
                new GetRouteClientRequest(destination, LightningMoney.MilliSatoshis(amountMsat)), cancellationToken);
            var hops = response.Hops.Select(hop => new RouteHopView(hop.ShortChannelId.ToUInt64(),
                                                                     Convert.ToHexStringLower(hop.NodeId),
                                                                     hop.Amount.MilliSatoshi,
                                                                     hop.Fee.MilliSatoshi, hop.CltvExpiry))
                                .ToList();
            view = new RouteView(hops, response.Fee.MilliSatoshi, hops.Count == 0 ? "no hops" : null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            view = new RouteView([], 0, $"{e.GetType().Name}: {e.Message}");
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
}