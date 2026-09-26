using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Daemon.Interfaces;
using Domain.Channels.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Fixtures;
using Utils;

/// <summary>
/// The gossip settings and calls of the BOLT 7 Docker proofs, written against the wave G-B contracts (plan §3.9, G1-T1,
/// G1-T6): <c>OpenChannelClientRequest.IsPublic</c> (IPC key 4 of <c>openchannel</c>), the <c>Gossip</c> section
/// (<c>Enabled</c>, <c>AcceptPublicChannels</c>, <c>AnnounceAddresses</c>) and <c>NodeOptions.Alias</c>/<c>Color</c>.
/// </summary>
/// <remarks>
/// Lane B3 wrote these tests in parallel with the lanes that implement the contracts (B1 public channels, B2 graph),
/// so every setting goes through configuration keys (ignored by the binder until the property exists) and the public
/// flag through <see cref="MarkPublic"/>. Each place a name may differ carries a <c>TODO(G-B integrator)</c>.
/// </remarks>
public static class GossipTestNodes
{
    /// <summary>
    /// The color every proof node announces (LND prints it as <c>#rrggbb</c>); not LND's default <c>#3399ff</c>, which
    /// the fixture's LND nodes announce.
    /// </summary>
    public const string Color = "#1f7a4d";

    // TODO(G-B integrator): check these keys against B1/B2's GossipOptions and NodeOptions (Alias, Color); a wrong key
    // is silently ignored by the configuration binder, and the proofs then fail on the missing alias or announcement
    private const string GossipEnabledKey = "Gossip:Enabled";
    private const string AcceptPublicChannelsKey = "Gossip:AcceptPublicChannels";
    private const string AliasKey = "Node:Alias";
    private const string ColorKey = "Node:Color";

    private static readonly TimeSpan s_openStepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_peerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A started node with its own port, key and SQLite file, gossip on, public channels accepted, and
    /// <paramref name="alias"/> and <see cref="Color"/> for its <c>node_announcement</c>. No announced addresses: BOLT 7
    /// allows a node_announcement without any, and the LND nodes never dial us.
    /// </summary>
    public static async Task<NLightningTestNode> StartGossipNodeAsync(LightningRegtestNetworkFixture fixture,
                                                                      string name, string alias,
                                                                      CancellationToken cancellationToken)
    {
        var node = await NLightningTestNode.CreateAsync(fixture, name);
        node.ExtraConfiguration[GossipEnabledKey] = "true";
        node.ExtraConfiguration[AcceptPublicChannelsKey] = "true";
        node.ExtraConfiguration[AliasKey] = alias;
        node.ExtraConfiguration[ColorKey] = Color;
        try
        {
            await node.StartAsync(cancellationToken);
            return node;
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Asks for a public channel (<c>openchannel --public</c>, G1-T1).
    /// </summary>
    /// <exception cref="InvalidOperationException">The build has no <c>IsPublic</c> flag yet (B1 not merged).</exception>
    public static OpenChannelClientRequest MarkPublic(OpenChannelClientRequest request)
    {
        // TODO(G-B integrator): replace with `request.IsPublic = true` once B1 (G1-T1) is merged, and drop the reflection
        var property = typeof(OpenChannelClientRequest).GetProperty("IsPublic")
                    ?? throw new InvalidOperationException(
                           "OpenChannelClientRequest.IsPublic does not exist in this build (wave G-B lane B1, G1-T1)");
        property.SetValue(request, true);
        return request;
    }

    /// <summary>
    /// Opens a channel through the daemon's client handlers and returns once <c>funding_signed</c> arrived, without
    /// mining (unlike <see cref="NLightningTestNode.OpenChannelAsync"/>), so a test can stop between confirmations.
    /// </summary>
    public static async Task<OpenChannelClientSubscriptionResponse> OpenUntilFundingSignedAsync(
        NLightningTestNode node, OpenChannelClientRequest request, CancellationToken cancellationToken)
    {
        OpenChannelClientResponse openResponse;
        using (var scope = node.Services.CreateScope())
        {
            var handler = scope.ServiceProvider
                               .GetRequiredService<IClientCommandHandler<OpenChannelClientRequest,
                                    OpenChannelClientResponse>>();
            openResponse = await handler.HandleAsync(request, cancellationToken)
                                        .WaitAsync(s_openStepTimeout, cancellationToken);
        }

        while (true)
        {
            OpenChannelClientSubscriptionResponse state;
            using (var scope = node.Services.CreateScope())
            {
                var handler = scope.ServiceProvider
                                   .GetRequiredService<IClientCommandHandler<OpenChannelClientSubscriptionRequest,
                                        OpenChannelClientSubscriptionResponse>>();
                state = await handler.HandleAsync(new OpenChannelClientSubscriptionRequest(openResponse.ChannelId),
                                                  cancellationToken)
                                     .WaitAsync(s_openStepTimeout, cancellationToken);
            }

            if (state.ChannelState is ChannelState.V1FundingSigned or ChannelState.ReadyForThem
                                   or ChannelState.ReadyForUs or ChannelState.Open)
                return state;
        }
    }

    /// <summary>
    /// A public channel request of <paramref name="capacity"/> to <paramref name="peerAddress"/>, at the fee rate the
    /// other Docker tests use.
    /// </summary>
    public static OpenChannelClientRequest PublicChannelRequest(string peerAddress, LightningMoney capacity) =>
        MarkPublic(new OpenChannelClientRequest(peerAddress, capacity)
        {
            FeeRatePerKw = LightningMoney.Satoshis(10_000)
        });

    /// <summary>
    /// Sends <c>gossip_timestamp_filter</c> covering every timestamp to <paramref name="peerId"/>: LND sends its graph
    /// only to a peer that sent one (BOLT 7 B7-RL-01).
    /// </summary>
    /// <remarks>
    /// TODO(G3-T2): our node sends its own filter once gossip sync lands; then this call only repeats it. Until then it
    /// stands in for the sync (plan Proof G2 (a): "before G3 through alice's full dump").
    /// </remarks>
    public static async Task SendFullTimestampFilterAsync(NLightningTestNode node, byte[] peerId,
                                                          CancellationToken cancellationToken)
    {
        var peer = await Poll.ForAsync(() => Task.FromResult(node.PeerManager.GetPeer(peerId)), s_peerTimeout,
                                       "peer registered", cancellationToken);
        Assert.True(peer.TryGetPeerService(out var peerService));
        await peerService.SendGossipMessageAsync(
            new GossipTimestampFilterMessage(new GossipTimestampFilterPayload(ChainConstants.Regtest, 0,
                                                                              uint.MaxValue)));
    }
}