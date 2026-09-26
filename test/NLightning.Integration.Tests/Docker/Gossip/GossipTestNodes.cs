using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Application.Gossip.Graph;
using Daemon.Interfaces;
using Domain.Channels.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Fixtures;
using Utils;

/// <summary>
/// The gossip settings and calls of the BOLT 7 Docker proofs (plan §3.9, G1-T1, G1-T6):
/// <c>OpenChannelClientRequest.IsPublic</c> (IPC key 4 of <c>openchannel</c>), the <c>Gossip</c> section
/// (<c>Enabled</c> of <c>GossipGraphOptions</c>, <c>AcceptPublicChannels</c> of <c>GossipOptions</c>; plan D12, G1-T1)
/// and <c>NodeOptions.Alias</c>/<c>Color</c>.
/// </summary>
/// <remarks>
/// A key the binder ignores would only show as a proof timing out, so <see cref="StartGossipNodeAsync"/> checks the
/// bound options right after the start (<see cref="VerifyBoundGossipOptions"/>).
/// </remarks>
public static class GossipTestNodes
{
    /// <summary>
    /// The color every proof node announces (LND prints it as <c>#rrggbb</c>); not LND's default <c>#3399ff</c>, which
    /// the fixture's LND nodes announce.
    /// </summary>
    public const string Color = "#1f7a4d";

    private const string GossipSection = "Gossip";
    private const string AliasKey = "Node:Alias";
    private const string ColorKey = "Node:Color";

    /// <summary>
    /// The <c>Gossip</c> flags every proof node sets: the graph on (<c>GossipGraphOptions.Enabled</c>; default on
    /// everywhere but mainnet, plan D12) and fundee acceptance of public channels (<c>GossipOptions</c>, G1-T1).
    /// </summary>
    private static readonly string[] s_gossipFlags =
        [nameof(GossipGraphOptions.Enabled), nameof(GossipOptions.AcceptPublicChannels)];

    private static readonly TimeSpan s_openStepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_peerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A started node with its own port, key and SQLite file, gossip on, public channels accepted, and
    /// <paramref name="alias"/> and <see cref="Color"/> for its <c>node_announcement</c>. No announced addresses: BOLT 7
    /// allows a node_announcement without any, and the LND nodes never dial us. <paramref name="configure"/> runs before
    /// the start (e.g. to set <see cref="NLightningTestNode.ConfigureServices"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The node did not bind the gossip settings.</exception>
    public static async Task<NLightningTestNode> StartGossipNodeAsync(LightningRegtestNetworkFixture fixture,
                                                                      string name, string alias,
                                                                      CancellationToken cancellationToken,
                                                                      Action<NLightningTestNode>? configure = null)
    {
        var node = await NLightningTestNode.CreateAsync(fixture, name);
        foreach (var flag in s_gossipFlags)
            node.ExtraConfiguration[$"{GossipSection}:{flag}"] = "true";
        node.ExtraConfiguration[AliasKey] = alias;
        node.ExtraConfiguration[ColorKey] = Color;
        configure?.Invoke(node);
        try
        {
            await node.StartAsync(cancellationToken);
            VerifyBoundGossipOptions(node.Services, alias);
            return node;
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Checks that the node bound what <see cref="StartGossipNodeAsync"/> configured: <c>NodeOptions.Alias</c> and
    /// <c>Color</c>, <c>GossipOptions.AcceptPublicChannels</c> and <c>GossipGraphOptions.Enabled</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">A bound option does not carry the configured value.</exception>
    public static void VerifyBoundGossipOptions(IServiceProvider services, string alias)
    {
        var mismatches = new List<string>();
        var nodeOptions = services.GetRequiredService<IOptions<NodeOptions>>().Value;
        CheckOption(nodeOptions, "Alias", alias, mismatches);
        CheckOption(nodeOptions, "Color", Color, mismatches);

        var gossipOptions = services.GetRequiredService<IOptions<GossipOptions>>().Value;
        CheckOption(gossipOptions, nameof(GossipOptions.AcceptPublicChannels), true, mismatches);
        var graphOptions = services.GetRequiredService<IOptions<GossipGraphOptions>>().Value;
        CheckOption(graphOptions, nameof(GossipGraphOptions.Enabled), true, mismatches);

        if (mismatches.Count > 0)
            throw new InvalidOperationException(
                $"The gossip test node did not bind its configuration: {string.Join("; ", mismatches)}");
    }

    /// <summary>
    /// Asks for a public channel (<c>openchannel --public</c>, G1-T1).
    /// </summary>
    public static OpenChannelClientRequest MarkPublic(OpenChannelClientRequest request)
    {
        request.IsPublic = true;
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

    /// <summary>
    /// Adds a mismatch when <paramref name="options"/> has a property <paramref name="name"/> that does not carry
    /// <paramref name="expected"/>. A color compares as hex with or without <c>#</c> (string, 3 bytes or a value
    /// object's text), an alias as text or as its zero-padded UTF-8 bytes.
    /// </summary>
    internal static void CheckOption(object options, string name, object expected, List<string> mismatches)
    {
        var property = options.GetType().GetProperty(name);
        if (property is null)
        {
            Console.WriteLine($"{options.GetType().Name}.{name} is not in this build: not checked");
            return;
        }

        var actual = property.GetValue(options);
        var matches = (expected, actual) switch
        {
            (string e, string a) => string.Equals(e.TrimStart('#'), a.TrimStart('#'),
                                                  StringComparison.OrdinalIgnoreCase),
            (string e, byte[] a) => string.Equals(e.TrimStart('#'), Convert.ToHexString(a),
                                                  StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(e, Encoding.UTF8.GetString(a).TrimEnd('\0'),
                                                  StringComparison.Ordinal),
            // A value object (e.g. an RGB color type): compare its text form
            (string e, not null) => string.Equals(e.TrimStart('#'), actual.ToString()?.TrimStart('#'),
                                                  StringComparison.OrdinalIgnoreCase),
            _ => Equals(expected, actual)
        };
        Console.WriteLine($"{options.GetType().Name}.{name} = '{actual}' ({(matches ? "as configured" : "MISMATCH")})");
        if (!matches)
            mismatches.Add($"{options.GetType().Name}.{name} is '{actual}', expected '{expected}'");
    }
}