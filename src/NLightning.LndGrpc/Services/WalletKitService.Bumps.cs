using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.LndGrpc.Services;

using Application.Onchain.Anchors;
using Application.Onchain.Fees;
using Application.Onchain.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Onchain.Enums;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Walletrpc;

/// <summary>
/// LND's sweeper controls over this node's BOLT 5 resolution (NL-1186): <c>BumpForceCloseFee</c> and <c>BumpFee</c>.
/// </summary>
/// <remarks>
/// The parameters (starting fee rate, budget, deadline, confirmation target, immediate) are kept in memory as LND keeps
/// its sweeper's (<see cref="OperatorFeeBumps"/>): for a force close they steer the CPFP child of the unconfirmed
/// commitment through our anchor (<see cref="IAnchorCpfpService"/>), for one of the channel's outputs its sweep, claim or
/// penalty (<see cref="SweepScheduler"/>), which a fresh request replaces at once instead of after the RBF interval.
/// Not supported: a CPFP of an unconfirmed wallet output (the wallet never spends unconfirmed outputs), and the
/// wallet-funded HTLC transactions of anchors channels, which their resolver bumps on its own schedule.
/// </remarks>
public sealed partial class WalletKitService
{
    /// <summary>
    /// <c>BumpForceCloseFee</c>: the fee bump of a force-closed anchors channel's unconfirmed commitment (ours, or the
    /// peer's in the mempool) through the CPFP child spending our anchor.
    /// </summary>
    public override async Task<BumpForceCloseFeeResponse> BumpForceCloseFee(BumpForceCloseFeeRequest request,
                                                                           ServerCallContext context)
    {
        if (request.ChanPoint is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "chan_point is required"));

        var txId = request.ChanPoint.FundingTxidCase == Lnrpc.ChannelPoint.FundingTxidOneofCase.FundingTxidBytes
                       ? new TxId(request.ChanPoint.FundingTxidBytes.ToByteArray())
                       : FromOutPoint(new Lnrpc.OutPoint { TxidStr = request.ChanPoint.FundingTxidStr }).TxId;
        var channels = _serviceProvider.GetRequiredService<IChannelMemoryRepository>();
        var channel = channels.FindChannels(c => c.FundingOutput is { TransactionId: { } id, Index: { } index }
                                              && id == txId && index == request.ChanPoint.OutputIndex)
                              .FirstOrDefault()
                   ?? throw new RpcException(new Status(StatusCode.NotFound,
                                                        "unable to find pending force close channel"));

        var bump = OperatorRequest(request.StartingFeerate, request.Budget, request.DeadlineDelta, request.TargetConf,
                                   request.Immediate);
        var outcome = await Anchors.RequestBumpAsync(channel.ChannelId, bump, null, context.CancellationToken);
        ThrowUnlessRegistered(outcome);
        return new BumpForceCloseFeeResponse { Status = "Successfully registered CPFP-transaction with the sweeper" };
    }

    /// <summary>
    /// <c>BumpFee</c>: our anchor of an unconfirmed commitment (as <c>BumpForceCloseFee</c>), or an output of a
    /// channel's on-chain resolution whose sweep, claim or penalty this node publishes (applied to its pending
    /// transaction, or to the first one once its delay passes). <c>sat_per_byte</c> and <c>force</c> are read as their
    /// replacements <c>sat_per_vbyte</c> and <c>immediate</c>.
    /// </summary>
    public override async Task<BumpFeeResponse> BumpFee(BumpFeeRequest request, ServerCallContext context)
    {
        var (txId, index) = FromOutPoint(request.Outpoint);
        var bump = OperatorRequest(request.SatPerVbyte != 0 ? request.SatPerVbyte : request.SatPerByte,
                                   request.Budget, request.DeadlineDelta, request.TargetConf,
                                   request.Immediate || request.Force);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // Our anchor of an unconfirmed commitment (ours, or the peer's we follow): the CPFP child's parameters
        if (await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId) is
            {
                Purpose: BroadcastPurpose.LocalCommitment or BroadcastPurpose.PeerCommitment,
                State: BroadcastState.Pending, ChannelId: { } commitmentChannelId
            })
        {
            var outcome = await Anchors.RequestBumpAsync(commitmentChannelId, bump, (txId, index),
                                                         context.CancellationToken);
            ThrowUnlessRegistered(outcome);
            return new BumpFeeResponse { Status = "Successfully registered CPFP-tx with the sweeper" };
        }

        // An output of a resolution: its sweep, claim or penalty
        if (await unitOfWork.OnchainResolutionDbRepository.GetOutputAsync(txId, index) is { } output)
        {
            if (output.State is OutputResolutionState.Resolved or OutputResolutionState.Irrevocable
                                                          or OutputResolutionState.Ignored)
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                                  $"the output {txId}:{index} is already resolved"));
            if (!SweepScheduler.CanBump(output.Descriptor))
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                                  $"the {output.Descriptor} output {txId}:{index} is fee-bumped by "
                                                + "the node itself; BumpFee applies to sweeps, claims and penalties"));

            var bumps = _serviceProvider.GetService<OperatorFeeBumps>()
                     ?? throw new RpcException(new Status(StatusCode.Unavailable,
                                                          "this node has no on-chain fee bumping"));
            bumps.SetOutput(txId, index, bump);
            if (bump.Immediate && _serviceProvider.GetService<IOnchainResolutionExecutor>() is { } executor)
                await executor.ResolveChannelAsync(output.ChannelId, Tip, context.CancellationToken);
            return new BumpFeeResponse { Status = "Successfully registered rbf-tx with sweeper" };
        }

        if (_serviceProvider.GetService<IUtxoMemoryRepository>()?.TryGetUtxo(txId, index, out _) == true)
            throw new RpcException(new Status(StatusCode.Unimplemented,
                                              "a CPFP of a wallet output is not supported: the wallet never spends "
                                            + "unconfirmed outputs"));

        throw new RpcException(new Status(StatusCode.NotFound,
                                          $"the outpoint {txId}:{index} is neither swept by this node nor ours"));
    }

    private IAnchorCpfpService Anchors =>
        _serviceProvider.GetService<IAnchorCpfpService>()
     ?? throw new RpcException(new Status(StatusCode.Unavailable, "this node has no anchor fee bumping"));

    private uint Tip => _serviceProvider.GetService<IBlockchainMonitor>()?.LastProcessedBlockHeight ?? 0;

    /// <summary>LND's sweeper parameters as an operator request (sat/vB to sat/kw; 0 = unset).</summary>
    private OperatorFeeBumpRequest OperatorRequest(ulong startingSatPerVbyte, ulong budgetSat, uint deadlineDelta,
                                                   uint targetConf, bool immediate)
    {
        uint? starting = startingSatPerVbyte == 0
                             ? null
                             : (uint)Math.Min(uint.MaxValue, Math.Max(253UL, startingSatPerVbyte * 250));
        uint? deadline = deadlineDelta == 0 ? null : Tip + deadlineDelta;
        return new OperatorFeeBumpRequest(starting, budgetSat == 0 ? null : budgetSat, deadline,
                                          targetConf == 0 ? null : targetConf, immediate);
    }

    private static void ThrowUnlessRegistered(AnchorBumpOutcome outcome)
    {
        switch (outcome)
        {
            case AnchorBumpOutcome.Registered:
                return;
            case AnchorBumpOutcome.UnknownChannel:
                throw new RpcException(new Status(StatusCode.NotFound,
                                                  "unable to find pending force close channel"));
            case AnchorBumpOutcome.NoAnchors:
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                                  "the channel has no anchors: its force close cannot be fee "
                                                + "bumped"));
            case AnchorBumpOutcome.NotForceClosed:
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                                  "the channel has no unconfirmed force close transaction"));
            case AnchorBumpOutcome.NotOurAnchor:
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                                  "the outpoint is not our anchor of the unconfirmed commitment"));
            default:
                throw new RpcException(new Status(StatusCode.Unavailable,
                                                  "the anchor fee bumping of this node is off"));
        }
    }
}