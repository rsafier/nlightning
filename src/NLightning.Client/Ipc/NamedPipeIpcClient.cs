using System.IO.Pipes;
using MessagePack;
using NLightning.Domain.Channels.ValueObjects;

namespace NLightning.Client.Ipc;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.ValueObjects;
using Handlers;
using Transport.Ipc;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

public sealed class NamedPipeIpcClient : IAsyncDisposable
{
    /// <summary>
    /// The shared framing (NL-154): the daemon's pipe server reads and writes the same frames.
    /// </summary>
    private static readonly IpcFraming s_framing = new();

    private readonly string _namedPipeFilePath;
    private readonly string _cookieFilePath;
    private readonly string _server;

    public NamedPipeIpcClient(string namedPipeFilePath, string cookieFilePath, string server = ".")
    {
        _namedPipeFilePath = namedPipeFilePath;
        _cookieFilePath = cookieFilePath;
        _server = server;
    }

    public async Task<NodeInfoIpcResponse> GetNodeInfoAsync(CancellationToken ct = default)
    {
        var req = new NodeInfoIpcRequest();
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.NodeInfo,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = 0
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<NodeInfoIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<ConnectPeerIpcResponse> ConnectPeerAsync(string address, CancellationToken ct = default)
    {
        var req = new ConnectPeerIpcRequest
        {
            Address = new PeerAddressInfo(address)
        };
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.ConnectPeer,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = 0
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<ConnectPeerIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<ListPeersIpcResponse> ListPeersAsync(CancellationToken ct = default)
    {
        var req = new ListPeersIpcRequest();
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.ListPeers,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = IpcEnvelopeKind.Request
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<ListPeersIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<ListChannelsIpcResponse> ListChannelsAsync(string? peerId, CancellationToken ct = default)
    {
        var req = new ListChannelsIpcRequest
        {
            PeerId = string.IsNullOrWhiteSpace(peerId) ? null : new CompactPubKey(Convert.FromHexString(peerId))
        };
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.ListChannels,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = IpcEnvelopeKind.Request
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<ListChannelsIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<GetAddressIpcResponse> GetAddressAsync(string? addressTypeString, CancellationToken ct = default)
    {
        var req = new GetAddressIpcRequest { AddressType = ParseAddressType(addressTypeString) };
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.GetAddress,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = IpcEnvelopeKind.Request
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<GetAddressIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<WalletBalanceIpcResponse> GetWalletBalance(CancellationToken ct)
    {
        var req = new WalletBalanceIpcRequest();
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.WalletBalance,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = 0
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<WalletBalanceIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    public async Task<OpenChannelIpcResponse> OpenChannelAsync(string nodeInfo, string amountSats,
                                                               string? pushSats = null,
                                                               CancellationToken ct = default,
                                                               bool isPublic = false, bool isDualFunded = false,
                                                               bool forceV1 = false, LabelArguments? labels = null,
                                                               ulong? requestInboundSat = null,
                                                               ulong? maxLiquidityFeeSat = null,
                                                               string? channelType = null)
    {
        var req = new OpenChannelIpcRequest
        {
            ChannelType = channelType,
            NodeInfo = nodeInfo,
            Amount = LightningMoney.Satoshis(Convert.ToInt64(amountSats)),
            PushAmount = pushSats is null ? null : LightningMoney.Satoshis(Convert.ToInt64(pushSats)),
            IsPublic = isPublic,
            IsDualFunded = isDualFunded,
            ForceV1 = forceV1,
            Label = labels?.Label,
            Tags = labels?.TagsOrNull,
            RequestInboundSat = requestInboundSat,
            MaxLiquidityFeeSat = maxLiquidityFeeSat
        };
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.OpenChannel,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = 0
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<OpenChannelIpcResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    /// <summary>
    /// One long-poll of the open subscription (ClientCommand 7): answers when the channel runs on a published funding
    /// transaction other than <paramref name="knownFundingTxId"/> and when it is ready (NL-535).
    /// </summary>
    public async Task<OpenChannelSubscriptionIpcResponse> OpenChannelSubscriptionAsync(
        ChannelId channelId, TxId? knownFundingTxId = null, CancellationToken ct = default)
    {
        var req = new OpenChannelSubscriptionIpcRequest
        {
            ChannelId = channelId,
            KnownFundingTxId = knownFundingTxId,
            ReportFundingChanges = true
        };
        var payload = MessagePackSerializer.Serialize(req, cancellationToken: ct);
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.OpenChannelSubscription,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = payload,
            Kind = 0
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<OpenChannelSubscriptionIpcResponse>(
                respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    /// <summary>
    /// Creates an invoice (ClientCommand 9).
    /// </summary>
    /// <param name="amount">The requested amount, or null for an any-amount invoice.</param>
    /// <param name="description">BOLT 11 <c>d</c>; may be empty.</param>
    /// <param name="expirySeconds">BOLT 11 <c>x</c>, or null for the node default.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <param name="labels">The operator's label and tags (NL-602 A3-T1), or null for none.</param>
    public Task<CreateInvoiceIpcResponse> CreateInvoiceAsync(LightningMoney? amount, string description,
                                                             uint? expirySeconds, CancellationToken ct = default,
                                                             LabelArguments? labels = null)
    {
        var req = new CreateInvoiceIpcRequest
        {
            Amount = amount,
            Description = description,
            ExpirySeconds = expirySeconds,
            Label = labels?.Label,
            Tags = labels?.TagsOrNull
        };
        return SendRequestAsync<CreateInvoiceIpcRequest, CreateInvoiceIpcResponse>(ClientCommand.CreateInvoice, req,
                                                                                    ct);
    }

    /// <summary>
    /// Waits until one of our invoices is no longer open (ClientCommand 47, Cashu plan C0, NL-991).
    /// </summary>
    /// <param name="paymentHash">The invoice's payment hash.</param>
    /// <param name="timeoutSeconds">How long the daemon waits, or null for its default.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<WaitInvoiceIpcResponse> WaitInvoiceAsync(Hash paymentHash, uint? timeoutSeconds,
                                                         CancellationToken ct = default)
    {
        var req = new WaitInvoiceIpcRequest { PaymentHash = paymentHash, TimeoutSeconds = timeoutSeconds };
        return SendRequestAsync<WaitInvoiceIpcRequest, WaitInvoiceIpcResponse>(ClientCommand.WaitInvoice, req, ct);
    }

    /// <summary>
    /// Creates a hold invoice (ClientCommand.CreateHoldInvoice, NL-995, Cashu plan C4): the node locks the paying
    /// HTLCs in and holds them until the operator settles with the preimage or cancels. The node does not generate
    /// the preimage: the caller must be able to produce one whose SHA-256 is <paramref name="paymentHash"/> (the
    /// NUT-14/ASP flow).
    /// </summary>
    /// <param name="paymentHash">SHA-256 of the preimage the operator keeps.</param>
    /// <param name="amount">The requested amount in msat, or null for an invoice that accepts any amount.</param>
    /// <param name="description">The description; may be empty.</param>
    /// <param name="expirySeconds">The expiry in seconds, or null for the node default.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <param name="labels">The operator's label and tags (NL-602 A3-T1), or null for none.</param>
    public Task<HoldInvoiceIpcResponse> CreateHoldInvoiceAsync(Hash paymentHash, LightningMoney? amount,
                                                               string description, uint? expirySeconds,
                                                               CancellationToken ct = default,
                                                               LabelArguments? labels = null)
    {
        var req = new CreateHoldInvoiceIpcRequest
        {
            PaymentHash = paymentHash,
            Amount = amount,
            Description = description,
            ExpirySeconds = expirySeconds,
            Label = labels?.Label,
            Tags = labels?.TagsOrNull
        };
        return SendRequestAsync<CreateHoldInvoiceIpcRequest, HoldInvoiceIpcResponse>(ClientCommand.CreateHoldInvoice,
                                                                                     req, ct);
    }

    /// <summary>
    /// Settles a held hold invoice by revealing the preimage (ClientCommand.SettleHoldInvoice, NL-995): the paying
    /// HTLCs are fulfilled with it; the daemon refuses a preimage that does not hash to the payment hash.
    /// </summary>
    /// <param name="paymentHash">The hold invoice's payment hash.</param>
    /// <param name="preimage">The preimage of <paramref name="paymentHash"/>.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<HoldInvoiceIpcResponse> SettleHoldInvoiceAsync(Hash paymentHash, Secret preimage,
                                                               CancellationToken ct = default)
    {
        var req = new SettleHoldInvoiceIpcRequest { PaymentHash = paymentHash, Preimage = preimage };
        return SendRequestAsync<SettleHoldInvoiceIpcRequest, HoldInvoiceIpcResponse>(ClientCommand.SettleHoldInvoice,
                                                                                     req, ct);
    }

    /// <summary>
    /// Cancels a hold invoice (ClientCommand.CancelHoldInvoice, NL-995): its held HTLCs are failed back.
    /// </summary>
    /// <param name="paymentHash">The hold invoice's payment hash.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<HoldInvoiceIpcResponse> CancelHoldInvoiceAsync(Hash paymentHash, CancellationToken ct = default)
    {
        var req = new CancelHoldInvoiceIpcRequest { PaymentHash = paymentHash };
        return SendRequestAsync<CancelHoldInvoiceIpcRequest, HoldInvoiceIpcResponse>(ClientCommand.CancelHoldInvoice,
                                                                                     req, ct);
    }

    /// <summary>
    /// Pays a BOLT 11 invoice and waits for the outcome (ClientCommand 10).
    /// </summary>
    /// <param name="bolt11">The invoice.</param>
    /// <param name="amount">The amount when the invoice has none.</param>
    /// <param name="timeoutSeconds">How long the daemon waits for the outcome and retries, or null for its default.
    /// </param>
    /// <param name="maxFeeMsat">The per-call fee limit in msat, or null for the daemon's default (NL-270).</param>
    /// <param name="maxParts">The most HTLCs in flight at once (1 never splits), or null for the daemon's default.
    /// </param>
    /// <param name="ct">Cancels the call (the payment itself keeps going in the daemon).</param>
    /// <param name="outgoingChannel">The only channel the payment may leave through (NL-609, <c>--out</c>), or null.
    /// </param>
    /// <param name="incomingChannel">For our own invoice, the only channel the payment may come back in through
    /// (NL-609, <c>--in</c>), or null.</param>
    /// <param name="labels">The operator's label and tags (NL-602 A3-T1), or null for none.</param>
    /// <param name="trampolineNode">The trampoline node to pay through (NL-875, <c>--trampoline</c>), or null.</param>
    public Task<PayInvoiceIpcResponse> PayInvoiceAsync(string bolt11, LightningMoney? amount, uint? timeoutSeconds,
                                                       ulong? maxFeeMsat = null, uint? maxParts = null,
                                                       CancellationToken ct = default, string? outgoingChannel = null,
                                                       string? incomingChannel = null, LabelArguments? labels = null,
                                                       CompactPubKey? trampolineNode = null)
    {
        var req = new PayInvoiceIpcRequest
        {
            Bolt11 = bolt11,
            Amount = amount,
            TimeoutSeconds = timeoutSeconds,
            MaxFee = maxFeeMsat is { } fee ? LightningMoney.MilliSatoshis(fee) : null,
            MaxParts = maxParts,
            OutgoingChannel = outgoingChannel,
            IncomingChannel = incomingChannel,
            TrampolineNode = trampolineNode,
            Label = labels?.Label,
            Tags = labels?.TagsOrNull
        };
        return SendRequestAsync<PayInvoiceIpcRequest, PayInvoiceIpcResponse>(ClientCommand.PayInvoice, req, ct);
    }

    /// <summary>
    /// Starts the cooperative close of a channel and waits for the closing transaction (ClientCommand 13).
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="feeRatePerKw">The feerate of our closing fee estimate, or null for the daemon's.</param>
    /// <param name="noFeeRange">Negotiate without <c>fee_range</c>.</param>
    /// <param name="waitSeconds">How long the daemon waits for the closing transaction, or null for its default.</param>
    /// <param name="ct">Cancels the call (the close itself goes on in the daemon).</param>
    /// <param name="force">Close even while a liquidity lease we sold on the channel is in force (NL-850).</param>
    public Task<CloseChannelIpcResponse> CloseChannelAsync(ChannelId channelId, uint? feeRatePerKw, bool noFeeRange,
                                                           uint? waitSeconds, CancellationToken ct = default,
                                                           bool force = false)
    {
        var req = new CloseChannelIpcRequest
        {
            ChannelId = channelId,
            FeeRatePerKw = feeRatePerKw,
            NoFeeRange = noFeeRange,
            WaitSeconds = waitSeconds,
            Force = force
        };
        return SendRequestAsync<CloseChannelIpcRequest, CloseChannelIpcResponse>(ClientCommand.CloseChannel, req,
                                                                                 ct);
    }

    /// <summary>
    /// Fails a channel and broadcasts our latest commitment (ClientCommand 14).
    /// </summary>
    public Task<ForceCloseChannelIpcResponse> ForceCloseChannelAsync(ChannelId channelId,
                                                                     CancellationToken ct = default)
    {
        var req = new ForceCloseChannelIpcRequest { ChannelId = channelId };
        return SendRequestAsync<ForceCloseChannelIpcRequest, ForceCloseChannelIpcResponse>(
            ClientCommand.ForceCloseChannel, req, ct);
    }

    /// <summary>
    /// Disconnects a peer (ClientCommand 24); refused while its channels have HTLCs in flight unless forced.
    /// </summary>
    /// <param name="nodeId">The peer.</param>
    /// <param name="force">Disconnect even with HTLCs in flight.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<DisconnectPeerIpcResponse> DisconnectPeerAsync(CompactPubKey nodeId, bool force,
                                                               CancellationToken ct = default)
    {
        var req = new DisconnectPeerIpcRequest { NodeId = nodeId, Force = force };
        return SendRequestAsync<DisconnectPeerIpcRequest, DisconnectPeerIpcResponse>(ClientCommand.DisconnectPeer,
                                                                                     req, ct);
    }

    /// <summary>
    /// Pays an external address from the on-chain wallet (ClientCommand 25).
    /// </summary>
    /// <param name="address">The destination address.</param>
    /// <param name="amountSat">The amount in sats; null sends everything the wallet may spend ("all").</param>
    /// <param name="satPerVbyte">The fee rate in sat/vB; null for the node's estimate.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <param name="labels">The operator's label and tags (NL-602 A3-T1), or null for none.</param>
    public Task<WithdrawIpcResponse> WithdrawAsync(string address, ulong? amountSat, ulong? satPerVbyte,
                                                   CancellationToken ct = default, LabelArguments? labels = null)
    {
        var req = new WithdrawIpcRequest
        {
            Address = address,
            AmountSat = amountSat,
            SatPerVbyte = satPerVbyte,
            Label = labels?.Label,
            Tags = labels?.TagsOrNull
        };
        return SendRequestAsync<WithdrawIpcRequest, WithdrawIpcResponse>(ClientCommand.Withdraw, req, ct);
    }

    /// <summary>
    /// Splices wallet funds into a channel (ClientCommand 33) and waits for the negotiation (bounded by the daemon).
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="amountSat">The amount added to our channel balance, in sats.</param>
    /// <param name="feeRatePerKw">The splice transaction's feerate in sat/kw; null for the node's estimate.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <param name="requestInboundSat">Inbound liquidity to buy with the splice (NL-850), or null.</param>
    /// <param name="maxLiquidityFeeSat">The most we pay for it, or null for the node's limit.</param>
    public Task<SpliceIpcResponse> SpliceInAsync(ChannelId channelId, ulong amountSat, uint? feeRatePerKw,
                                                 CancellationToken ct = default, ulong? requestInboundSat = null,
                                                 ulong? maxLiquidityFeeSat = null)
    {
        var req = new SpliceInIpcRequest
        {
            ChannelId = channelId,
            AmountSat = amountSat,
            FeeRatePerKw = feeRatePerKw,
            RequestInboundSat = requestInboundSat,
            MaxLiquidityFeeSat = maxLiquidityFeeSat
        };
        return SendRequestAsync<SpliceInIpcRequest, SpliceIpcResponse>(ClientCommand.SpliceIn, req, ct);
    }

    /// <summary>
    /// Splices funds out of a channel to an address or our wallet (ClientCommand 34) and waits for the negotiation
    /// (bounded by the daemon).
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="amountSat">The amount taken out of our channel balance, in sats.</param>
    /// <param name="address">The destination; null for a new address of the node's wallet.</param>
    /// <param name="feeRatePerKw">The splice transaction's feerate in sat/kw; null for the node's estimate.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<SpliceIpcResponse> SpliceOutAsync(ChannelId channelId, ulong amountSat, string? address,
                                                  uint? feeRatePerKw, CancellationToken ct = default)
    {
        var req = new SpliceOutIpcRequest
        {
            ChannelId = channelId,
            AmountSat = amountSat,
            Address = address,
            FeeRatePerKw = feeRatePerKw
        };
        return SendRequestAsync<SpliceOutIpcRequest, SpliceIpcResponse>(ClientCommand.SpliceOut, req, ct);
    }

    /// <summary>
    /// RBFs a channel's pending splice at a higher feerate (ClientCommand 37) and waits for the new attempt (bounded by
    /// the daemon).
    /// </summary>
    /// <param name="channelId">The channel whose pending splice is bumped.</param>
    /// <param name="feeRatePerKw">The new attempt's feerate in sat/kw (at least the RBF minimum of the latest
    /// attempt).</param>
    /// <param name="maxFeeSat">The most our side may pay for the new attempt, in sats; null for no cap.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<SpliceIpcResponse> BumpSpliceAsync(ChannelId channelId, uint feeRatePerKw, ulong? maxFeeSat,
                                                   CancellationToken ct = default)
    {
        var req = new BumpSpliceIpcRequest
        {
            ChannelId = channelId,
            FeeRatePerKw = feeRatePerKw,
            MaxFeeSat = maxFeeSat
        };
        return SendRequestAsync<BumpSpliceIpcRequest, SpliceIpcResponse>(ClientCommand.BumpSplice, req, ct);
    }

    /// <summary>
    /// RBFs our unconfirmed dual-funded open at a higher feerate (ClientCommand 38) and waits for the new attempt.
    /// </summary>
    /// <param name="channelId">The dual-funded channel whose funding is bumped.</param>
    /// <param name="feeRatePerKw">The new attempt's feerate in sat/kw (at least the RBF minimum of the latest
    /// attempt).</param>
    /// <param name="contributionSat">Our new contribution in sats; null keeps it.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<BumpOpenIpcResponse> BumpOpenAsync(ChannelId channelId, uint feeRatePerKw, ulong? contributionSat,
                                                   CancellationToken ct = default, ulong? requestInboundSat = null,
                                                   ulong? maxLiquidityFeeSat = null)
    {
        var req = new BumpOpenIpcRequest
        {
            ChannelId = channelId,
            FeeRatePerKw = feeRatePerKw,
            ContributionSat = contributionSat,
            RequestInboundSat = requestInboundSat,
            MaxLiquidityFeeSat = maxLiquidityFeeSat
        };
        return SendRequestAsync<BumpOpenIpcRequest, BumpOpenIpcResponse>(ClientCommand.BumpOpen, req, ct);
    }

    /// <summary>
    /// Liquidity ads (ClientCommand 47, NL-850): our rates, the sellers we know of or our purchases.
    /// </summary>
    public Task<LiquidityAdsIpcResponse> LiquidityAdsAsync(LiquidityAdsIpcRequest request,
                                                           CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRequestAsync<LiquidityAdsIpcRequest, LiquidityAdsIpcResponse>(ClientCommand.LiquidityAds, request,
                                                                                 ct);
    }

    /// <summary>
    /// Lists the latest peer_storage_retrieval of each peer and the blobs we keep for our peers (ClientCommand 32).
    /// </summary>
    /// <param name="peerNodeId">Only this peer; null for every peer.</param>
    /// <param name="includeBlob">Also return each retrieved blob's bytes.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<ListPeerStorageIpcResponse> ListPeerStorageAsync(CompactPubKey? peerNodeId, bool includeBlob,
                                                                 CancellationToken ct = default)
    {
        var req = new ListPeerStorageIpcRequest { PeerNodeId = peerNodeId, IncludeBlob = includeBlob };
        return SendRequestAsync<ListPeerStorageIpcRequest, ListPeerStorageIpcResponse>(ClientCommand.ListPeerStorage,
                                                                                       req, ct);
    }

    /// <summary>
    /// Sets or resets a channel's routing policy (ClientCommand 35, wave sp1 lane SP1-G).
    /// </summary>
    public Task<ChannelPolicyIpcResponse> SetChannelPolicyAsync(SetChannelPolicyIpcRequest request,
                                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRequestAsync<SetChannelPolicyIpcRequest, ChannelPolicyIpcResponse>(ClientCommand.SetChannelPolicy,
                                                                                      request, ct);
    }

    /// <summary>
    /// Reads a channel's routing policy in force (ClientCommand 36, wave sp1 lane SP1-G).
    /// </summary>
    public Task<ChannelPolicyIpcResponse> GetChannelPolicyAsync(GetChannelPolicyIpcRequest request,
                                                                CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRequestAsync<GetChannelPolicyIpcRequest, ChannelPolicyIpcResponse>(ClientCommand.GetChannelPolicy,
                                                                                      request, ct);
    }

    /// <summary>
    /// Creates one of our BOLT 12 offers (ClientCommand 26).
    /// </summary>
    public Task<CreateOfferIpcResponse> CreateOfferAsync(CreateOfferIpcRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendRequestAsync<CreateOfferIpcRequest, CreateOfferIpcResponse>(ClientCommand.CreateOffer,
                                                                               request, ct);
    }

    /// <summary>
    /// Lists our BOLT 12 offers, newest first (ClientCommand 27).
    /// </summary>
    public Task<ListOffersIpcResponse> ListOffersAsync(bool activeOnly, int skip, int take,
                                                       CancellationToken ct = default)
    {
        var req = new ListOffersIpcRequest { ActiveOnly = activeOnly, Skip = skip, Take = take };
        return SendRequestAsync<ListOffersIpcRequest, ListOffersIpcResponse>(ClientCommand.ListOffers, req, ct);
    }

    /// <summary>
    /// Disables one of our BOLT 12 offers (ClientCommand 28).
    /// </summary>
    public Task<DisableOfferIpcResponse> DisableOfferAsync(Hash offerId, CancellationToken ct = default)
    {
        var req = new DisableOfferIpcRequest { OfferId = offerId };
        return SendRequestAsync<DisableOfferIpcRequest, DisableOfferIpcResponse>(ClientCommand.DisableOffer,
                                                                                 req, ct);
    }

    /// <summary>
    /// Sends a spontaneous (keysend) payment and waits for the outcome (ClientCommand 31).
    /// </summary>
    /// <param name="arguments">The parsed <c>keysend</c> arguments.</param>
    /// <param name="ct">Cancels the call (the payment itself keeps going in the daemon).</param>
    /// <param name="labels">The operator's label and tags (NL-602 A3-T1), or null for none.</param>
    public Task<PayInvoiceIpcResponse> KeysendAsync(KeysendArguments arguments, CancellationToken ct = default,
                                                    LabelArguments? labels = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var req = new KeysendIpcRequest
        {
            Destination = arguments.Destination,
            Amount = LightningMoney.Satoshis(arguments.AmountSat),
            CustomRecords = arguments.CustomRecords.Count == 0
                                ? null
                                : arguments.CustomRecords.ToDictionary(pair => pair.Key, pair => pair.Value),
            TimeoutSeconds = arguments.TimeoutSeconds,
            MaxFee = arguments.MaxFeeMsat is { } fee ? LightningMoney.MilliSatoshis(fee) : null,
            Label = labels?.Label,
            Tags = labels?.TagsOrNull
        };
        return SendRequestAsync<KeysendIpcRequest, PayInvoiceIpcResponse>(ClientCommand.Keysend, req, ct);
    }

    /// <summary>
    /// Fetches an invoice for a BOLT 12 offer and pays it (ClientCommand 29).
    /// </summary>
    /// <param name="arguments">The parsed <c>payoffer</c> arguments.</param>
    /// <param name="ct">Cancels the call (a payment already started keeps going in the daemon).</param>
    /// <param name="labels">The operator's label and tags of the payment (NL-602 A3-T1), or null for none.</param>
    public Task<PayOfferIpcResponse> PayOfferAsync(PayOfferArguments arguments, CancellationToken ct = default,
                                                   LabelArguments? labels = null)
        => SendRequestAsync<PayOfferIpcRequest, PayOfferIpcResponse>(ClientCommand.PayOffer,
                                                                     ToRequest(arguments, labels), ct);

    /// <summary>
    /// Fetches and verifies an invoice for a BOLT 12 offer without paying it (ClientCommand 30).
    /// </summary>
    /// <param name="arguments">The parsed <c>fetchinvoice</c> arguments (the payment limits are ignored).</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<FetchInvoiceIpcResponse> FetchInvoiceAsync(PayOfferArguments arguments,
                                                           CancellationToken ct = default) =>
        SendRequestAsync<PayOfferIpcRequest, FetchInvoiceIpcResponse>(ClientCommand.FetchInvoice, ToRequest(arguments),
                                                                      ct);

    private static PayOfferIpcRequest ToRequest(PayOfferArguments arguments, LabelArguments? labels = null) => new()
    {
        Offer = arguments.Offer,
        Amount = arguments.AmountMsat is { } amount ? LightningMoney.MilliSatoshis(amount) : null,
        Quantity = arguments.Quantity,
        PayerNote = arguments.PayerNote,
        TimeoutSeconds = arguments.TimeoutSeconds,
        MaxFee = arguments.MaxFeeMsat is { } fee ? LightningMoney.MilliSatoshis(fee) : null,
        MaxParts = arguments.MaxParts,
        Label = labels?.Label,
        Tags = labels?.TagsOrNull,
        TrampolineNode = arguments.TrampolineNode
    };

    /// <summary>
    /// Lists the on-chain resolution of closed channels (ClientCommand 15).
    /// </summary>
    /// <param name="channelId">Only this channel, when set.</param>
    /// <param name="includeClosed">Also the channels already closed.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<PendingSweepsIpcResponse> PendingSweepsAsync(ChannelId? channelId, bool includeClosed,
                                                             CancellationToken ct = default)
    {
        var req = new PendingSweepsIpcRequest { ChannelId = channelId, IncludeClosed = includeClosed };
        return SendRequestAsync<PendingSweepsIpcRequest, PendingSweepsIpcResponse>(ClientCommand.PendingSweeps, req,
                                                                                   ct);
    }

    /// <summary>
    /// Exports the static channel backup, encrypted to the node key (ClientCommand 24).
    /// </summary>
    /// <param name="channelId">Only this channel, when set.</param>
    /// <param name="ct">Cancels the call.</param>
    public Task<ExportChanBackupIpcResponse> ExportChanBackupAsync(ChannelId? channelId,
                                                                   CancellationToken ct = default) =>
        SendRequestAsync<ExportChanBackupIpcRequest, ExportChanBackupIpcResponse>(
            ClientCommand.ExportChanBackup, new ExportChanBackupIpcRequest { ChannelId = channelId }, ct);

    /// <summary>
    /// Checks a static channel backup against the node's key, chain and key derivation (ClientCommand 22).
    /// </summary>
    public Task<VerifyChanBackupIpcResponse> VerifyChanBackupAsync(byte[] backup, CancellationToken ct = default) =>
        SendRequestAsync<VerifyChanBackupIpcRequest, VerifyChanBackupIpcResponse>(
            ClientCommand.VerifyChanBackup, new VerifyChanBackupIpcRequest { Backup = backup }, ct);

    /// <summary>
    /// Restores a static channel backup of this node as recovery channels (ClientCommand 23).
    /// </summary>
    public Task<RestoreChanBackupIpcResponse> RestoreChanBackupAsync(byte[] backup, CancellationToken ct = default) =>
        SendRequestAsync<RestoreChanBackupIpcRequest, RestoreChanBackupIpcResponse>(
            ClientCommand.RestoreChanBackup, new RestoreChanBackupIpcRequest { Backup = backup }, ct);

    /// <summary>
    /// Whether the node's chain processing is halted and what it refuses meanwhile (ClientCommand 16, NL-216).
    /// </summary>
    public Task<ChainStatusIpcResponse> ChainStatusAsync(CancellationToken ct = default) =>
        SendRequestAsync<ChainStatusIpcRequest, ChainStatusIpcResponse>(ClientCommand.ChainStatus,
                                                                         new ChainStatusIpcRequest(), ct);

    /// <summary>
    /// Stops the node gracefully (ClientCommand 39, NL-591/NL-592): plain (refused while HTLCs are in flight),
    /// <c>wait</c> (drain until idle, at most <paramref name="timeoutSeconds"/>; 0 means the node's default) and/or
    /// <c>force</c> (stop although something is in flight).
    /// </summary>
    public Task<ShutdownIpcResponse> ShutdownAsync(bool wait = false, int timeoutSeconds = 0, bool force = false,
                                                   CancellationToken ct = default) =>
        SendRequestAsync<ShutdownIpcRequest, ShutdownIpcResponse>(ClientCommand.Shutdown,
                                                                   new ShutdownIpcRequest
                                                                   {
                                                                       Wait = wait,
                                                                       TimeoutSeconds = timeoutSeconds,
                                                                       Force = force
                                                                   }, ct);

    /// <summary>
    /// Lists the announced nodes of the gossip graph, or only <paramref name="nodeId"/> (ClientCommand 17).
    /// </summary>
    public Task<ListNodesIpcResponse> ListNodesAsync(CompactPubKey? nodeId, CancellationToken ct = default) =>
        SendRequestAsync<ListNodesIpcRequest, ListNodesIpcResponse>(ClientCommand.ListNodes,
                                                                     new ListNodesIpcRequest { NodeId = nodeId }, ct);

    /// <summary>
    /// Lists the channels of the gossip graph, optionally one short channel id and/or one node's (ClientCommand 18).
    /// </summary>
    public Task<ListGraphChannelsIpcResponse> ListGraphChannelsAsync(ulong? shortChannelId, CompactPubKey? nodeId,
                                                                     CancellationToken ct = default) =>
        SendRequestAsync<ListGraphChannelsIpcRequest, ListGraphChannelsIpcResponse>(
            ClientCommand.ListGraphChannels,
            new ListGraphChannelsIpcRequest { ShortChannelId = shortChannelId, NodeId = nodeId }, ct);

    /// <summary>
    /// The route a payment of <paramref name="amountMsat"/> to <paramref name="nodeId"/> would take now
    /// (ClientCommand 19); nothing is sent. With <paramref name="trampolineNode"/>, the outer route to it around its
    /// cached (or the default) trampoline policy, which the response's <c>Trampoline</c> section carries (NL-940).
    /// </summary>
    public Task<GetRouteIpcResponse> GetRouteAsync(CompactPubKey nodeId, ulong amountMsat, ulong? maxFeeMsat,
                                                   ushort? finalCltvDelta, CompactPubKey? trampolineNode = null,
                                                   CancellationToken ct = default) =>
        SendRequestAsync<GetRouteIpcRequest, GetRouteIpcResponse>(ClientCommand.GetRoute,
                                                                  new GetRouteIpcRequest
                                                                  {
                                                                      NodeId = nodeId,
                                                                      AmountMsat = amountMsat,
                                                                      MaxFeeMsat = maxFeeMsat,
                                                                      FinalCltvDelta = finalCltvDelta,
                                                                      TrampolineNode = trampolineNode
                                                                  }, ct);

    /// <summary>
    /// The gossip graph's state, with an optional page of channels and of nodes (ClientCommand 20).
    /// </summary>
    public Task<DescribeGraphIpcResponse> DescribeGraphAsync(bool includeChannels, bool includeNodes, int offset,
                                                             int limit, CancellationToken ct = default) =>
        SendRequestAsync<DescribeGraphIpcRequest, DescribeGraphIpcResponse>(ClientCommand.DescribeGraph,
                                                                            new DescribeGraphIpcRequest
                                                                            {
                                                                                IncludeChannels = includeChannels,
                                                                                IncludeNodes = includeNodes,
                                                                                Offset = offset,
                                                                                Limit = limit
                                                                            }, ct);

    /// <summary>
    /// Lists a page of our invoices, newest first (ClientCommand 11).
    /// </summary>
    public Task<ListInvoicesIpcResponse> ListInvoicesAsync(int skip, int take, CancellationToken ct = default)
    {
        var req = new ListInvoicesIpcRequest { Skip = skip, Take = take };
        return SendRequestAsync<ListInvoicesIpcRequest, ListInvoicesIpcResponse>(ClientCommand.ListInvoices, req, ct);
    }

    /// <summary>
    /// Lists a page of our outgoing payments, newest first (ClientCommand 12); the outgoing legs of trampoline relays
    /// only with <paramref name="includeRelayLegs"/> (NL-899).
    /// </summary>
    public Task<ListPaymentsIpcResponse> ListPaymentsAsync(int skip, int take, CancellationToken ct = default,
                                                           bool includeRelayLegs = false)
    {
        var req = new ListPaymentsIpcRequest { Skip = skip, Take = take, IncludeRelayLegs = includeRelayLegs };
        return SendRequestAsync<ListPaymentsIpcRequest, ListPaymentsIpcResponse>(ClientCommand.ListPayments, req, ct);
    }

    /// <summary>
    /// Lists our forwarded payments, newest first (ClientCommand 40, NL-597), with the refused-HTLC counters in the
    /// summary (NL-598); every filter is optional.
    /// </summary>
    public Task<ListForwardsIpcResponse> ListForwardsAsync(int skip, int take, long? sinceUnixSeconds = null,
                                                           long? untilUnixSeconds = null, byte? status = null,
                                                           string? channel = null, CancellationToken ct = default) =>
        SendRequestAsync<ListForwardsIpcRequest, ListForwardsIpcResponse>(ClientCommand.ListForwards,
                                                                          new ListForwardsIpcRequest
                                                                          {
                                                                              Skip = skip,
                                                                              Take = take,
                                                                              SinceUnixSeconds = sinceUnixSeconds,
                                                                              UntilUnixSeconds = untilUnixSeconds,
                                                                              Status = status,
                                                                              Channel = channel
                                                                          }, ct);

    /// <summary>
    /// Lists the sealed accounting events after a ledger sequence (ClientCommand 41, NL-602); every filter is
    /// optional.
    /// </summary>
    public Task<ListAccountingEventsIpcResponse> ListAccountingEventsAsync(ListAccountingEventsIpcRequest request,
                                                                           CancellationToken ct = default) =>
        SendRequestAsync<ListAccountingEventsIpcRequest, ListAccountingEventsIpcResponse>(
            ClientCommand.ListAccountingEvents, request, ct);

    /// <summary>
    /// The node's live balances by bucket (ClientCommand 42, NL-602).
    /// </summary>
    public Task<AccountingSnapshotIpcResponse> AccountingSnapshotAsync(CancellationToken ct = default) =>
        SendRequestAsync<AccountingSnapshotIpcRequest, AccountingSnapshotIpcResponse>(
            ClientCommand.AccountingSnapshot, new AccountingSnapshotIpcRequest(), ct);

    /// <summary>
    /// A report of the operational books (ClientCommand 43, NL-602 A2).
    /// </summary>
    public Task<AccountingReportIpcResponse> AccountingReportAsync(AccountingReportIpcRequest request,
                                                                   CancellationToken ct = default) =>
        SendRequestAsync<AccountingReportIpcRequest, AccountingReportIpcResponse>(ClientCommand.AccountingReport,
                                                                                  request, ct);

    /// <summary>
    /// One page of an export of the books (ClientCommand 44, NL-602 A2).
    /// </summary>
    public Task<AccountingExportIpcResponse> AccountingExportAsync(AccountingExportIpcRequest request,
                                                                   CancellationToken ct = default) =>
        SendRequestAsync<AccountingExportIpcRequest, AccountingExportIpcResponse>(ClientCommand.AccountingExport,
                                                                                  request, ct);

    /// <summary>
    /// Reconcile, rebuild or verify the books (ClientCommand 45, NL-602 A2).
    /// </summary>
    public Task<AccountingAdminIpcResponse> AccountingAdminAsync(AccountingAdminIpcRequest request,
                                                                 CancellationToken ct = default) =>
        SendRequestAsync<AccountingAdminIpcRequest, AccountingAdminIpcResponse>(ClientCommand.AccountingAdmin,
                                                                                request, ct);

    /// <summary>
    /// Parses the `getaddress` argument. With no argument, the <see cref="GetAddressIpcRequest"/> default is used.
    /// </summary>
    internal static AddressType ParseAddressType(string? addressTypeString)
    {
        if (string.IsNullOrWhiteSpace(addressTypeString))
            return new GetAddressIpcRequest().AddressType;

        return addressTypeString.ToLowerInvariant() switch
        {
            "p2tr" => AddressType.P2Tr,
            "p2wpkh" => AddressType.P2Wpkh,
            "all" => AddressType.P2Tr | AddressType.P2Wpkh,
            _ => throw new ArgumentOutOfRangeException(nameof(addressTypeString), addressTypeString,
                                                       "Address has to be `p2tr`, `p2wpkh`, or `all`.")
        };
    }

    private async Task<TResponse> SendRequestAsync<TRequest, TResponse>(ClientCommand command, TRequest request,
                                                                       CancellationToken ct)
    {
        var env = new IpcEnvelope
        {
            Version = 1,
            Command = command,
            CorrelationId = Guid.NewGuid(),
            AuthToken = await GetAuthTokenAsync(ct),
            Payload = MessagePackSerializer.Serialize(request, cancellationToken: ct),
            Kind = IpcEnvelopeKind.Request
        };

        var respEnv = await SendAsync(env, ct);
        if (respEnv.Kind != IpcEnvelopeKind.Error)
            return MessagePackSerializer.Deserialize<TResponse>(respEnv.Payload, cancellationToken: ct);

        var err = MessagePackSerializer.Deserialize<IpcError>(respEnv.Payload, cancellationToken: ct);
        throw new InvalidOperationException($"IPC error {err.Code}: {err.Message}");
    }

    private async Task<IpcEnvelope> SendAsync(IpcEnvelope envelope, CancellationToken ct)
    {
        // CurrentUserOnly: the server must run as the same user, so another local user cannot stand in for the
        // daemon (e.g. after it stopped) and collect the cookie this request carries
        await using var client =
            new NamedPipeClientStream(_server, _namedPipeFilePath, PipeDirection.InOut,
                                      PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(2), ct);
        }
        catch (TimeoutException)
        {
            throw new IOException(
                "Could not connect to NLightning node IPC pipe. Ensure the node is running and listening for IPC.");
        }

        // Send request and read the response with the one shared framing (NL-154)
        await s_framing.WriteAsync(client, envelope, ct);
        return await s_framing.ReadAsync(client, ct);
    }

    private async Task<string> GetAuthTokenAsync(CancellationToken ct)
    {
        if (!File.Exists(_cookieFilePath))
            throw new IOException(
                "Authentication cookie file not found. Ensure the node is running and the cookie file path is correct.");

        var content = await File.ReadAllTextAsync(_cookieFilePath, ct);
        return content.Trim();
    }

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}