using System.Collections.Concurrent;
using System.Threading.Channels;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.LndGrpc.Services;

using Domain.Channels.Acceptance;
using Domain.Money;
using Domain.Node;
using Lnrpc;
using Feature = Domain.Enums.Feature;

/// <summary>
/// One <c>ChannelAcceptor</c> stream as an <see cref="IChannelOpenDecider"/> (LND's <c>chanacceptor.RPCAcceptor</c>,
/// NL-1180): each open is sent to the client as a <see cref="ChannelAcceptRequest"/> and waits for the
/// <see cref="ChannelAcceptResponse"/> with its <c>pending_chan_id</c>. No answer within the timeout, a full queue, a
/// closed stream or an invalid answer rejects the open with LND's generic text; a rejection with an error sends that
/// error to the opener.
/// </summary>
internal sealed class RpcChannelAcceptor : IChannelOpenDecider
{
    private readonly ILogger _logger;
    private readonly int _maxPending;
    private readonly Network _network;
    private readonly Channel<ChannelAcceptRequest> _outbound =
        System.Threading.Channels.Channel.CreateUnbounded<ChannelAcceptRequest>();
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _done = new();

    public RpcChannelAcceptor(Network network, TimeSpan timeout, int maxPending, TimeProvider timeProvider,
                              ILogger logger)
    {
        _network = network;
        _timeout = timeout;
        _maxPending = maxPending;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The opens waiting for this client's answer (tests).</summary>
    internal int PendingCount => _pending.Count;

    /// <inheritdoc />
    public async Task<ChannelOpenDecision> DecideAsync(ChannelOpenRequest request, CancellationToken cancellationToken)
    {
        if (_done.IsCancellationRequested || _pending.Count >= _maxPending)
            return ChannelOpenDecision.Rejected();

        var key = Convert.ToHexString((byte[])request.PendingChannelId);
        var pending = new Pending(request, new TaskCompletionSource<ChannelOpenDecision>(
                                               TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_pending.TryAdd(key, pending))
            return ChannelOpenDecision.Rejected();

        try
        {
            if (!_outbound.Writer.TryWrite(ToRpc(request)))
                return ChannelOpenDecision.Rejected();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _done.Token);
            return await pending.Decision.Task.WaitAsync(_timeout, _timeProvider, linked.Token);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The channel acceptor did not answer the open {PendingChannelId} of {Peer} within "
                             + "{Timeout}: rejecting it", request.PendingChannelId, request.NodeId, _timeout);
            return ChannelOpenDecision.Rejected();
        }
        catch (OperationCanceledException) when (_done.IsCancellationRequested
                                              && !cancellationToken.IsCancellationRequested)
        {
            return ChannelOpenDecision.Rejected();
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Runs the stream until the client or the server ends it: requests go out in order, answers resolve their
    /// opens. Every open still waiting is rejected when it ends.
    /// </summary>
    public async Task RunAsync(IAsyncStreamReader<ChannelAcceptResponse> requests,
                               IServerStreamWriter<ChannelAcceptRequest> responses,
                               CancellationToken cancellationToken)
    {
        var writer = Task.Run(async () =>
        {
            await foreach (var request in _outbound.Reader.ReadAllAsync(cancellationToken))
                await responses.WriteAsync(request, cancellationToken);
        }, CancellationToken.None);
        try
        {
            while (await requests.MoveNext(cancellationToken))
                Resolve(requests.Current);
        }
        finally
        {
            _done.Cancel();
            _outbound.Writer.TryComplete();
            foreach (var pending in _pending.Values)
                pending.Decision.TrySetResult(ChannelOpenDecision.Rejected());
            try
            {
                await writer;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException
                                          or RpcException)
            {
                // The stream is gone: nothing more to send
            }

            _done.Dispose();
        }
    }

    private void Resolve(ChannelAcceptResponse response)
    {
        // LND copies the id into a [32]byte: a shorter one is zero-padded
        var id = new byte[32];
        response.PendingChanId.Span[..Math.Min(32, response.PendingChanId.Length)].CopyTo(id);
        if (!_pending.TryGetValue(Convert.ToHexString(id), out var pending))
            return;

        var decision = Validate(pending.Request, response, _network, out var invalid);
        if (invalid is not null)
            _logger.LogWarning("Invalid channel acceptor response for {PendingChannelId}: {Reason}",
                               pending.Request.PendingChannelId, invalid);
        pending.Decision.TrySetResult(decision);
    }

    /// <summary>
    /// LND's <c>validateAcceptorResponse</c>: an invalid answer rejects with the generic text (<paramref name="invalid"/>
    /// says why); an acceptance carries its non-zero values.
    /// </summary>
    internal static ChannelOpenDecision Validate(ChannelOpenRequest request, ChannelAcceptResponse response,
                                                 Network network, out string? invalid)
    {
        invalid = null;
        if (response.MaxHtlcCount > ChannelOpenDecisionRules.MaxAcceptedHtlcsLimit)
            invalid = $"htlc limit exceeds spec limit of: {ChannelOpenDecisionRules.MaxAcceptedHtlcsLimit}";
        else if (response.ReserveSat != 0 && LightningMoney.Satoshis(response.ReserveSat) < request.DustLimit)
            invalid = "reserve lower than proposed dust limit";
        else if (response.CsvDelay > ushort.MaxValue)
            invalid = "csv_delay above 65535";
        else if (response.MinAcceptDepth > ushort.MaxValue)
            invalid = "min_accept_depth above 65535";
        else if (response.Error.Length > ChannelOpenDecisionRules.MaxErrorLength)
            invalid = $"custom error message exceeds length limit: {ChannelOpenDecisionRules.MaxErrorLength}";
        else if (response.Accept && response.Error.Length > 0)
            invalid = "channel acceptor response accepts channel, but also includes custom error";

        Domain.Bitcoin.ValueObjects.BitcoinScript? upfront = null;
        if (invalid is null && response.UpfrontShutdown.Length > 0)
        {
            try
            {
                upfront = BitcoinAddress.Create(response.UpfrontShutdown, network).ScriptPubKey.ToBytes();
            }
            catch (FormatException)
            {
                invalid = "could not parse upfront shutdown address";
            }
        }

        if (invalid is not null)
            return ChannelOpenDecision.Rejected();
        if (!response.Accept)
            return ChannelOpenDecision.Rejected(response.Error);

        return new ChannelOpenDecision
        {
            Accept = true,
            UpfrontShutdownScript = upfront,
            ToSelfDelay = response.CsvDelay == 0 ? null : (ushort)response.CsvDelay,
            ChannelReserve = response.ReserveSat == 0 ? null : LightningMoney.Satoshis(response.ReserveSat),
            MaxHtlcValueInFlight = response.InFlightMaxMsat == 0
                                       ? null
                                       : LightningMoney.MilliSatoshis(response.InFlightMaxMsat),
            MaxAcceptedHtlcs = response.MaxHtlcCount == 0 ? null : (ushort)response.MaxHtlcCount,
            HtlcMinimum = response.MinHtlcIn == 0 ? null : LightningMoney.MilliSatoshis(response.MinHtlcIn),
            MinimumDepth = response.MinAcceptDepth == 0 ? null : response.MinAcceptDepth,
            ZeroConf = response.ZeroConf
        };
    }

    /// <summary>The open as LND sends it to its acceptors.</summary>
    internal static ChannelAcceptRequest ToRpc(ChannelOpenRequest request)
    {
        var (commitmentType, zeroConf, scidAlias) = DescribeChannelType(request.ChannelType);
        return new ChannelAcceptRequest
        {
            NodePubkey = ByteString.CopyFrom((byte[])request.NodeId),
            ChainHash = ByteString.CopyFrom((byte[])request.ChainHash),
            PendingChanId = ByteString.CopyFrom((byte[])request.PendingChannelId),
            FundingAmt = (ulong)request.FundingAmount.Satoshi,
            PushAmt = (ulong)request.PushAmount.MilliSatoshi,
            DustLimit = (ulong)request.DustLimit.Satoshi,
            MaxValueInFlight = (ulong)request.MaxValueInFlight.MilliSatoshi,
            ChannelReserve = (ulong)request.ChannelReserve.Satoshi,
            MinHtlc = (ulong)request.HtlcMinimum.MilliSatoshi,
            FeePerKw = request.FeeRatePerKw,
            CsvDelay = request.ToSelfDelay,
            MaxAcceptedHtlcs = request.MaxAcceptedHtlcs,
            ChannelFlags = request.ChannelFlags,
            CommitmentType = commitmentType,
            WantsZeroConf = zeroConf,
            WantsScidAlias = scidAlias
        };
    }

    /// <summary>LND's reading of a <c>channel_type</c>: the commitment type and the zero-conf/scid-alias bits.</summary>
    internal static (CommitmentType Type, bool ZeroConf, bool ScidAlias) DescribeChannelType(FeatureSet? channelType)
    {
        if (channelType is null)
            return (CommitmentType.UnknownCommitmentType, false, false);

        var zeroConf = channelType.IsFeatureSet(Feature.OptionZeroconf, true);
        var scidAlias = channelType.IsFeatureSet(Feature.OptionScidAlias, true);
        CommitmentType type;
        if (channelType.IsFeatureSet(Feature.OptionSimpleTaproot, true))
            type = CommitmentType.SimpleTaprootFinal;
        else if (channelType.IsFeatureSet(Feature.OptionAnchors, true))
            type = CommitmentType.Anchors;
        else if (channelType.IsFeatureSet(Feature.OptionStaticRemoteKey, true))
            type = CommitmentType.StaticRemoteKey;
        else
            type = CommitmentType.Legacy;
        return (type, zeroConf, scidAlias);
    }

    private sealed record Pending(ChannelOpenRequest Request, TaskCompletionSource<ChannelOpenDecision> Decision);
}