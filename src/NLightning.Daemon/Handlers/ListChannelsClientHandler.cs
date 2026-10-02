using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Handlers;

using Application.Channels.RoutingPolicies;
using Domain.Accounting.Labels;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Reestablish;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// Lists the node's channels: every channel loaded in memory (the live state), then every persisted channel that is
/// not loaded (closed or stale ones).
/// </summary>
/// <remarks>
/// Pending HTLC counts come from the commitment snapshot (<see cref="ChannelModel.Commitments"/>): every HTLC whose
/// state is not final, per direction (NL-241). The legacy <c>LocalOfferedHtlcs</c>/<c>RemoteOfferedHtlcs</c>
/// collections are only used for a channel without a snapshot. The routing policy is the one the channel announces
/// (<see cref="ChannelPolicyRules.Resolve"/>): its <c>setchannelpolicy</c> override where set (wave sp1 lane SP1-G,
/// through the optional <see cref="IChannelPolicyProvider"/>), the node's <see cref="RoutingOptions"/> elsewhere.
/// <para>Fundings (splicing plan §3.10, wave sp2 lane SP2-D) come from the <c>ChannelFundings</c> rows: the current
/// funding first, then the pending splices in creation order, then the replaced fundings whose short channel id still
/// resolves in the optional <see cref="IRetiredScidMap"/>; discarded fundings are not listed. A channel without rows
/// (funded before the splice schema, or a unit of work that stores none) lists its funding output as the current
/// funding. The rows of a Closed or Stale channel are not read (one query per closed channel would grow the command
/// with the node's history): its funding output is listed as its funding. Depths are counted from the chain monitor's last processed block (optional
/// <see cref="IBlockchainMonitor"/>); the current funding's short channel id falls back to the channel's. Retired short
/// channel ids come from the same map, oldest first.</para>
/// </remarks>
public class ListChannelsClientHandler : IClientCommandHandler<ListChannelsClientRequest, ListChannelsClientResponse>
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IPeerManager _peerManager;
    private readonly IReestablishTracker _reestablishTracker;
    private readonly IUnitOfWork _unitOfWork;
    private readonly RoutingOptions _routingOptions;
    private readonly IChannelPolicyProvider? _channelPolicyProvider;
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IRetiredScidMap? _retiredScidMap;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListChannels;

    public ListChannelsClientHandler(IChannelMemoryRepository channelMemoryRepository, IPeerManager peerManager,
                                     IReestablishTracker reestablishTracker, IUnitOfWork unitOfWork,
                                     IOptions<NodeOptions> nodeOptions,
                                     IChannelPolicyProvider? channelPolicyProvider = null,
                                     IBlockchainMonitor? blockchainMonitor = null,
                                     IRetiredScidMap? retiredScidMap = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _retiredScidMap = retiredScidMap;
        _channelPolicyProvider = channelPolicyProvider;
        _routingOptions = nodeOptions.Value.Routing;
        _channelMemoryRepository = channelMemoryRepository;
        _peerManager = peerManager;
        _reestablishTracker = reestablishTracker;
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc/>
    public async Task<ListChannelsClientResponse> HandleAsync(ListChannelsClientRequest request,
                                                              CancellationToken ct)
    {
        var channels = new List<ChannelModel>();
        var listed = new HashSet<ChannelId>();

        foreach (var channel in _channelMemoryRepository.FindChannels(c => IsRequested(c, request)))
        {
            if (listed.Add(channel.ChannelId))
                channels.Add(channel);
        }

        foreach (var channel in await _unitOfWork.ChannelDbRepository.GetAllAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (IsRequested(channel, request) && listed.Add(channel.ChannelId))
                channels.Add(channel);
        }

        var fundingRepository = GetFundingRepository();
        var tipHeight = _blockchainMonitor?.LastProcessedBlockHeight ?? 0;
        var infos = new List<ChannelInfoClientResponse>(channels.Count);
        foreach (var channel in channels)
        {
            ct.ThrowIfCancellationRequested();
            // A Closed or Stale channel's rows are not read: one query per closed channel grows listchannels with
            // the node's history, and its retired short channel ids are gone; its funding output is listed instead
            var fundings = fundingRepository is null || IsFinished(channel)
                               ? []
                               : await fundingRepository.GetByChannelIdAsync(channel.ChannelId);
            infos.Add(ToChannelInfo(channel, fundings, tipHeight, await GetOtherOpenAttemptsAsync(channel)));
        }

        return new ListChannelsClientResponse(infos);
    }

    /// <summary>
    /// NL-535: the other signed attempts of a dual-funded open still waiting for its funding (RBF, either side's), from
    /// their interactive-tx rows, listed after the channel's current funding as <see cref="ChannelFundingStatus.Pending"/>
    /// <see cref="ChannelFundingKind.Initial"/> fundings: any of them may be the one that confirms.
    /// </summary>
    private async Task<IReadOnlyList<ChannelFundingInfoClientResponse>> GetOtherOpenAttemptsAsync(ChannelModel channel)
    {
        if (channel is not { Version: ChannelVersion.V2, State: ChannelState.V1FundingSigned })
            return [];

        IReadOnlyList<InteractiveTxSessionModel> sessions;
        try
        {
            if (_unitOfWork.InteractiveTxSessionDbRepository is not { } repository)
                return [];

            sessions = await repository.GetByChannelIdAsync(channel.ChannelId);
        }
        catch (NotSupportedException)
        {
            // A unit of work without the interactive-tx schema
            return [];
        }

        var currentTxId = channel.FundingOutput?.TransactionId;
        var attempts = new List<ChannelFundingInfoClientResponse>();
        foreach (var session in sessions)
        {
            if (session is not
                {
                    Purpose: InteractiveTxPurpose.DualFund or InteractiveTxPurpose.DualFundRbf,
                    State: InteractiveTxSessionState.Signed or InteractiveTxSessionState.TxSignaturesSent,
                    ConstructedTx: { SharedOutputIndex: { } index } transaction
                }
             || transaction.TxId == currentTxId || index >= transaction.Outputs.Count
             || attempts.Any(a => a.FundingTxId == transaction.TxId))
                continue;

            attempts.Add(new ChannelFundingInfoClientResponse
            {
                FundingTxId = transaction.TxId,
                OutputIndex = checked((ushort)index),
                Capacity = transaction.Outputs[(int)index].Amount,
                Status = ChannelFundingStatus.Pending,
                Kind = ChannelFundingKind.Initial
            });
        }

        return attempts;
    }

    private IChannelFundingDbRepository? GetFundingRepository()
    {
        try
        {
            return _unitOfWork.ChannelFundingDbRepository;
        }
        catch (NotSupportedException)
        {
            // A unit of work without the splice schema stores no fundings: list the funding output alone
            return null;
        }
    }

    private static bool IsFinished(ChannelModel channel) =>
        channel.State is ChannelState.Closed or ChannelState.Stale;

    private static bool IsRequested(ChannelModel channel, ListChannelsClientRequest request) =>
        request.PeerId is null || channel.RemoteNodeId == request.PeerId.Value;

    private ChannelInfoClientResponse ToChannelInfo(ChannelModel channel, IReadOnlyList<ChannelFunding> fundings,
                                                    uint tipHeight,
                                                    IReadOnlyList<ChannelFundingInfoClientResponse> openAttempts)
    {
        var retired = _retiredScidMap?.GetByChannel(channel.ChannelId) ?? [];
        var policy = _channelPolicyProvider?.GetEffectivePolicy(channel)
                  ?? ChannelPolicyRules.Resolve(channel, _routingOptions, null);
        return new ChannelInfoClientResponse
        {
            ChannelId = channel.ChannelId,
            PeerId = channel.RemoteNodeId,
            State = channel.State,
            IsInitiator = channel.IsInitiator,
            IsPeerConnected = _peerManager.GetPeer(channel.RemoteNodeId) is not null,
            // A default ShortChannelId has no bytes; block 0 never holds a funding transaction
            ShortChannelId = channel.ShortChannelId.BlockHeight == 0
                                 ? (ShortChannelId?)null
                                 : channel.ShortChannelId,
            FundingTxId = channel.FundingOutput?.TransactionId,
            FundingOutputIndex = channel.FundingOutput?.Index,
            Capacity = channel.FundingOutput?.Amount ?? LightningMoney.Zero,
            LocalBalance = channel.LocalBalance,
            RemoteBalance = channel.RemoteBalance,
            LocalCommitmentNumber = channel.LocalCommitmentNumber,
            RemoteCommitmentNumber = channel.RemoteCommitmentNumber,
            OfferedHtlcCount = CountPendingHtlcs(channel, HtlcDirection.Outgoing),
            ReceivedHtlcCount = CountPendingHtlcs(channel, HtlcDirection.Incoming),
            DataLossDetected = channel.DataLossDetected,
            // True once channel_reestablish was exchanged on the peer's current connection (BOLT2 plan N7)
            IsReestablished = _reestablishTracker.IsReestablished(channel.ChannelId),
            FeeBaseMsat = policy.FeeBaseMsat,
            FeePpm = policy.FeeProportionalMillionths,
            CltvExpiryDelta = policy.CltvExpiryDelta,
            HtlcMinimumMsat = policy.HtlcMinimumMsat,
            HtlcMaximumMsat = policy.HtlcMaximumMsat,
            HasPolicyOverride = policy.Override is not null,
            Fundings = [.. ToFundingInfos(channel, fundings, retired, tipHeight), .. openAttempts],
            RetiredShortChannelIds = retired.Select(r => new RetiredScidInfoClientResponse
            {
                ShortChannelId = r.ShortChannelId,
                RetiredAtHeight = r.RetiredAtHeight,
                ExpiresAtHeight = r.ExpiresAtHeight
            }).ToList(),
            Label = channel.Label,
            Tags = SourceLabels.FromStored(null, channel.Tags).TagStrings
        };
    }

    /// <summary>
    /// The current funding, the pending ones in creation order, then the replaced ones whose short channel id is still
    /// retired (resolvable); discarded fundings and replaced ones past their retention are left out.
    /// </summary>
    private static List<ChannelFundingInfoClientResponse> ToFundingInfos(
        ChannelModel channel, IReadOnlyList<ChannelFunding> fundings, IReadOnlyList<RetiredShortChannelId> retired,
        uint tipHeight)
    {
        var channelScid = channel.ShortChannelId.BlockHeight == 0 ? (ShortChannelId?)null : channel.ShortChannelId;
        if (fundings.Count == 0)
        {
            // No rows: the funding output is the channel's only (current) funding
            if (channel.FundingOutput is not { } output || ChannelFunding.FromFundingOutput(output) is not { } initial)
                return [];

            return [ToFundingInfo(initial, channelScid, tipHeight)];
        }

        var retiredScids = retired.Select(r => r.ShortChannelId).ToHashSet();
        var listed = new List<ChannelFundingInfoClientResponse>(fundings.Count);
        listed.AddRange(fundings.Where(f => f.Status == ChannelFundingStatus.Current)
                                .Select(f => ToFundingInfo(f, channelScid, tipHeight)));
        listed.AddRange(fundings.Where(f => f.Status == ChannelFundingStatus.Pending)
                                .Select(f => ToFundingInfo(f, null, tipHeight)));
        listed.AddRange(fundings.Where(f => f.Status == ChannelFundingStatus.Replaced
                                         && f.ShortChannelId is { } scid && retiredScids.Contains(scid))
                                .Select(f => ToFundingInfo(f, null, tipHeight)));
        return listed;
    }

    private static ChannelFundingInfoClientResponse ToFundingInfo(ChannelFunding funding,
                                                                  ShortChannelId? fallbackScid, uint tipHeight)
    {
        var scid = funding.ShortChannelId ?? fallbackScid;
        // The confirmation height: stored for splices; an initial funding's is its short channel id's block
        var confirmedHeight = funding.ConfirmedHeight ?? scid?.BlockHeight;
        uint? depth = confirmedHeight is { } height && height > 0 && tipHeight >= height
                          ? tipHeight - height + 1
                          : null;
        return new ChannelFundingInfoClientResponse
        {
            FundingTxId = funding.FundingTxId,
            OutputIndex = funding.OutputIndex,
            Capacity = LightningMoney.Satoshis(funding.CapacitySatoshis),
            Status = funding.Status,
            Kind = funding.Kind,
            Depth = depth,
            ShortChannelId = scid,
            SpliceLockedSent = funding.SpliceLockedSent,
            SpliceLockedReceived = funding.SpliceLockedReceived
        };
    }

    /// <summary>
    /// HTLCs of <paramref name="direction"/> that are not fully resolved: offered (or received) and not yet removed
    /// from both commitments with the removal irrevocably committed.
    /// </summary>
    private static int CountPendingHtlcs(ChannelModel channel, HtlcDirection direction)
    {
        if (channel.Commitments is { } commitments)
            return commitments.Htlcs.Values.Count(h => h.Direction == direction && !HtlcStateTable.IsFinal(h.State));

        // No snapshot yet (legacy or still opening): only the in-memory legacy collections can hold HTLCs
        var legacy = direction == HtlcDirection.Outgoing ? channel.LocalOfferedHtlcs : channel.RemoteOfferedHtlcs;
        return legacy?.Count ?? 0;
    }
}