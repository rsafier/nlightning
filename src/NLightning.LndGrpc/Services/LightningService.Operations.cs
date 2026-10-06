using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.LndGrpc.Services;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Lnrpc;
using CommitmentType = Lnrpc.CommitmentType;
using LndChannelPoint = Lnrpc.ChannelPoint;

public sealed partial class LightningService
{
    /// <summary>How long OpenChannelSync/OpenChannel wait for the peer's answers before the funding is published.</summary>
    private static readonly TimeSpan s_openStepTimeout = TimeSpan.FromMinutes(3);

    /// <summary>How often the close stream re-reads the channel while it waits for a confirmation.</summary>
    private static readonly TimeSpan s_closePoll = TimeSpan.FromSeconds(1);

    /// <summary>
    /// <c>ConnectPeer</c>: dials <c>pubkey@host</c> (port 9735 when the host has none) and answers once the
    /// <c>init</c> exchange is done, within <c>timeout</c> seconds when set; with <c>perm</c> the dial runs in the
    /// background and the call answers at once, as LND's. An already connected peer answers with a status (LND 0.21).
    /// </summary>
    public override async Task<ConnectPeerResponse> ConnectPeer(ConnectPeerRequest request, ServerCallContext context)
    {
        if (request.Addr is null || string.IsNullOrWhiteSpace(request.Addr.Pubkey)
                                 || string.IsNullOrWhiteSpace(request.Addr.Host))
            throw InvalidArgument("addr needs the peer's pubkey and host");

        var nodeId = ParseNodeId(request.Addr.Pubkey);
        if (_peerManager.GetPeer(nodeId) is not null)
            return new ConnectPeerResponse { Status = $"already connected to peer: {request.Addr.Pubkey}" };

        var host = request.Addr.Host.Trim();
        var hasPort = host.StartsWith('[') ? host.Contains("]:", StringComparison.Ordinal) : host.Contains(':');
        var address = new PeerAddressInfo($"{nodeId}@{(hasPort ? host : host + ":9735")}");
        if (request.Perm)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _peerManager.ConnectToPeerAsync(address);
                }
                catch (Exception e)
                {
                    _logger.LogInformation(e, "LND gRPC persistent connect to {Address} failed", address.Address);
                }
            });
            return new ConnectPeerResponse { Status = $"connection to {request.Addr.Pubkey} initiated" };
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        if (request.Timeout > 0)
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(request.Timeout, 3_600)));
        try
        {
            await _peerManager.DialPeerAsync(address, timeout.Token);
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded,
                                              $"connection to {request.Addr.Pubkey} timed out"));
        }
        catch (Exception e) when (e is not (OperationCanceledException or RpcException))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, $"unable to connect to peer: {e.Message}"));
        }

        return new ConnectPeerResponse { Status = $"connection to {request.Addr.Pubkey} was successful" };
    }

    /// <summary>
    /// <c>DisconnectPeer</c>: refused while the peer has an open channel, as LND refuses (LND: "disconnect from peer
    /// with active channels"); otherwise the node's <c>disconnect</c>.
    /// </summary>
    public override async Task<DisconnectPeerResponse> DisconnectPeer(DisconnectPeerRequest request,
                                                                      ServerCallContext context)
    {
        var nodeId = ParseNodeId(request.PubKey);
        if (_channels.FindChannels(c => c.RemoteNodeId == nodeId && c.State == ChannelState.Open).Count > 0)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                              "cannot disconnect from peer(s) with active channels"));

        await DispatchAsync<DisconnectPeerClientRequest, DisconnectPeerClientResponse>(
            new DisconnectPeerClientRequest(nodeId), context);
        return new DisconnectPeerResponse { Status = $"disconnect from peer {request.PubKey} initiated" };
    }

    /// <summary>
    /// <c>OpenChannelSync</c>: the node's <c>openchannel</c> to a connected peer, answered once the funding transaction
    /// is published (LND answers there too, not at the confirmation). See <see cref="ToOpenRequest"/> for the fields.
    /// </summary>
    public override async Task<LndChannelPoint> OpenChannelSync(OpenChannelRequest request, ServerCallContext context)
    {
        var funded = await OpenUntilPublishedAsync(request, context);
        return ToChannelPoint(funded.TxId, funded.Index);
    }

    /// <summary>
    /// <c>OpenChannel</c>: <c>chan_pending</c> once the funding transaction is published, <c>chan_open</c> once the
    /// channel is ready (the funding is deep enough and <c>channel_ready</c> was exchanged). The open goes on when the
    /// caller leaves.
    /// </summary>
    public override async Task OpenChannel(OpenChannelRequest request, IServerStreamWriter<OpenStatusUpdate> responseStream,
                                           ServerCallContext context)
    {
        var funded = await OpenUntilPublishedAsync(request, context);
        await responseStream.WriteAsync(new OpenStatusUpdate
        {
            ChanPending = new PendingUpdate
            {
                Txid = ByteString.CopyFrom((byte[])funded.TxId!.Value),
                OutputIndex = funded.Index ?? 0
            }
        }, context.CancellationToken);

        while (true)
        {
            var state = await DispatchAsync<OpenChannelClientSubscriptionRequest,
                                OpenChannelClientSubscriptionResponse>(
                            new OpenChannelClientSubscriptionRequest(funded.ChannelId), context);
            if (state.ChannelState is ChannelState.ReadyForThem or ChannelState.ReadyForUs or ChannelState.Open)
            {
                await responseStream.WriteAsync(new OpenStatusUpdate
                {
                    ChanOpen = new ChannelOpenUpdate
                    {
                        ChannelPoint = ToChannelPoint(state.TxId ?? funded.TxId, state.Index ?? funded.Index)
                    }
                }, context.CancellationToken);
                return;
            }
        }
    }

    /// <summary>
    /// <c>CloseChannel</c>: cooperative (the node's <c>closechannel</c>, its fee from <c>sat_per_vbyte</c> or the
    /// estimator) or with <c>force</c> its <c>forceclosechannel</c>; <c>close_pending</c> with the closing (or
    /// commitment) transaction once known, <c>chan_close</c> once it confirmed. With <c>no_wait</c> one
    /// <c>close_instant</c> and the call ends. Refused: <c>delivery_address</c> (our close goes to a wallet address or
    /// the upfront script) and <c>max_fee_per_vbyte</c> (no cap is enforced on the negotiation).
    /// </summary>
    public override async Task CloseChannel(CloseChannelRequest request,
                                            IServerStreamWriter<CloseStatusUpdate> responseStream,
                                            ServerCallContext context)
    {
        if (!string.IsNullOrEmpty(request.DeliveryAddress))
            throw Unimplemented("delivery_address: the node closes to its own wallet or the upfront shutdown script");
        if (request.MaxFeePerVbyte != 0)
            throw Unimplemented("max_fee_per_vbyte is not enforced by this node; leave it unset");

        var channel = FindByChannelPoint(request.ChannelPoint)
                   ?? throw NotFound("unable to find channel");
        var pendingHtlcs = channel.Commitments?.Htlcs.Count ?? 0;
        TxId? closingTxId;
        if (request.Force)
        {
            var forced = await DispatchAsync<ForceCloseChannelClientRequest, ForceCloseChannelClientResponse>(
                             new ForceCloseChannelClientRequest(channel.ChannelId), context);
            closingTxId = forced.CommitmentTxId;
        }
        else
        {
            var closed = await DispatchAsync<CloseChannelClientRequest, CloseChannelClientResponse>(
                             new CloseChannelClientRequest(channel.ChannelId)
                             {
                                 FeeRatePerKw = request.SatPerVbyte > 0
                                                    ? checked((uint)(request.SatPerVbyte * 250))
                                                    : null,
                                 WaitSeconds = request.NoWait ? 0u : 60u
                             }, context);
            closingTxId = closed.ClosingTxId;
        }

        if (request.NoWait)
        {
            await responseStream.WriteAsync(new CloseStatusUpdate
            {
                CloseInstant = new InstantUpdate { NumPendingHtlcs = pendingHtlcs }
            }, context.CancellationToken);
            return;
        }

        // The closing transaction once the negotiation agreed on it (or the commitment we broadcast)
        while (closingTxId is null)
        {
            await Task.Delay(s_closePoll, _timeProvider, context.CancellationToken);
            closingTxId = await ClosingTxIdAsync(channel.ChannelId);
        }

        await responseStream.WriteAsync(new CloseStatusUpdate
        {
            ClosePending = new PendingUpdate { Txid = ByteString.CopyFrom((byte[])closingTxId.Value) }
        }, context.CancellationToken);

        // Confirmed: a mutual close is Closed, a force close is resolving its outputs on chain (or done)
        while (!await IsCloseConfirmedAsync(channel.ChannelId, request.Force))
            await Task.Delay(s_closePoll, _timeProvider, context.CancellationToken);

        await responseStream.WriteAsync(new CloseStatusUpdate
        {
            ChanClose = new ChannelCloseUpdate
            {
                ClosingTxid = ByteString.CopyFrom((byte[])closingTxId.Value),
                Success = true
            }
        }, context.CancellationToken);
    }

    /// <summary>
    /// <c>UpdateChannelPolicy</c>: the node's <c>setchannelpolicy</c> on one channel (<c>chan_point</c>) or, with
    /// <c>global</c>, on every Open channel (the node's configured default policy itself is not changed at run time). As
    /// LND, the base fee, the fee rate (<c>fee_rate_ppm</c>, else <c>fee_rate</c> x 1,000,000) and the time lock delta
    /// are always applied; <c>max_htlc_msat</c> when non-zero, <c>min_htlc_msat</c> with
    /// <c>min_htlc_msat_specified</c>. Inbound fees are refused. A channel the node refuses is a failed update.
    /// </summary>
    public override async Task<PolicyUpdateResponse> UpdateChannelPolicy(PolicyUpdateRequest request,
                                                                          ServerCallContext context)
    {
        if (request.InboundFee is { } inbound && (inbound.BaseFeeMsat != 0 || inbound.FeeRatePpm != 0))
            throw Unimplemented("inbound fees are not set by this node");
        if (request.BaseFeeMsat is < 0 or > uint.MaxValue)
            throw InvalidArgument("base_fee_msat is out of range");
        if (request.TimeLockDelta > ushort.MaxValue)
            throw InvalidArgument("time_lock_delta is out of range");

        var feeRatePpm = request.FeeRatePpm != 0 ? request.FeeRatePpm : (uint)Math.Round(request.FeeRate * 1_000_000);
        List<ChannelModel> channels;
        if (request.ScopeCase == PolicyUpdateRequest.ScopeOneofCase.ChanPoint)
            channels = [FindByChannelPoint(request.ChanPoint) ?? throw NotFound("unable to find channel")];
        else if (request.Global)
            channels = _channels.FindChannels(c => c.State == ChannelState.Open);
        else
            throw InvalidArgument("set global or chan_point");

        var response = new PolicyUpdateResponse();
        foreach (var channel in channels)
        {
            var update = new SetChannelPolicyClientRequest(new ChannelReference(channel.ChannelId))
            {
                FeeBaseMsat = (uint)request.BaseFeeMsat,
                FeeProportionalMillionths = feeRatePpm,
                CltvExpiryDelta = (ushort)request.TimeLockDelta,
                HtlcMaximumMsat = request.MaxHtlcMsat != 0 ? request.MaxHtlcMsat : null,
                HtlcMinimumMsat = request.MinHtlcMsatSpecified ? request.MinHtlcMsat : null
            };
            try
            {
                await DispatchAsync<SetChannelPolicyClientRequest, ChannelPolicyClientResponse>(update, context);
            }
            catch (RpcException e)
            {
                response.FailedUpdates.Add(new FailedUpdate
                {
                    Outpoint = ToOutPoint(channel),
                    Reason = e.StatusCode == StatusCode.NotFound
                                 ? UpdateFailure.NotFound
                                 : UpdateFailure.InvalidParameter,
                    UpdateError = e.Status.Detail
                });
            }
        }

        return response;
    }

    /// <summary>
    /// <c>SendCoins</c>: the node's <c>withdraw</c> (confirmed wallet outputs, the anchors reserve kept), the fee from
    /// <c>sat_per_vbyte</c> or the estimator; <c>send_all</c> sends everything spendable. Refused: chosen
    /// <c>outpoints</c>, <c>spend_unconfirmed</c>, a coin selection strategy and <c>min_confs</c> other than 0/1.
    /// </summary>
    public override async Task<SendCoinsResponse> SendCoins(SendCoinsRequest request, ServerCallContext context)
    {
        if (request.Outpoints.Count > 0 || request.SpendUnconfirmed || request.MinConfs > 1
         || request.CoinSelectionStrategy != CoinSelectionStrategy.StrategyUseGlobalConfig)
            throw Unimplemented("outpoints, spend_unconfirmed, min_confs and coin_selection_strategy are not supported");
        if (request.SendAll == (request.Amount != 0))
            throw InvalidArgument("set either amount or send_all");
        if (request.Amount < 0)
            throw InvalidArgument("amount cannot be negative");

        var result = await DispatchAsync<WithdrawClientRequest, WithdrawClientResponse>(
                         new WithdrawClientRequest(request.Addr, request.SendAll ? null : (ulong)request.Amount,
                                                   request.SatPerVbyte == 0 ? null : request.SatPerVbyte)
                         {
                             Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label
                         }, context);
        return new SendCoinsResponse { Txid = result.TxId.ToString() };
    }

    /// <summary>
    /// <c>NewAddress</c>: a fresh wallet address (handed out once, NL-280): P2WPKH for the witness types, P2TR for the
    /// taproot ones; nested P2SH is refused. One account, <c>default</c>.
    /// </summary>
    public override async Task<NewAddressResponse> NewAddress(NewAddressRequest request, ServerCallContext context)
    {
        if (request.Account is { Length: > 0 } account && account != "default")
            throw NotFound($"account {account} not found");

        var type = request.Type switch
        {
            Lnrpc.AddressType.WitnessPubkeyHash or Lnrpc.AddressType.UnusedWitnessPubkeyHash =>
                Domain.Bitcoin.Enums.AddressType.P2Wpkh,
            Lnrpc.AddressType.TaprootPubkey or Lnrpc.AddressType.UnusedTaprootPubkey =>
                Domain.Bitcoin.Enums.AddressType.P2Tr,
            _ => throw Unimplemented("nested P2SH addresses are not supported")
        };
        await using var scope = CreateScope();
        var wallet = scope.ServiceProvider.GetService<IBitcoinWalletService>()
                  ?? throw new RpcException(new Status(StatusCode.Unavailable, "the wallet is not available"));
        var address = await wallet.GetUnusedAddressAsync(type, false);
        return new NewAddressResponse { Address = address.Address };
    }

    /// <summary>
    /// The node's open up to the published funding: the open handler, then the open subscription until the channel
    /// runs on a published funding transaction (or is already ready).
    /// </summary>
    private async Task<OpenChannelClientSubscriptionResponse> OpenUntilPublishedAsync(OpenChannelRequest request,
                                                                                     ServerCallContext context)
    {
        var open = ToOpenRequest(request);
        OpenChannelClientResponse opened;
        using (var step = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken))
        {
            step.CancelAfter(s_openStepTimeout);
            opened = await DispatchAsync<OpenChannelClientRequest, OpenChannelClientResponse>(open, step.Token);
        }

        while (true)
        {
            using var step = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            step.CancelAfter(s_openStepTimeout);
            var state = await DispatchAsync<OpenChannelClientSubscriptionRequest,
                                OpenChannelClientSubscriptionResponse>(
                            new OpenChannelClientSubscriptionRequest(opened.ChannelId)
                            {
                                ReportFundingChanges = true
                            }, step.Token);
            if (state.TxId is not null)
                return state;
        }
    }

    /// <summary>
    /// LND's open request as ours: <c>node_pubkey</c> (a connected peer), <c>local_funding_amount</c>,
    /// <c>push_sat</c>, <c>private</c> (LND's default is a public channel, so is ours), <c>sat_per_vbyte</c> as the
    /// feerate, the limits we announce (<c>min_htlc_msat</c>, <c>remote_csv_delay</c>,
    /// <c>remote_max_value_in_flight_msat</c>, <c>remote_max_htlcs</c>, <c>remote_chan_reserve_sat</c>),
    /// <c>zero_conf</c>, <c>commitment_type</c> ANCHORS (or unset: the node's choice) or TAPROOT/SIMPLE_TAPROOT (a
    /// simple taproot channel, experimental), <c>memo</c> as the label. Refused: STATIC_REMOTE_KEY/LEGACY/leases,
    /// <c>scid_alias</c> without <c>zero_conf</c>, funding shims, <c>fund_max</c>, chosen <c>outpoints</c>,
    /// <c>close_address</c>, <c>max_local_csv</c>, fee policies at the open and <c>min_confs</c> 0.
    /// <c>target_conf</c> is ignored (the estimator decides).
    /// </summary>
    internal static OpenChannelClientRequest ToOpenRequest(OpenChannelRequest request)
    {
        if (request.FundingShim is not null || request.FundMax || request.Outpoints.Count > 0)
            throw Unimplemented("funding shims, fund_max and chosen outpoints are not supported");
        if (!string.IsNullOrEmpty(request.CloseAddress))
            throw Unimplemented("close_address is not supported; the node reserves its own upfront shutdown address");
        if (request.MaxLocalCsv != 0 || request.UseBaseFee || request.UseFeeRate || request.SpendUnconfirmed)
            throw Unimplemented("max_local_csv, fee policies at the open and spend_unconfirmed are not supported");
        if (request.ScidAlias && !request.ZeroConf)
            throw Unimplemented("scid_alias is only negotiated for a zero-conf channel");
        if (request.LocalFundingAmount <= 0)
            throw InvalidArgument("local_funding_amount must be positive");
        if (request.PushSat < 0 || request.PushSat >= request.LocalFundingAmount)
            throw InvalidArgument("push_sat must be below the funding amount");

        var taproot = request.CommitmentType switch
        {
            CommitmentType.UnknownCommitmentType or CommitmentType.Anchors => false,
            CommitmentType.Taproot or CommitmentType.SimpleTaproot => true,
            _ => throw Unimplemented($"commitment type {request.CommitmentType} is not opened by this node")
        };
        var nodeId = request.NodePubkey.Length > 0
                         ? Convert.ToHexStringLower(request.NodePubkey.ToByteArray())
                         : request.NodePubkeyString;
        if (string.IsNullOrEmpty(nodeId) || nodeId.Length != 66)
            throw InvalidArgument("node_pubkey must be a 33-byte public key");

        return new OpenChannelClientRequest(nodeId, LightningMoney.Satoshis(request.LocalFundingAmount))
        {
            PushAmount = request.PushSat > 0 ? LightningMoney.Satoshis(request.PushSat) : null,
            IsPublic = !request.Private,
            FeeRatePerKw = request.SatPerVbyte > 0 ? LightningMoney.Satoshis(request.SatPerVbyte * 250) : null,
            HtlcMinimumAmount = request.MinHtlcMsat > 0 ? LightningMoney.MilliSatoshis(request.MinHtlcMsat) : null,
            ToSelfDelay = request.RemoteCsvDelay > 0 ? checked((ushort)request.RemoteCsvDelay) : null,
            MaxHtlcValueInFlight = request.RemoteMaxValueInFlightMsat > 0
                                       ? LightningMoney.MilliSatoshis(request.RemoteMaxValueInFlightMsat)
                                       : null,
            MaxAcceptedHtlcs = request.RemoteMaxHtlcs > 0 ? checked((ushort)request.RemoteMaxHtlcs) : null,
            ChannelReserveAmount = request.RemoteChanReserveSat > 0
                                       ? LightningMoney.Satoshis(request.RemoteChanReserveSat)
                                       : null,
            IsZeroConfChannel = request.ZeroConf,
            IsSimpleTaproot = taproot,
            Label = string.IsNullOrWhiteSpace(request.Memo) ? null : request.Memo
        };
    }

    /// <summary>Runs a node command, its refusals as gRPC statuses.</summary>
    private Task<TResponse> DispatchAsync<TRequest, TResponse>(TRequest request, ServerCallContext context)
        where TRequest : notnull =>
        DispatchAsync<TRequest, TResponse>(request, context.CancellationToken);

    private async Task<TResponse> DispatchAsync<TRequest, TResponse>(TRequest request,
                                                                     CancellationToken cancellationToken)
        where TRequest : notnull
    {
        var dispatcher = _dispatcher
                      ?? throw new RpcException(new Status(StatusCode.Unimplemented,
                                                           "node commands are not available on this host"));
        try
        {
            return await dispatcher.DispatchAsync<TRequest, TResponse>(request, cancellationToken);
        }
        catch (ClientException e)
        {
            throw new RpcException(new Status(e.ErrorCode switch
            {
                ErrorCodes.InvalidChannel => StatusCode.NotFound,
                ErrorCodes.InvalidAddress => StatusCode.InvalidArgument,
                ErrorCodes.ConnectionError => StatusCode.Unavailable,
                ErrorCodes.ServerError => StatusCode.Internal,
                _ => StatusCode.FailedPrecondition
            }, e.Message));
        }
    }

    /// <summary>Our channel at LND's <c>ChannelPoint</c> (bytes in internal order, or the displayed txid string).</summary>
    private ChannelModel? FindByChannelPoint(LndChannelPoint? point)
    {
        if (point is null)
            throw InvalidArgument("channel_point is required");

        var txId = point.FundingTxidCase == LndChannelPoint.FundingTxidOneofCase.FundingTxidBytes
                       ? (TxId)point.FundingTxidBytes.ToByteArray()
                       : ParseChannelPoint($"{point.FundingTxidStr}:{point.OutputIndex}").TxId;
        return _channels.FindChannels(c => c.FundingOutput is { TransactionId: { } id, Index: { } index }
                                        && id == txId && index == point.OutputIndex)
                        .FirstOrDefault();
    }

    private static LndChannelPoint ToChannelPoint(TxId? txId, uint? index) => new()
    {
        FundingTxidBytes = txId is { } id ? ByteString.CopyFrom((byte[])id) : ByteString.Empty,
        OutputIndex = index ?? 0
    };

    private static OutPoint ToOutPoint(ChannelModel channel) => new()
    {
        TxidBytes = channel.FundingOutput?.TransactionId is { } id ? ByteString.CopyFrom((byte[])id) : ByteString.Empty,
        TxidStr = channel.FundingOutput?.TransactionId?.ToString() ?? string.Empty,
        OutputIndex = channel.FundingOutput?.Index ?? 0
    };

    /// <summary>The closing transaction of a channel in memory, else from its stored row.</summary>
    private async Task<TxId?> ClosingTxIdAsync(ChannelId channelId)
    {
        if (_channels.TryGetChannel(channelId, out var channel) && channel.ClosingTransaction is { } closing)
            return closing.TxId;

        await using var scope = CreateScope();
        var unitOfWork = UnitOfWork(scope);
        if (await unitOfWork.OnchainResolutionDbRepository.GetCloseAsync(channelId) is { } close)
            return close.CommitmentTransactionId;

        return (await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId))?.ClosingTransaction?.TxId;
    }

    private async Task<bool> IsCloseConfirmedAsync(ChannelId channelId, bool force)
    {
        if (!_channels.TryGetChannelState(channelId, out var state))
        {
            await using var scope = CreateScope();
            state = (await UnitOfWork(scope).ChannelDbRepository.GetByIdAsync(channelId))?.State ?? ChannelState.Closed;
        }

        return force ? state >= ChannelState.OnchainResolving : state >= ChannelState.Closed;
    }

    private static CompactPubKey ParseNodeId(string hex)
    {
        try
        {
            var bytes = Convert.FromHexString(hex);
            return bytes.Length == 33 ? new CompactPubKey(bytes) : throw new FormatException();
        }
        catch (FormatException)
        {
            throw InvalidArgument("the public key must be 33 bytes in hex");
        }
    }
}