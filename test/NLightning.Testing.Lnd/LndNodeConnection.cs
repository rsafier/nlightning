// Ported from LNUnit.LND (https://github.com/nbd-wtf/LNUnit, LNDNodeConnection.cs), Copyright (c) 2024-2025 nbd,
// MIT License; the full text is in LICENSE-LNUnit.txt next to this file. Changed: the channel comes from
// LndGrpcChannelFactory (pinned certificate, macaroon optional), clients for every fetched service, ConnectAsync and
// RefreshNodeInfoAsync next to the blocking constructor, idempotent disposal, no StartWithBase64 (a connection is
// built once; Clone opens another).

using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace NLightning.Testing.Lnd;

using Autopilotrpc;
using Chainrpc;
using Devrpc;
using Invoicesrpc;
using Lnrpc;
using Neutrinorpc;
using Peersrpc;
using Routerrpc;
using Signrpc;
using Verrpc;
using Walletrpc;
using WatchtowerService = Watchtowerrpc.Watchtower;
using WtClientService = Wtclientrpc.WatchtowerClient;

/// <summary>
/// One gRPC connection to an LND node with a typed client per LND service. The member names follow LNUnit.LND's
/// <c>LNDNodeConnection</c>, so tests move over by swapping the type and namespace.
/// </summary>
public class LndNodeConnection : IDisposable
{
    /// <summary>The TLV type of a keysend preimage record.</summary>
    public const ulong KeysendRecordType = 5482373484;

    /// <summary>The TLV type of a keysend text message record (as LNUnit.LND sends it).</summary>
    public const ulong KeysendMessageRecordType = 34349334;

    private readonly ILogger<LndNodeConnection>? _logger;
    private int _disposed;

    /// <summary>
    /// Opens the channel and loads the node's identity with a blocking <c>GetInfo</c>, as LNUnit.LND's constructor
    /// does; throws (and closes the channel) when the node does not answer. Prefer <see cref="ConnectAsync"/>.
    /// </summary>
    public LndNodeConnection(LndSettings settings, ILogger<LndNodeConnection>? logger = null)
        : this(settings, logger, loadNodeInfo: true)
    {
    }

    private LndNodeConnection(LndSettings settings, ILogger<LndNodeConnection>? logger, bool loadNodeInfo)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _logger = logger;
        Settings = settings.Clone();
        Host = Settings.GrpcEndpoint ?? string.Empty;
        Channel = LndGrpcChannelFactory.Create(Settings);

        LightningClient = new Lightning.LightningClient(Channel);
        RouterClient = new Router.RouterClient(Channel);
        SignClient = new Signer.SignerClient(Channel);
        StateClient = new State.StateClient(Channel);
        WalletUnlockerClient = new WalletUnlocker.WalletUnlockerClient(Channel);
        ChainNotifierClient = new ChainNotifier.ChainNotifierClient(Channel);
        ChainKitClient = new ChainKit.ChainKitClient(Channel);
        DevClient = new Dev.DevClient(Channel);
        InvoiceClient = new Invoices.InvoicesClient(Channel);
        PeersClient = new Peers.PeersClient(Channel);
        WalletKitClient = new WalletKit.WalletKitClient(Channel);
        VersionerClient = new Versioner.VersionerClient(Channel);
        AutopilotClient = new Autopilot.AutopilotClient(Channel);
        WatchtowerClient = new WatchtowerService.WatchtowerClient(Channel);
        WtClientClient = new WtClientService.WatchtowerClientClient(Channel);
        NeutrinoKitClient = new NeutrinoKit.NeutrinoKitClient(Channel);
        _logger?.LogDebug("Set up gRPC with {Host}", Host);

        if (!loadNodeInfo)
            return;

        try
        {
            ApplyNodeInfo(LightningClient.GetInfo(new GetInfoRequest()));
        }
        catch
        {
            Channel.Dispose();
            throw;
        }
    }

    /// <summary>A copy of the settings the connection was opened with.</summary>
    public LndSettings Settings { get; }

    /// <summary>The gRPC endpoint (LNUnit.LND's name).</summary>
    public string Host { get; }

    /// <summary>The underlying channel, for clients this class does not expose.</summary>
    public GrpcChannel Channel { get; }

    /// <summary><c>lnrpc.Lightning</c>.</summary>
    public Lightning.LightningClient LightningClient { get; }

    /// <summary><c>routerrpc.Router</c>.</summary>
    public Router.RouterClient RouterClient { get; }

    /// <summary><c>signrpc.Signer</c> (LNUnit.LND's name, kept for the swap).</summary>
    public Signer.SignerClient SignClient { get; }

    /// <summary><c>lnrpc.State</c>.</summary>
    public State.StateClient StateClient { get; }

    /// <summary><c>lnrpc.WalletUnlocker</c>.</summary>
    public WalletUnlocker.WalletUnlockerClient WalletUnlockerClient { get; }

    /// <summary><c>chainrpc.ChainNotifier</c>.</summary>
    public ChainNotifier.ChainNotifierClient ChainNotifierClient { get; }

    /// <summary><c>chainrpc.ChainKit</c>.</summary>
    public ChainKit.ChainKitClient ChainKitClient { get; }

    /// <summary><c>devrpc.Dev</c> (only on an LND built with the <c>dev</c> tag).</summary>
    public Dev.DevClient DevClient { get; }

    /// <summary><c>invoicesrpc.Invoices</c> (LNUnit.LND's name).</summary>
    public Invoices.InvoicesClient InvoiceClient { get; }

    /// <summary><c>peersrpc.Peers</c>.</summary>
    public Peers.PeersClient PeersClient { get; }

    /// <summary><c>walletrpc.WalletKit</c>.</summary>
    public WalletKit.WalletKitClient WalletKitClient { get; }

    /// <summary><c>verrpc.Versioner</c>.</summary>
    public Versioner.VersionerClient VersionerClient { get; }

    /// <summary><c>autopilotrpc.Autopilot</c>.</summary>
    public Autopilot.AutopilotClient AutopilotClient { get; }

    /// <summary><c>watchtowerrpc.Watchtower</c> (the tower side).</summary>
    public WatchtowerService.WatchtowerClient WatchtowerClient { get; }

    /// <summary><c>wtclientrpc.WatchtowerClient</c> (the client side).</summary>
    public WtClientService.WatchtowerClientClient WtClientClient { get; }

    /// <summary><c>neutrinorpc.NeutrinoKit</c>.</summary>
    public NeutrinoKit.NeutrinoKitClient NeutrinoKitClient { get; }

    /// <summary>The node's identity key, hex, from the last <c>GetInfo</c> (empty before one).</summary>
    public string LocalNodePubKey { get; private set; } = string.Empty;

    /// <summary><see cref="LocalNodePubKey"/> as bytes.</summary>
    public byte[] LocalNodePubKeyBytes => Convert.FromHexString(LocalNodePubKey);

    /// <summary>The node's alias from the last <c>GetInfo</c>.</summary>
    public string LocalAlias { get; private set; } = string.Empty;

    /// <summary>The first non-onion URI <c>GetInfo</c> listed (<c>pubkey@host:port</c>), or empty.</summary>
    public string ClearnetConnectString { get; private set; } = string.Empty;

    /// <summary>The first onion URI <c>GetInfo</c> listed, or empty.</summary>
    public string OnionConnectString { get; private set; } = string.Empty;

    /// <summary>At least RPC ready (also true once the server is active).</summary>
    public bool IsRpcReady => GetStateSafe() is WalletState.RpcActive or WalletState.ServerActive;

    /// <summary>The server is ready for every call.</summary>
    public bool IsServerReady => GetStateSafe() == WalletState.ServerActive;

    /// <summary>Opens a connection and loads the node's identity without blocking a thread.</summary>
    public static async Task<LndNodeConnection> ConnectAsync(LndSettings settings,
                                                             ILogger<LndNodeConnection>? logger = null,
                                                             CancellationToken cancellationToken = default)
    {
        var connection = new LndNodeConnection(settings, logger, loadNodeInfo: false);
        try
        {
            await connection.RefreshNodeInfoAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A connection whose node identity is not loaded yet (no call is made); for pools, tests and a harness that opens
    /// the connection while LND is still starting (the Kubernetes harness's <c>LndNode</c>), then calls
    /// <see cref="RefreshNodeInfoAsync"/>.
    /// </summary>
    public static LndNodeConnection CreateWithoutNodeInfo(LndSettings settings,
                                                            ILogger<LndNodeConnection>? logger = null) =>
        new(settings, logger, loadNodeInfo: false);

    /// <summary>Calls <c>GetInfo</c> and refreshes the identity, alias and connect strings.</summary>
    public async Task<GetInfoResponse> RefreshNodeInfoAsync(CancellationToken cancellationToken = default)
    {
        var info = await LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken)
                                        .ConfigureAwait(false);
        ApplyNodeInfo(info);
        return info;
    }

    /// <summary>The wallet state, or <see cref="WalletState.NonExisting"/> when the call fails or times out.</summary>
    public WalletState GetStateSafe(double timeOutSeconds = 3)
    {
        try
        {
            return StateClient.GetState(new GetStateRequest(), null, DateTime.UtcNow.AddSeconds(timeOutSeconds)).State;
        }
        catch (Exception e) when (e is RpcException or ObjectDisposedException or InvalidOperationException)
        {
            _logger?.LogDebug(e, "GetState failed for {Host}", Host);
            return WalletState.NonExisting;
        }
    }

    /// <summary>The wallet state, or <see cref="WalletState.NonExisting"/> when the call fails or times out.</summary>
    public async Task<WalletState> GetStateSafeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await StateClient.GetStateAsync(new GetStateRequest(), null, DateTime.UtcNow.Add(timeout),
                                                           cancellationToken).ConfigureAwait(false);
            return response.State;
        }
        catch (Exception e) when (e is RpcException or ObjectDisposedException or InvalidOperationException)
        {
            _logger?.LogDebug(e, "GetState failed for {Host}", Host);
            return WalletState.NonExisting;
        }
    }

    /// <summary>
    /// Sends a keysend payment through <c>Router.SendPaymentV2</c> and returns the last update (null if the stream
    /// ended without one). A random preimage goes in record 5482373484; <paramref name="message"/> in 34349334.
    /// </summary>
    public async Task<Payment?> KeysendPayment(string dest, long amtSat, long feeLimitSat = 10, string? message = null,
                                               int timeoutSeconds = 60, Dictionary<ulong, byte[]>? keySendPairs = null,
                                               CancellationToken cancellationToken = default)
    {
        var preimage = RandomNumberGenerator.GetBytes(32);
        var payment = new SendPaymentRequest
        {
            Dest = ByteString.CopyFrom(Convert.FromHexString(dest)),
            Amt = amtSat,
            FeeLimitSat = feeLimitSat,
            PaymentHash = ByteString.CopyFrom(SHA256.HashData(preimage)),
            TimeoutSeconds = timeoutSeconds
        };
        payment.DestCustomRecords.Add(KeysendRecordType, ByteString.CopyFrom(preimage));
        if (keySendPairs is not null)
            foreach (var (type, value) in keySendPairs)
                payment.DestCustomRecords.Add(type, ByteString.CopyFrom(value));
        if (message is not null)
            payment.DestCustomRecords.Add(KeysendMessageRecordType, ByteString.CopyFrom(Encoding.UTF8.GetBytes(message)));

        using var call = RouterClient.SendPaymentV2(payment, cancellationToken: cancellationToken);
        Payment? last = null;
        await foreach (var update in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            last = update;
        return last;
    }

    /// <summary>A new connection with the same settings (blocking <c>GetInfo</c>, as LNUnit.LND's Clone).</summary>
    public LndNodeConnection Clone() => new(Settings, _logger);

    /// <summary>Closes the channel (LNUnit.LND's name).</summary>
    public Task Stop()
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Closes the channel; later calls fail. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Channel.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ApplyNodeInfo(GetInfoResponse info)
    {
        LocalNodePubKey = info.IdentityPubkey;
        LocalAlias = info.Alias;
        ClearnetConnectString = info.Uris.FirstOrDefault(x => !x.Contains("onion", StringComparison.Ordinal))
                             ?? string.Empty;
        OnionConnectString = info.Uris.FirstOrDefault(x => x.Contains("onion", StringComparison.Ordinal))
                          ?? string.Empty;
        _logger?.LogDebug("Connected gRPC to {Alias} {PubKey} @ {Host}", LocalAlias, LocalNodePubKey, Host);
    }
}