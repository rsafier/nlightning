using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Integration.Tests.Docker.Gossip;

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
/// The gossip settings and calls of the BOLT 7 Docker proofs, written against the wave G-B contracts (plan §3.9, G1-T1,
/// G1-T6): <c>OpenChannelClientRequest.IsPublic</c> (IPC key 4 of <c>openchannel</c>), the <c>Gossip</c> section
/// (<c>SyncEnabled</c>, <c>RelayEnabled</c>, <c>AcceptPublicChannels</c>, <c>AnnounceAddresses</c>; plan D12, G1-T1,
/// G1-T4) and <c>NodeOptions.Alias</c>/<c>Color</c>.
/// </summary>
/// <remarks>
/// Lane B3 wrote these tests in parallel with the lanes that implement the contracts (B1 public channels, B2 graph),
/// so every setting goes through configuration keys (ignored by the binder until the property exists) and the public
/// flag through <see cref="MarkPublic"/>. Each place a name may differ carries a <c>TODO(G-B integrator)</c>. A key
/// the binder ignores would only show as a proof timing out, so <see cref="StartGossipNodeAsync"/> checks the bound
/// options right after the start (<see cref="VerifyBoundGossipOptions"/>): a property that exists without the
/// configured value fails the start at once.
/// </remarks>
public static class GossipTestNodes
{
    /// <summary>
    /// The color every proof node announces (LND prints it as <c>#rrggbb</c>); not LND's default <c>#3399ff</c>, which
    /// the fixture's LND nodes announce.
    /// </summary>
    public const string Color = "#1f7a4d";

    // TODO(G-B integrator): check these names against B1/B2's GossipOptions and NodeOptions (Alias, Color). A wrong key
    // is silently ignored by the configuration binder; VerifyBoundGossipOptions catches a property that exists with
    // another value, but a property with another name is only logged as missing: fix s_gossipFlags then
    private const string GossipSection = "Gossip";
    private const string GossipOptionsTypeName = "GossipOptions";
    private const string AliasKey = "Node:Alias";
    private const string ColorKey = "Node:Color";

    /// <summary>
    /// The <c>Gossip</c> flags every proof node sets: sync and relay on whatever their default (plan D12
    /// <c>Gossip:SyncEnabled</c>/<c>RelayEnabled</c>), and fundee acceptance of public channels (G1-T1).
    /// </summary>
    private static readonly string[] s_gossipFlags = ["SyncEnabled", "RelayEnabled", "AcceptPublicChannels"];

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
    /// <c>Color</c>, and every flag of <see cref="s_gossipFlags"/> on the <c>GossipOptions</c> the node registered. A
    /// property (or the options class) this build does not have yet is logged and skipped, since the contracts land in
    /// parallel lanes; one that exists without the configured value throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">A bound option does not carry the configured value.</exception>
    public static void VerifyBoundGossipOptions(IServiceProvider services, string alias)
    {
        var mismatches = new List<string>();
        var nodeOptions = services.GetRequiredService<IOptions<NodeOptions>>().Value;
        CheckOption(nodeOptions, "Alias", alias, mismatches);
        CheckOption(nodeOptions, "Color", Color, mismatches);

        var gossipOptionsType = FindGossipOptionsType();
        if (gossipOptionsType is null)
        {
            Console.WriteLine($"{GossipOptionsTypeName} is not in this build: the {GossipSection} flags are not checked");
        }
        else
        {
            var optionsType = typeof(IOptions<>).MakeGenericType(gossipOptionsType);
            var options = services.GetService(optionsType);
            var gossipOptions = options is null
                                    ? null
                                    : optionsType.GetProperty(nameof(IOptions<object>.Value))!.GetValue(options);
            if (gossipOptions is null)
                mismatches.Add($"{gossipOptionsType.FullName} is not registered as IOptions<>");
            else
                foreach (var flag in s_gossipFlags)
                    CheckOption(gossipOptions, flag, true, mismatches);
        }

        if (mismatches.Count > 0)
            throw new InvalidOperationException(
                $"The gossip test node did not bind its configuration: {string.Join("; ", mismatches)}");
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

    private static Type? FindGossipOptionsType() =>
        AppDomain.CurrentDomain.GetAssemblies()
                 .Where(a => a.GetName().Name?.StartsWith("NLightning.", StringComparison.Ordinal) == true
                          && !a.GetName().Name!.EndsWith(".Tests", StringComparison.Ordinal))
                 .SelectMany(LoadableTypes)
                 .FirstOrDefault(t => t is { IsClass: true, IsAbstract: false, Name: GossipOptionsTypeName });

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
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