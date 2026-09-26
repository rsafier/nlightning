using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Crypto;

namespace NLightning.Application.Onchain.Fees;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;

/// <summary>
/// Bumps our unconfirmed sweeps, claims and penalties by RBF (BOLT 5 plan §3.7, O6-T1; NL-317): every round, each
/// <see cref="BroadcastState.Pending"/> transaction of purpose <see cref="BroadcastPurpose.Sweep"/>,
/// <see cref="BroadcastPurpose.HtlcClaim"/> or <see cref="BroadcastPurpose.Penalty"/> that
/// <see cref="SweepFeePolicy.ShouldBump"/> says has waited long enough is re-signed with a higher absolute fee
/// (<see cref="SweepFeePolicy.DecideReplacement"/>: the BIP 125 minimum or the estimate for its deadline's target,
/// whichever is higher, within the caps) and the same inputs, sequences, lock time and destination.
/// </summary>
/// <remarks>
/// <para>The replacement is persisted before it is broadcast (D4): its row (<c>ReplacesTxId</c> = the old one), the
/// old row <see cref="BroadcastState.Replaced"/> and every output row that named the old transaction pointing to the new
/// one go into the executor's one save; the executor publishes afterwards. The deadline of a transaction is the
/// earliest <see cref="OutputResolutionModel.DeadlineHeight"/> of the outputs it spends.</para>
/// <para>Re-signing: every input spends one of the channel's output rows (by outpoint); the key comes from the row's
/// descriptor (<see cref="OutputDescriptorKind.DelayedToLocal"/> → delayed key at our point,
/// <see cref="OutputDescriptorKind.PaymentToRemote"/> → payment basepoint, the peer's HTLC outputs → HTLC key at its
/// point, the revoked outputs → revocation key with the peer's secret of the revoked commitment from our shachain),
/// the amount and witness script from its <see cref="OutputDescriptorData"/>. Only the signature (witness item 0) of
/// each input changes; every other witness item is kept. An input whose row or data is missing leaves the transaction as
/// it is (logged once).</para>
/// <para>Retirement (NL-294): a pending transaction whose input was spent on chain by another transaction can never
/// confirm and is abandoned; a pending one that no output row names any more and that a later row replaces (a penalty
/// batch split into singles, O5-T3) is marked replaced. Neither is rebroadcast after that.</para>
/// <para>Pre-signed HTLC-timeout/success transactions and our commitment carry a fee fixed at signing and are never
/// bumped (no anchors: O7).</para>
/// </remarks>
public sealed class SweepScheduler : ISweepScheduler
{
    private static readonly BroadcastPurpose[] s_bumpable =
        [BroadcastPurpose.Sweep, BroadcastPurpose.HtlcClaim, BroadcastPurpose.Penalty];

    private readonly IFeeService _feeService;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<SweepScheduler> _logger;
    private readonly SweepFeePolicy _policy;
    private readonly ISecretStorageServiceFactory? _secretStorageServiceFactory;
    private readonly HashSet<TxId> _loggedOnce = [];

    public SweepScheduler(IFeeService feeService, ILightningSigner lightningSigner, ILogger<SweepScheduler> logger,
                          SweepFeePolicy? policy = null,
                          ISecretStorageServiceFactory? secretStorageServiceFactory = null)
    {
        _feeService = feeService;
        _lightningSigner = lightningSigner;
        _logger = logger;
        _policy = policy ?? new SweepFeePolicy();
        _secretStorageServiceFactory = secretStorageServiceFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutputResolverAction>> PlanAsync(ChannelCloseModel close,
                                                                     IReadOnlyList<OutputResolutionModel> outputs,
                                                                     uint height, IUnitOfWork unitOfWork,
                                                                     CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var broadcasts = await unitOfWork.BroadcastTransactionDbRepository.GetByChannelIdAsync(close.ChannelId);
        var rows = outputs.ToDictionary(o => (o.TransactionId, o.OutputIndex));
        var actions = new List<OutputResolverAction>();
        Secret? revocationSecret = null;

        foreach (var broadcast in broadcasts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (broadcast.State != BroadcastState.Pending || !s_bumpable.Contains(broadcast.Purpose))
                continue;

            var txId = broadcast.TransactionId;
            Transaction tx;
            try
            {
                tx = Transaction.Load(broadcast.RawTransaction, Network.Main);
            }
            catch (FormatException e)
            {
                LogOnce(txId, e, "Stored broadcast {TxId} of channel {ChannelId} does not parse; not bumping it",
                        Display(txId), close.ChannelId);
                continue;
            }

            var spent = tx.Inputs.Select(i => rows.GetValueOrDefault((new TxId(i.PrevOut.Hash.ToBytes()),
                                                                       i.PrevOut.N)))
                          .ToList();

            // An input spent on chain by another transaction: this one can never confirm
            if (spent.Any(r => r is { State: OutputResolutionState.Resolved or OutputResolutionState.Irrevocable }))
            {
                _logger.LogInformation("{Purpose} {TxId} of channel {ChannelId} lost an input to another transaction "
                                     + "on chain; it is no longer rebroadcast", broadcast.Purpose, Display(txId),
                                       close.ChannelId);
                actions.Add(new StageWriteAction($"abandon {Display(txId)}",
                                                 (uow, _) => uow.BroadcastTransactionDbRepository
                                                                .MarkAbandonedAsync(txId)));
                continue;
            }

            var naming = outputs.Where(o => o.ResolvingTransactionId == txId).ToList();
            if (naming.Count == 0)
            {
                // Superseded by a later row (a penalty batch split into singles): retire it
                if (broadcasts.Any(b => b.ReplacesTransactionId == txId))
                    actions.Add(new StageWriteAction($"replaced {Display(txId)}",
                                                     (uow, _) => uow.BroadcastTransactionDbRepository
                                                                    .MarkReplacedAsync(txId)));
                continue;
            }

            var deadline = spent.Where(r => r?.DeadlineHeight is not null).Select(r => r!.DeadlineHeight).Min();
            if (!_policy.ShouldBump(broadcast.FirstBroadcastHeight, height, deadline))
                continue;

            if (spent.Any(r => r is null) || tx.Outputs.Count != 1)
            {
                LogOnce(txId, null, "{Purpose} {TxId} of channel {ChannelId} spends an output without a row, or has "
                                  + "more than one output; it is not bumped", broadcast.Purpose, Display(txId),
                        close.ChannelId);
                continue;
            }

            if (spent.Any(r => IsRevoked(r!.Descriptor)) && revocationSecret is null)
            {
                revocationSecret = await LoadRevocationSecretAsync(close, unitOfWork);
                if (revocationSecret is null)
                {
                    LogOnce(txId, null, "The peer's secret of revoked commitment {Number} of channel {ChannelId} is "
                                      + "unknown; penalty {TxId} is not bumped", close.CommitmentNumber,
                            close.ChannelId, Display(txId));
                    continue;
                }
            }

            var replacement = await BuildReplacementAsync(close, broadcast, tx, spent!, deadline, height,
                                                           revocationSecret, cancellationToken);
            if (replacement is null)
                continue;

            actions.Add(new BroadcastAction(replacement));
            foreach (var row in naming)
                actions.Add(new UpsertOutputAction(row with { ResolvingTransactionId = replacement.TransactionId }));
            actions.Add(new StageWriteAction($"replaced {Display(txId)}",
                                             (uow, _) => uow.BroadcastTransactionDbRepository
                                                            .MarkReplacedAsync(txId)));
        }

        return actions;
    }

    private async Task<BroadcastTransactionModel?> BuildReplacementAsync(ChannelCloseModel close,
                                                                        BroadcastTransactionModel broadcast,
                                                                        Transaction tx,
                                                                        IReadOnlyList<OutputResolutionModel> spent,
                                                                        uint? deadline, uint height,
                                                                        Secret? revocationSecret,
                                                                        CancellationToken cancellationToken)
    {
        var txId = broadcast.TransactionId;
        var inputs = new List<(OutputDescriptorData Data, SweepKeyKind Kind)>();
        foreach (var row in spent)
        {
            if (OutputDescriptorData.TryDecode(row) is not { } data || GetKeyKind(row.Descriptor) is not { } kind
             || kind is SweepKeyKind.DelayedPayment or SweepKeyKind.HtlcRemotePoint
             && data.PerCommitmentPoint is null)
            {
                LogOnce(txId, null, "Output {Vout} of {OutputTxId} ({Kind}) spent by {TxId} of channel {ChannelId} "
                                  + "has no usable descriptor; not bumping it", row.OutputIndex,
                        Display(row.TransactionId), row.Descriptor, Display(txId), close.ChannelId);
                return null;
            }

            inputs.Add((data, kind));
        }

        var total = inputs.Aggregate(0UL, (sum, i) => sum + i.Data.AmountSat);
        var outputSat = (ulong)tx.Outputs[0].Value.Satoshi;
        if (outputSat >= total)
        {
            LogOnce(txId, null, "{TxId} of channel {ChannelId} pays out more than its inputs' recorded value; not "
                              + "bumping it", Display(txId), close.ChannelId);
            return null;
        }

        var oldFee = total - outputSat;

        // The replacement has the same shape; a signature may be one byte longer
        var weight = (long)tx.GetVirtualSize() * 4 + tx.Inputs.Count * 4;
        var target = _policy.GetConfirmationTarget(height, deadline);
        var estimate = await FeeEstimates.GetForTargetAsync(_feeService, target, _logger, cancellationToken);
        var destination = tx.Outputs[0].ScriptPubKey.ToBytes();
        var dust = ShutdownScriptValidator.GetDustThresholdSat(destination);
        var isPenalty = broadcast.Purpose == BroadcastPurpose.Penalty;
        var decision = _policy.DecideReplacement(total, oldFee, weight, estimate, isPenalty, height, deadline, dust);
        if (decision is null)
        {
            LogOnce(txId, null, "{Purpose} {TxId} of channel {ChannelId} is unconfirmed, but no replacement can pay "
                              + "more than its {FeeSat} sat fee within the cap; it is kept", broadcast.Purpose,
                    Display(txId), close.ChannelId, oldFee);
            return null;
        }

        var replacement = tx.Clone();
        replacement.Outputs[0].Value = Money.Satoshis(total - decision.FeeSat);
        foreach (var input in replacement.Inputs)
            input.WitScript = WitScript.Empty;
        var unsignedBytes = replacement.ToBytes();

        try
        {
            for (var i = 0; i < replacement.Inputs.Count; i++)
            {
                var (data, kind) = inputs[i];
                var oldWitness = tx.Inputs[i].WitScript.Pushes.ToArray();
                if (oldWitness.Length == 0)
                    return null;

                var witnessScript = data.WitnessScript
                                 ?? (kind != SweepKeyKind.Payment && oldWitness.Length >= 3 ? oldWitness[^1] : null);
                var context = new SweepSigningContext(unsignedBytes, i, witnessScript, data.AmountSat, kind,
                                                      kind == SweepKeyKind.Revocation ? null : data.PerCommitmentPoint,
                                                      kind == SweepKeyKind.Revocation ? revocationSecret : null);
                var compact = _lightningSigner.SignSweepInput(close.ChannelId, context);
                if (!ECDSASignature.TryParseFromCompact(compact, out var ecdsa))
                    return null;

                oldWitness[0] = new TransactionSignature(ecdsa, SigHash.All).ToBytes();
                replacement.Inputs[i].WitScript = new WitScript(oldWitness);
            }
        }
        catch (Exception e) when (e is Domain.Exceptions.SignerException or ArgumentException
                                      or InvalidOperationException)
        {
            LogOnce(txId, e, "Cannot re-sign {Purpose} {TxId} of channel {ChannelId}; it is kept", broadcast.Purpose,
                    Display(txId), close.ChannelId);
            return null;
        }

        var newTxId = new TxId(replacement.GetHash().ToBytes());
        _logger.LogWarning("{Purpose} {TxId} of channel {ChannelId} is unconfirmed since height {Since} (deadline "
                         + "{Deadline}); replacing it with {NewTxId}: fee {OldFee} -> {NewFee} sat ({Rate} sat/kw, "
                         + "target {Target} blocks)", broadcast.Purpose, Display(txId), close.ChannelId,
                           broadcast.FirstBroadcastHeight, deadline, Display(newTxId), oldFee, decision.FeeSat,
                           decision.FeeratePerKw, target);
        return new BroadcastTransactionModel(new SignedTransaction(newTxId, replacement.ToBytes()), broadcast.Purpose,
                                             close.ChannelId, height, decision.FeeratePerKw, txId);
    }

    private async Task<Secret?> LoadRevocationSecretAsync(ChannelCloseModel close, IUnitOfWork unitOfWork)
    {
        if (_secretStorageServiceFactory is null || close.CommitmentNumber is not { } number)
            return null;

        var entries = await unitOfWork.RemoteShachainDbRepository.GetByChannelIdAsync(close.ChannelId);
        using var shachain = _secretStorageServiceFactory.CreatePerCommitmentStorage();
        try
        {
            shachain.Load(entries);
            return shachain.DeriveOldSecret(PerCommitmentIndex.From(number));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static SweepKeyKind? GetKeyKind(OutputDescriptorKind descriptor) => descriptor switch
    {
        OutputDescriptorKind.DelayedToLocal => SweepKeyKind.DelayedPayment,
        OutputDescriptorKind.PaymentToRemote => SweepKeyKind.Payment,
        OutputDescriptorKind.RemoteReceivedHtlc or OutputDescriptorKind.RemoteOfferedHtlc =>
            SweepKeyKind.HtlcRemotePoint,
        OutputDescriptorKind.RevokedToLocal or OutputDescriptorKind.RevokedHtlc
                                            or OutputDescriptorKind.RevokedSecondLevel => SweepKeyKind.Revocation,
        _ => null
    };

    private static bool IsRevoked(OutputDescriptorKind descriptor) =>
        descriptor is OutputDescriptorKind.RevokedToLocal or OutputDescriptorKind.RevokedHtlc
                                                          or OutputDescriptorKind.RevokedSecondLevel;

    private void LogOnce(TxId txId, Exception? exception, string message, params object?[] args)
    {
        lock (_loggedOnce)
        {
            if (!_loggedOnce.Add(txId))
                return;
        }

#pragma warning disable CA2254 // The templates are constants of this class
        _logger.LogWarning(exception, message, args);
#pragma warning restore CA2254
    }

    private static string Display(TxId txId) => new uint256(txId).ToString();
}