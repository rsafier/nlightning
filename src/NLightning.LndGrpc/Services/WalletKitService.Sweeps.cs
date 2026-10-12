using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.LndGrpc.Services;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Walletrpc;

public sealed partial class WalletKitService
{
    /// <summary>What LND's sweeper publishes, in our <c>BroadcastTransactions</c> purposes (NL-1245).</summary>
    private static readonly HashSet<BroadcastPurpose> s_sweepPurposes =
    [
        BroadcastPurpose.Sweep, BroadcastPurpose.HtlcTransaction, BroadcastPurpose.HtlcClaim,
        BroadcastPurpose.Penalty, BroadcastPurpose.AnchorCpfp, BroadcastPurpose.AnchorSweep
    ];

    /// <summary>
    /// <c>ListSweeps</c> (NL-1245): the transactions our BOLT 5 resolution broadcast to take channel outputs (sweeps of
    /// <c>to_local</c>/<c>to_remote</c> and second-level outputs, HTLC timeout/success transactions, HTLC claims on the
    /// peer's commitment, penalties, anchor CPFP children and anchor sweeps; LND's sweeper publishes the same kinds),
    /// from the <c>BroadcastTransactions</c> rows of every channel with a recorded close plus the pending rows; a
    /// replaced or abandoned attempt is left out, as LND lists only what it did not replace. <c>start_height</c> keeps
    /// the sweeps confirmed at or above it (and the unconfirmed ones); -1 only the unconfirmed ones. Not verbose: their
    /// txids (display order). Verbose: <c>GetTransactions</c>' entries of those txids, as LND reads its wallet's
    /// history for them, so a sweep that moved no wallet output (an HTLC transaction, a penalty paying elsewhere) is
    /// listed only in the txids.
    /// </summary>
    public override async Task<ListSweepsResponse> ListSweeps(ListSweepsRequest request, ServerCallContext context)
    {
        if (request.StartHeight < -1)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "start_height must be -1 or more"));

        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var sweeps = await ReadSweepsAsync(unitOfWork);
        var unconfirmedOnly = request.StartHeight == -1;
        var selected = sweeps.Where(r => r.ConfirmedHeight is { } height
                                             ? !unconfirmedOnly && height >= (uint)Math.Max(0, request.StartHeight)
                                             : true)
                             .OrderBy(r => r.ConfirmedHeight ?? uint.MaxValue).ThenBy(r => r.CreatedAt)
                             .ToList();
        if (!request.Verbose)
        {
            var ids = new ListSweepsResponse.Types.TransactionIDs();
            ids.TransactionIds.AddRange(selected.Select(r => r.TransactionId.ToString()));
            return new ListSweepsResponse { TransactionIds = ids };
        }

        var lightning = _serviceProvider.GetService<LightningService>()
                     ?? throw new RpcException(new Status(StatusCode.Unavailable,
                                                          "the wallet history is not available"));
        var details = await lightning.ListWalletTransactionsAsync(
                          new Lnrpc.GetTransactionsRequest { StartHeight = Math.Max(0, request.StartHeight) },
                          selected.Select(r => r.TransactionId).ToHashSet(), unconfirmedOnly,
                          context.CancellationToken);
        return new ListSweepsResponse { TransactionDetails = details };
    }

    /// <summary>
    /// <c>PendingSweeps</c> (NL-1245): every output of a closed channel our BOLT 5 resolution still has to take
    /// (<c>Pending</c>, <c>Waiting</c> for its CSV/CLTV, or <c>Broadcast</c> and not yet confirmed), with LND's witness
    /// type for its descriptor, its amount, <c>broadcast_attempts</c> 1 once its resolving transaction is out (0
    /// before), that transaction's feerate as <c>sat_per_vbyte</c>, the deadline as <c>deadline_height</c> and the
    /// height it waits for as <c>maturity_height</c>. Outputs that are the peer's or ignored are not sweeps. The
    /// taproot witness types are not distinguished (a taproot channel's outputs carry the plain types); <c>budget</c>,
    /// <c>immediate</c> and <c>requested_sat_per_vbyte</c> stay 0 (no per-input budget or user request exists).
    /// </summary>
    public override async Task<PendingSweepsResponse> PendingSweeps(PendingSweepsRequest request,
                                                                   ServerCallContext context)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var response = new PendingSweepsResponse();
        foreach (var close in (await unitOfWork.OnchainResolutionDbRepository.GetClosesAsync())
                             .OrderBy(c => c.SpentAtHeight))
        {
            var outputs = await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(close.ChannelId);
            foreach (var output in outputs.OrderBy(o => o.TransactionId.ToString()).ThenBy(o => o.OutputIndex))
            {
                if (output.State is not (OutputResolutionState.Pending or OutputResolutionState.Waiting
                                      or OutputResolutionState.Broadcast)
                 || ToWitnessType(output) is not { } witness)
                    continue;

                var resolving = output.ResolvingTransactionId is { } txId
                                    ? await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId)
                                    : null;
                response.PendingSweeps.Add(new PendingSweep
                {
                    Outpoint = ToOutPoint(output.TransactionId, output.OutputIndex),
                    WitnessType = witness,
                    AmountSat = (uint)Math.Min(OutputDescriptorData.TryDecode(output)?.AmountSat ?? 0, uint.MaxValue),
                    BroadcastAttempts = output.ResolvingTransactionId is null ? 0u : 1u,
                    SatPerVbyte = resolving is null ? 0 : (ulong)resolving.FeeratePerKw / 250,
                    DeadlineHeight = output.DeadlineHeight ?? 0,
                    MaturityHeight = output.WaitUntilHeight ?? 0
                });
            }
        }

        return response;
    }

    /// <summary>The sweep rows (see <see cref="ListSweeps"/>), one per transaction.</summary>
    private static async Task<List<BroadcastTransactionModel>> ReadSweepsAsync(IUnitOfWork unitOfWork)
    {
        var rows = new Dictionary<TxId, BroadcastTransactionModel>();
        foreach (var close in await unitOfWork.OnchainResolutionDbRepository.GetClosesAsync())
        {
            foreach (var row in await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(close.ChannelId))
                rows.TryAdd(row.TransactionId, row);
        }

        foreach (var row in await unitOfWork.BroadcastTransactionDbRepository.GetPendingAsync())
            rows.TryAdd(row.TransactionId, row);

        return rows.Values.Where(r => s_sweepPurposes.Contains(r.Purpose)
                                   && r.State is BroadcastState.Pending or BroadcastState.Confirmed)
                   .ToList();
    }

    /// <summary>LND's witness type of an output we sweep, or null for one that is not ours to take.</summary>
    private static WitnessType? ToWitnessType(OutputResolutionModel output) => output.Descriptor switch
    {
        OutputDescriptorKind.DelayedToLocal => WitnessType.CommitmentTimeLock,
        OutputDescriptorKind.PaymentToRemote => OutputDescriptorData.TryDecode(output) is { CsvDelay: > 0 }
                                             || output.WaitUntilHeight is not null
                                                    ? WitnessType.CommitmentToRemoteConfirmed
                                                    : WitnessType.CommitmentNoDelayTweakless,
        OutputDescriptorKind.LocalOfferedHtlc => WitnessType.HtlcOfferedTimeoutSecondLevel,
        OutputDescriptorKind.LocalReceivedHtlc => WitnessType.HtlcAcceptedSuccessSecondLevel,
        OutputDescriptorKind.RemoteReceivedHtlc => WitnessType.HtlcOfferedRemoteTimeout,
        OutputDescriptorKind.RemoteOfferedHtlc => WitnessType.HtlcAcceptedRemoteSuccess,
        OutputDescriptorKind.RevokedToLocal => WitnessType.CommitmentRevoke,
        OutputDescriptorKind.RevokedHtlc => output.HtlcDirection == HtlcDirection.Incoming
                                                ? WitnessType.HtlcAcceptedRevoke
                                                : WitnessType.HtlcOfferedRevoke,
        OutputDescriptorKind.RevokedSecondLevel => WitnessType.HtlcSecondLevelRevoke,
        OutputDescriptorKind.OurAnchor => WitnessType.CommitmentAnchor,
        _ => null
    };
}