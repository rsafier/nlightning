using Google.Protobuf;
using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Lnrpc;
using CommitmentType = Lnrpc.CommitmentType;

public sealed partial class LightningService
{
    /// <summary>
    /// <c>ChannelBalance</c>: the LND-style balances (see <see cref="ListChannels"/>) summed over the channels
    /// <c>ListChannels</c> lists, the pending HTLCs by direction as the unsettled balances, and the channels whose
    /// funding is not locked yet as the pending-open balances.
    /// </summary>
    public override Task<ChannelBalanceResponse> ChannelBalance(ChannelBalanceRequest request,
                                                                ServerCallContext context)
    {
        ulong local = 0, remote = 0, unsettledLocal = 0, unsettledRemote = 0, pendingLocal = 0, pendingRemote = 0;
        foreach (var channel in _channels.FindChannels(_ => true))
        {
            var balances = LndBalances.Of(channel);
            if (IsListed(channel))
            {
                local += balances.LocalMsat;
                remote += balances.RemoteMsat;
                unsettledLocal += balances.OutgoingHtlcMsat;
                unsettledRemote += balances.IncomingHtlcMsat;
            }
            else if (IsPendingOpen(channel))
            {
                pendingLocal += balances.LocalMsat;
                pendingRemote += balances.RemoteMsat;
            }
        }

        return Task.FromResult(new ChannelBalanceResponse
        {
            Balance = (long)(local / 1000),
            PendingOpenBalance = (long)(pendingLocal / 1000),
            LocalBalance = Amount(local),
            RemoteBalance = Amount(remote),
            UnsettledLocalBalance = Amount(unsettledLocal),
            UnsettledRemoteBalance = Amount(unsettledRemote),
            PendingOpenLocalBalance = Amount(pendingLocal),
            PendingOpenRemoteBalance = Amount(pendingRemote)
        });
    }

    /// <summary>
    /// <c>ListChannels</c>: the Open channels and the ones closing cooperatively whose close transaction is not broadcast
    /// yet (ShuttingDown, Negotiating), as LND lists a channel until then.
    /// </summary>
    /// <remarks>
    /// <c>local_balance</c>/<c>remote_balance</c> follow LND: the <c>to_local</c>/<c>to_remote</c> amounts of our latest
    /// local commitment, i.e. without the pending HTLCs (our own <c>ChannelModel.LocalBalance</c> is gross, NL-062) and,
    /// for the funder, without the commitment fee and both anchors. <c>commit_fee</c> is that commitment's base fee
    /// (LND also counts trimmed HTLCs and rounding there). <c>chan_id</c> is the SCID as uint64, 0 before it is known.
    /// </remarks>
    public override Task<ListChannelsResponse> ListChannels(ListChannelsRequest request, ServerCallContext context)
    {
        if (request.ActiveOnly && request.InactiveOnly)
            throw InvalidArgument("either `active_only` or `inactive_only` can be set, but not both");
        if (request.PublicOnly && request.PrivateOnly)
            throw InvalidArgument("either `public_only` or `private_only` can be set, but not both");

        CompactPubKey? peer = null;
        if (request.Peer.Length > 0)
        {
            if (request.Peer.Length != 33)
                throw InvalidArgument("peer must be a 33-byte public key");
            peer = new CompactPubKey(request.Peer.ToByteArray());
        }

        var graph = request.PeerAliasLookup ? Graph : null;
        var response = new ListChannelsResponse();
        foreach (var channel in _channels.FindChannels(IsListed)
                                         .OrderBy(c => c.ShortChannelId.BlockHeight)
                                         .ThenBy(c => c.ShortChannelId.TransactionIndex))
        {
            var active = IsActive(channel);
            if ((request.ActiveOnly && !active) || (request.InactiveOnly && active)
             || (request.PublicOnly && !channel.AnnounceChannel) || (request.PrivateOnly && channel.AnnounceChannel)
             || (peer is { } only && channel.RemoteNodeId != only))
                continue;

            var item = ToLndChannel(channel, active);
            if (graph is not null && graph.TryGetNode(channel.RemoteNodeId, out var node))
                item.PeerAlias = node.AliasText;
            response.Channels.Add(item);
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// <c>PendingChannels</c>: channels before Open with a funding transaction (pending open), mutual closes and failed
    /// channels whose close transaction is not confirmed (waiting close), and channels resolving their commitment on
    /// chain (pending force close). The limbo balances are our balance at the close; maturity heights and per-HTLC
    /// details are not reported.
    /// </summary>
    public override async Task<PendingChannelsResponse> PendingChannels(PendingChannelsRequest request,
                                                                        ServerCallContext context)
    {
        var response = new PendingChannelsResponse();
        var channels = _channels.FindChannels(_ => true);
        IReadOnlyDictionary<ChannelId, ChannelCloseModel> closes = new Dictionary<ChannelId, ChannelCloseModel>();
        if (channels.Any(c => c.State == ChannelState.OnchainResolving))
            closes = await LoadClosesAsync();

        long limbo = 0;
        foreach (var channel in channels)
        {
            var balances = LndBalances.Of(channel);
            switch (channel.State)
            {
                case > ChannelState.None and < ChannelState.Open when IsPendingOpen(channel):
                    response.PendingOpenChannels.Add(new PendingChannelsResponse.Types.PendingOpenChannel
                    {
                        Channel = ToPendingChannel(channel, balances),
                        CommitFee = (long)balances.CommitFeeSat,
                        CommitWeight = (long)balances.CommitWeight,
                        FeePerKw = balances.FeeratePerKw
                    });
                    break;
                case ChannelState.Closing or ChannelState.Failed:
                    limbo += (long)(balances.LocalMsat / 1000);
                    response.WaitingCloseChannels.Add(new PendingChannelsResponse.Types.WaitingCloseChannel
                    {
                        Channel = ToPendingChannel(channel, balances),
                        LimboBalance = (long)(balances.LocalMsat / 1000),
                        ClosingTxid = channel.ClosingTransaction?.TxId.ToString() ?? string.Empty
                    });
                    break;
                case ChannelState.OnchainResolving:
                    limbo += (long)(balances.LocalMsat / 1000);
                    response.PendingForceClosingChannels.Add(new PendingChannelsResponse.Types.ForceClosedChannel
                    {
                        Channel = ToPendingChannel(channel, balances),
                        LimboBalance = (long)(balances.LocalMsat / 1000),
                        ClosingTxid = closes.TryGetValue(channel.ChannelId, out var close)
                                          ? close.CommitmentTransactionId.ToString()
                                          : string.Empty
                    });
                    break;
            }
        }

        response.TotalLimboBalance = limbo;
        return response;
    }

    /// <summary>
    /// <c>ClosedChannels</c>: the Closed channels (and Stale ones: a funding that never confirmed, FUNDING_CANCELED)
    /// with how they closed, from the stored channels and their <c>ChannelCloses</c> row. Settled and time-locked
    /// balances and per-output resolutions are not reported yet (NL-1166).
    /// </summary>
    public override async Task<ClosedChannelsResponse> ClosedChannels(ClosedChannelsRequest request,
                                                                      ServerCallContext context)
    {
        var filtered = request.Cooperative || request.LocalForce || request.RemoteForce || request.Breach
                    || request.FundingCanceled || request.Abandoned;
        await using var scope = CreateScope();
        var unitOfWork = UnitOfWork(scope);
        var stored = await unitOfWork.ChannelDbRepository.GetAllAsync();
        var closes = (await unitOfWork.OnchainResolutionDbRepository.GetClosesAsync())
                    .ToDictionary(c => c.ChannelId);
        // LND prints the genesis hash in display order (chainhash.Hash.String()); ChainHash has no hex ToString
        // (NL-1244)
        var chainHash = DisplayHex(_nodeOptions.BitcoinNetwork.ChainHash.Value);
        var response = new ClosedChannelsResponse();
        foreach (var channel in stored.Where(c => c.State is ChannelState.Closed or ChannelState.Stale))
        {
            closes.TryGetValue(channel.ChannelId, out var close);
            var type = channel.State == ChannelState.Stale
                           ? ChannelCloseSummary.Types.ClosureType.FundingCanceled
                           : close?.Kind switch
                           {
                               ChannelCloseKind.LocalCommitment => ChannelCloseSummary.Types.ClosureType.LocalForceClose,
                               ChannelCloseKind.RemoteCommitment or ChannelCloseKind.RemoteNextCommitment =>
                                   ChannelCloseSummary.Types.ClosureType.RemoteForceClose,
                               ChannelCloseKind.RevokedCommitment => ChannelCloseSummary.Types.ClosureType.BreachClose,
                               _ => ChannelCloseSummary.Types.ClosureType.CooperativeClose
                           };
            if (filtered && !(type switch
            {
                ChannelCloseSummary.Types.ClosureType.CooperativeClose => request.Cooperative,
                ChannelCloseSummary.Types.ClosureType.LocalForceClose => request.LocalForce,
                ChannelCloseSummary.Types.ClosureType.RemoteForceClose => request.RemoteForce,
                ChannelCloseSummary.Types.ClosureType.BreachClose => request.Breach,
                ChannelCloseSummary.Types.ClosureType.FundingCanceled => request.FundingCanceled,
                _ => request.Abandoned
            }))
                continue;

            var summary = new ChannelCloseSummary
            {
                ChannelPoint = ChannelPoint(channel),
                ChanId = ToChanId(channel.ShortChannelId),
                ChainHash = chainHash,
                ClosingTxHash = close?.CommitmentTransactionId.ToString()
                             ?? channel.ClosingTransaction?.TxId.ToString() ?? string.Empty,
                RemotePubkey = channel.RemoteNodeId.ToString(),
                Capacity = channel.FundingOutput?.Amount.Satoshi ?? 0,
                CloseHeight = close?.SpentAtHeight ?? 0,
                CloseType = type,
                OpenInitiator = channel.IsInitiator ? Initiator.Local : Initiator.Remote,
                CloseInitiator = type switch
                {
                    ChannelCloseSummary.Types.ClosureType.LocalForceClose => Initiator.Local,
                    ChannelCloseSummary.Types.ClosureType.RemoteForceClose
                     or ChannelCloseSummary.Types.ClosureType.BreachClose => Initiator.Remote,
                    ChannelCloseSummary.Types.ClosureType.CooperativeClose => channel.LocalIsCloser switch
                    {
                        true => Initiator.Local,
                        false => Initiator.Remote,
                        null => Initiator.Unknown
                    },
                    _ => Initiator.Unknown
                }
            };
            if (channel.LocalAliases is { } aliases)
                summary.AliasScids.Add(aliases.Select(ToChanId));
            if (type == ChannelCloseSummary.Types.ClosureType.CooperativeClose)
                summary.SettledBalance = CooperativeCloseBalance(channel);
            else if (close is not null)
                await AddResolutionsAsync(unitOfWork, channel, summary);
            response.Channels.Add(summary);
        }

        return response;
    }

    /// <summary>
    /// The resolutions of a force-closed channel's outputs (NL-1166): ours only (the peer's outputs and anchors are not
    /// listed), typed and judged as LND does — <c>CLAIMED</c> when our transaction spent it, <c>UNCLAIMED</c> when
    /// another did, <c>ABANDONED</c> when ignored, still <c>OUTCOME_UNKNOWN</c> while pending. The claimed amounts of
    /// our commitment outputs and HTLCs are the settled balance, the pending ones the time-locked balance.
    /// </summary>
    private static async Task AddResolutionsAsync(Domain.Persistence.Interfaces.IUnitOfWork unitOfWork,
                                                  ChannelModel channel, ChannelCloseSummary summary)
    {
        foreach (var output in await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(
                                   channel.ChannelId))
        {
            var type = output.Descriptor switch
            {
                OutputDescriptorKind.DelayedToLocal or OutputDescriptorKind.PaymentToRemote
                 or OutputDescriptorKind.RevokedToLocal => ResolutionType.Commit,
                OutputDescriptorKind.OurAnchor => ResolutionType.Anchor,
                OutputDescriptorKind.LocalOfferedHtlc or OutputDescriptorKind.RemoteReceivedHtlc =>
                    ResolutionType.OutgoingHtlc,
                OutputDescriptorKind.LocalReceivedHtlc or OutputDescriptorKind.RemoteOfferedHtlc =>
                    ResolutionType.IncomingHtlc,
                OutputDescriptorKind.RevokedHtlc or OutputDescriptorKind.RevokedSecondLevel =>
                    output.HtlcDirection == HtlcDirection.Incoming
                        ? ResolutionType.IncomingHtlc
                        : ResolutionType.OutgoingHtlc,
                _ => ResolutionType.TypeUnknown
            };
            if (type == ResolutionType.TypeUnknown)
                continue;

            var outcome = output.State switch
            {
                OutputResolutionState.Resolved or OutputResolutionState.Irrevocable =>
                    output.ResolvingTransactionId is null
                        ? ResolutionOutcome.Unclaimed
                        : ResolutionOutcome.Claimed,
                OutputResolutionState.Ignored => ResolutionOutcome.Abandoned,
                _ => ResolutionOutcome.OutcomeUnknown
            };
            var amountSat = OutputDescriptorData.TryDecode(output)?.AmountSat ?? 0;
            summary.Resolutions.Add(new Resolution
            {
                ResolutionType = type,
                Outcome = outcome,
                Outpoint = new OutPoint
                {
                    TxidBytes = ByteString.CopyFrom((byte[])output.TransactionId),
                    TxidStr = output.TransactionId.ToString(),
                    OutputIndex = output.OutputIndex
                },
                AmountSat = amountSat,
                SweepTxid = output.ResolvingTransactionId?.ToString() ?? string.Empty
            });
            if (type == ResolutionType.Anchor)
                continue;

            if (outcome == ResolutionOutcome.Claimed)
                summary.SettledBalance += (long)amountSat;
            else if (outcome == ResolutionOutcome.OutcomeUnknown)
                summary.TimeLockedBalance += (long)amountSat;
        }
    }

    /// <summary>Our output of the mutual close transaction (to our shutdown script), 0 when not found.</summary>
    private static long CooperativeCloseBalance(ChannelModel channel)
    {
        if (channel.ClosingTransaction is not { } closing || channel.LocalShutdownScript is not { } script)
            return 0;

        try
        {
            var transaction = NBitcoin.Transaction.Load(closing.RawTxBytes, NBitcoin.Network.RegTest);
            var ours = transaction.Outputs.FirstOrDefault(o => o.ScriptPubKey.ToBytes()
                                                                              .AsSpan()
                                                                              .SequenceEqual((byte[])script));
            return ours?.Value.Satoshi ?? 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private async Task<IReadOnlyDictionary<ChannelId, ChannelCloseModel>> LoadClosesAsync()
    {
        await using var scope = CreateScope();
        return (await UnitOfWork(scope).OnchainResolutionDbRepository.GetClosesAsync()).ToDictionary(c => c.ChannelId);
    }

    /// <summary>The channels <c>ListChannels</c> lists.</summary>
    private static bool IsListed(ChannelModel channel) =>
        channel.State is ChannelState.Open or ChannelState.ShuttingDown or ChannelState.Negotiating;

    private Channel ToLndChannel(ChannelModel channel, bool active)
    {
        var balances = LndBalances.Of(channel);
        var item = new Channel
        {
            Active = active,
            RemotePubkey = channel.RemoteNodeId.ToString(),
            ChannelPoint = ChannelPoint(channel),
            ChanId = ToChanId(channel.ShortChannelId),
            Capacity = channel.FundingOutput?.Amount.Satoshi ?? 0,
            LocalBalance = (long)(balances.LocalMsat / 1000),
            RemoteBalance = (long)(balances.RemoteMsat / 1000),
            CommitFee = (long)balances.CommitFeeSat,
            CommitWeight = (long)balances.CommitWeight,
            FeePerKw = balances.FeeratePerKw,
            UnsettledBalance = (long)((balances.OutgoingHtlcMsat + balances.IncomingHtlcMsat) / 1000),
            NumUpdates = channel.LocalCommitmentNumber,
            Private = !channel.AnnounceChannel,
            Initiator = channel.IsInitiator,
            ChanStatusFlags = channel.DataLossDetected ? "ChanStatusLocalDataLoss" : "ChanStatusDefault",
            CsvDelay = channel.ChannelParams.Remote.ToSelfDelay,
            LocalChanReserveSat = channel.ChannelParams.Remote.ChannelReserveAmount.Satoshi,
            RemoteChanReserveSat = channel.ChannelParams.Local.ChannelReserveAmount.Satoshi,
            StaticRemoteKey = true,
            CommitmentType = ToCommitmentType(channel.ChannelParams.CommitmentFormat),
            LocalConstraints = Constraints(channel.ChannelParams.Remote, channel.ChannelParams.Local),
            RemoteConstraints = Constraints(channel.ChannelParams.Local, channel.ChannelParams.Remote),
            PeerScidAlias = channel.RemoteAlias is { } remoteAlias ? ToChanId(remoteAlias) : 0
        };
        item.PendingHtlcs.Add(balances.Htlcs.Select(h => new HTLC
        {
            Incoming = h.Direction == HtlcDirection.Incoming,
            Amount = (long)(h.AmountMsat / 1000),
            HashLock = Google.Protobuf.ByteString.CopyFrom((byte[])h.PaymentHash),
            ExpirationHeight = h.CltvExpiry,
            HtlcIndex = h.Id,
            LockedIn = true
        }));
        if (channel.LocalAliases is { } aliases)
            item.AliasScids.Add(aliases.Select(ToChanId));
        return item;
    }

    /// <summary>
    /// LND's <c>ChannelConstraints</c> of one owner: <paramref name="otherParty"/> announced what binds the owner
    /// (to_self_delay on its outputs, the reserve it keeps, the HTLCs it may offer), <paramref name="owner"/> its own
    /// dust limit.
    /// </summary>
    private static ChannelConstraints Constraints(ChannelParty otherParty, ChannelParty owner) => new()
    {
        CsvDelay = otherParty.ToSelfDelay,
        ChanReserveSat = (ulong)otherParty.ChannelReserveAmount.Satoshi,
        DustLimitSat = (ulong)owner.DustLimitAmount.Satoshi,
        MaxPendingAmtMsat = otherParty.MaxHtlcValueInFlight.MilliSatoshi,
        MinHtlcMsat = otherParty.HtlcMinimumAmount.MilliSatoshi,
        MaxAcceptedHtlcs = otherParty.MaxAcceptedHtlcs
    };

    private PendingChannelsResponse.Types.PendingChannel ToPendingChannel(ChannelModel channel, LndBalances balances) =>
        new()
        {
            RemoteNodePub = channel.RemoteNodeId.ToString(),
            ChannelPoint = ChannelPoint(channel),
            Capacity = channel.FundingOutput?.Amount.Satoshi ?? 0,
            LocalBalance = (long)(balances.LocalMsat / 1000),
            RemoteBalance = (long)(balances.RemoteMsat / 1000),
            LocalChanReserveSat = channel.ChannelParams.Remote.ChannelReserveAmount.Satoshi,
            RemoteChanReserveSat = channel.ChannelParams.Local.ChannelReserveAmount.Satoshi,
            Initiator = channel.IsInitiator ? Initiator.Local : Initiator.Remote,
            CommitmentType = ToCommitmentType(channel.ChannelParams.CommitmentFormat),
            ChanStatusFlags = channel.DataLossDetected ? "ChanStatusLocalDataLoss" : "ChanStatusDefault",
            Private = !channel.AnnounceChannel
        };

    /// <summary>LND's commitment type: our taproot channels use the final bits 80/81, LND's <c>TAPROOT</c>.</summary>
    internal static CommitmentType ToCommitmentType(CommitmentFormat format) => format switch
    {
        CommitmentFormat.Anchors => CommitmentType.Anchors,
        CommitmentFormat.SimpleTaproot => CommitmentType.Taproot,
        _ => CommitmentType.StaticRemoteKey
    };

    /// <summary><c>txid:index</c> of the current funding, or empty without one.</summary>
    internal static string ChannelPoint(ChannelModel channel) =>
        channel.FundingOutput is { TransactionId: { } txId, Index: { } index } ? $"{txId}:{index}" : string.Empty;

    private static Amount Amount(ulong msat) => new() { Sat = msat / 1000, Msat = msat };

    /// <summary>
    /// A channel's balances as LND reports them, from our latest local commitment (<see cref="ChannelCommitments"/>):
    /// the main outputs without the HTLCs, the funder's side without the commitment fee and the anchors.
    /// </summary>
    internal sealed record LndBalances(ulong LocalMsat, ulong RemoteMsat, ulong OutgoingHtlcMsat,
                                       ulong IncomingHtlcMsat, ulong CommitFeeSat, ulong CommitWeight,
                                       long FeeratePerKw, IReadOnlyList<SpecHtlc> Htlcs)
    {
        public static LndBalances Of(ChannelModel channel)
        {
            if (channel.Commitments is not { } commitments)
                return new LndBalances(channel.LocalBalance.MilliSatoshi, channel.RemoteBalance.MilliSatoshi, 0, 0, 0,
                                       0, (long)channel.ChannelParams.FeeRateAmountPerKw.Satoshi, []);

            return FromSpec(commitments.LocalCommit.Spec, channel.ChannelParams.CommitmentFormat,
                            (ulong)channel.ChannelParams.Local.DustLimitAmount.Satoshi, channel.IsInitiator);
        }

        /// <summary>The balances of a local commitment <paramref name="spec"/> (holder: us).</summary>
        internal static LndBalances FromSpec(CommitmentSpec spec, CommitmentFormat format, ulong dustLimitSat,
                                             bool isFunder)
        {
            var untrimmed = spec.Htlcs.Count(h => !CommitmentFeeCalculator.IsHtlcTrimmed(
                                                       h.AmountMsat, h.IsOfferedBy(CommitmentSide.Local), dustLimitSat,
                                                       spec.FeeratePerKw, format));
            var fee = CommitmentFeeCalculator.CommitmentBaseFeeSatoshis(spec.FeeratePerKw, format, untrimmed);
            var funderCostMsat = CommitmentFeeCalculator.FunderCostSatoshis(spec.FeeratePerKw, format, untrimmed) * 1000;
            var local = spec.LocalMsat;
            var remote = spec.RemoteMsat;
            if (isFunder)
                local = local > funderCostMsat ? local - funderCostMsat : 0;
            else
                remote = remote > funderCostMsat ? remote - funderCostMsat : 0;

            var outgoing = spec.Htlcs.Where(h => h.Direction == HtlcDirection.Outgoing)
                               .Aggregate(0UL, (sum, h) => sum + h.AmountMsat);
            var incoming = spec.Htlcs.Where(h => h.Direction == HtlcDirection.Incoming)
                               .Aggregate(0UL, (sum, h) => sum + h.AmountMsat);
            return new LndBalances(local, remote, outgoing, incoming, fee,
                                   CommitmentFeeCalculator.CommitmentWeight(format, untrimmed), spec.FeeratePerKw,
                                   spec.Htlcs);
        }
    }
}