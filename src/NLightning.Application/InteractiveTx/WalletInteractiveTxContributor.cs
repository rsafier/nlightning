using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.InteractiveTx;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Constants;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Infrastructure.Bitcoin.InteractiveTx;

/// <summary>
/// What our wallet adds to an interactive-tx negotiation (splicing plan §3.9, IT2-T3), over the persisted fee-input
/// reservations (<see cref="IFeeInputSelector"/>) and the reserved-inputs-only wallet signer
/// (<see cref="ILightningSigner.SignWalletTransaction(SignedTransaction, Guid, IReadOnlyList{SpentOutput})"/>).
/// </summary>
/// <remarks>
/// <para><b>Selection.</b> Wallet inputs are added only when <see cref="InteractiveTxContributionRequest.WalletAmount"/>
/// is positive: the selector then reserves mined P2WPKH/P2TR wallet outputs (never locked to a channel funding,
/// reserved for another spend or spent by a pending broadcast; all confirmed, so <c>require_confirmed_inputs</c> is
/// always met, and checked again against the UTXO set when the peer asked for it) worth the wallet amount plus the
/// requested outputs plus our fee: <see cref="InteractiveTxContributionRequest.ExtraWeight"/>, our outputs, our inputs
/// counted with BOLT 3's minimum witness weight of 107 (a P2TR key path input weighs less, the peer charges 107) and a
/// P2WPKH change output. The change goes back to a fresh wallet change address and is added only when it is at least
/// the P2WPKH dust limit (294 sat); otherwise the excess is fee. Without a wallet amount the request's outputs are
/// returned without inputs (a splice-out whose fees come from the channel balance, plan D16).</para>
/// <para><b>Anchors reserve (NL-379).</b> The selector may spend the reserve (it serves CPFP), so after reserving, the
/// outputs still backing the reserve plus our change must cover it, else the reservation is released and
/// <see cref="AnchorReserveException"/> is thrown, as for a withdrawal.</para>
/// <para><b>Lifetime (IT-ABT-01).</b> <see cref="ReleaseAsync"/> frees the reservation of an abandoned negotiation.
/// Once <see cref="SignAsync"/> has returned our witnesses the reservation is kept: a later
/// <see cref="ReleaseAsync"/> is refused (logged) because the transaction may still confirm, and only
/// <see cref="ConfirmAsync"/> ends it, when the chain monitor has seen an input spent. That guard lives in memory; after
/// a restart the driver's persisted negotiation state decides (it must not release a signed one).</para>
/// <para><b>Signing.</b> Before signing, the constructed transaction must hold every input and every output of our
/// contribution exactly as contributed, so a wrong construction never gets our signature; only the reservation's
/// inputs are signed (<c>SIGHASH_ALL</c>).</para>
/// </remarks>
public sealed class WalletInteractiveTxContributor : IInteractiveTxContributor
{
    /// <summary>The <c>nSequence</c> of our inputs: replaceable (BIP 125) and allowed by BOLT 2 (at most
    /// 0xFFFFFFFD).</summary>
    public const uint InputSequence = 0xFFFFFFFD;

    /// <summary>The purpose prefix of our reservations (<c>itx:&lt;purpose&gt;:&lt;channel id&gt;</c>).</summary>
    public const string ReservationPurposePrefix = "itx:";

    /// <summary>BOLT 3's minimum witness weight of an input in the collaborative fee calculation.</summary>
    internal const int MinimumWitnessWeight = 107;

    /// <summary>The weight of an input without its witness: outpoint, empty scriptSig, sequence, x 4.</summary>
    internal const int InputBaseWeight = (32 + 4 + 1 + 4) * 4;

    private const int MaxSelectionAttempts = 3;

    private readonly IAnchorReserveService? _anchorReserveService;
    private readonly IFeeInputSelector _feeInputSelector;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<WalletInteractiveTxContributor> _logger;
    private readonly IPrevTxInspector _prevTxInspector;
    private readonly IWalletPrevTxSource _prevTxSource;
    private readonly ConcurrentDictionary<Guid, byte> _signedReservations = new();
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public WalletInteractiveTxContributor(IFeeInputSelector feeInputSelector, ILightningSigner lightningSigner,
                                          IUtxoMemoryRepository utxoMemoryRepository,
                                          IWalletPrevTxSource prevTxSource, IPrevTxInspector prevTxInspector,
                                          IAnchorReserveService? anchorReserveService = null,
                                          ILogger<WalletInteractiveTxContributor>? logger = null)
    {
        _feeInputSelector = feeInputSelector;
        _lightningSigner = lightningSigner;
        _utxoMemoryRepository = utxoMemoryRepository;
        _prevTxSource = prevTxSource;
        _prevTxInspector = prevTxInspector;
        _anchorReserveService = anchorReserveService;
        _logger = logger ?? NullLogger<WalletInteractiveTxContributor>.Instance;
    }

    /// <summary>The purpose of the reservation of a negotiation.</summary>
    public static string GetReservationPurpose(InteractiveTxContributionRequest request) =>
        $"{ReservationPurposePrefix}{request.Purpose.ToString().ToLowerInvariant()}:{request.ChannelId}";

    /// <inheritdoc />
    public async Task<InteractiveTxContribution> ContributeAsync(InteractiveTxContributionRequest request,
                                                                 CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Outputs);
        if (request.WalletAmount.MilliSatoshi % 1_000 != 0)
            throw new ArgumentException("The wallet amount must be a whole number of satoshis", nameof(request));
        if (request.ExtraWeight < 0)
            throw new ArgumentException("The extra weight cannot be negative", nameof(request));
        if (request.FeeratePerKw == 0)
            throw new ArgumentException("The feerate must be positive", nameof(request));
        foreach (var output in request.Outputs)
        {
            if (output.Amount.MilliSatoshi % 1_000 != 0 || output.Amount.IsZero)
                throw new ArgumentException("Every output must be a positive whole number of satoshis",
                                            nameof(request));
        }

        if (request.WalletAmount.IsZero)
            return request.Outputs.Count == 0
                       ? InteractiveTxContribution.Empty
                       : new InteractiveTxContribution([], request.Outputs.ToList(), null);

        var targetSat = request.WalletAmount.Satoshi + request.Outputs.Sum(o => o.Amount.Satoshi);
        var outputsWeight = request.Outputs.Sum(o => GetOutputWeight(((byte[])o.ScriptPubKey).Length));
        var baseWeight = request.ExtraWeight + outputsWeight;
        var purpose = GetReservationPurpose(request);

        var padding = 0;
        for (var attempt = 1; ; attempt++)
        {
            var reservation = await _feeInputSelector.ReserveAsync(LightningMoney.Satoshis(targetSat),
                                                                   LightningMoney.Satoshis(request.FeeratePerKw),
                                                                   baseWeight + padding, purpose,
                                                                   cancellationToken);
            try
            {
                var plan = PlanChange(reservation, targetSat, baseWeight, request.FeeratePerKw);
                if (plan is null)
                {
                    // Our inputs' witnesses weigh less than BOLT 3's minimum (P2TR): ask for more and try again
                    await _feeInputSelector.ReleaseAsync(reservation.Id, CancellationToken.None);
                    if (attempt >= MaxSelectionAttempts)
                        throw new InsufficientFundsException(
                            LightningMoney.Satoshis(targetSat + FeeSat(request.FeeratePerKw,
                                                                       baseWeight + GetInputsWeight(reservation))),
                            reservation.Total);

                    padding += reservation.Inputs.Sum(i => Math.Max(0, MinimumWitnessWeight
                                                                        - (i.InputWeight - InputBaseWeight)))
                             + WalletWeights.P2WpkhOutputWeight;
                    continue;
                }

                if (request.RequireConfirmedInputs)
                    EnsureConfirmed(reservation);

                await EnsureReserveKeptAsync(reservation, plan.Value.ChangeSat, cancellationToken);

                var inputs = new List<ContributedInput>(reservation.Inputs.Count);
                foreach (var input in reservation.Inputs)
                    inputs.Add(await ToContributedInputAsync(input, cancellationToken));

                var outputs = request.Outputs.ToList();
                if (plan.Value.ChangeSat > 0 && reservation.ChangeScript is { } changeScript)
                    outputs.Add(new ContributedOutput(LightningMoney.Satoshis(plan.Value.ChangeSat), changeScript,
                                                      true));

                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "Contributing {Inputs} wallet input(s) worth {Total} sat to {Purpose} (reservation "
                      + "{ReservationId}, change {Change} sat)", inputs.Count, reservation.Total.Satoshi, purpose,
                        reservation.Id, plan.Value.ChangeSat);

                return new InteractiveTxContribution(inputs, outputs, reservation.Id);
            }
            catch
            {
                await _feeInputSelector.ReleaseAsync(reservation.Id, CancellationToken.None);
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(InteractiveTxContribution contribution,
                                   CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (contribution.ReservationId is not { } reservationId)
            return;

        if (_signedReservations.ContainsKey(reservationId))
        {
            // IT-ABT-01: our tx_signatures may be out, the transaction may still confirm
            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "Not releasing reservation {ReservationId}: its inputs were signed for an interactive "
                  + "transaction, which may still confirm; it ends when an input is spent", reservationId);
            return;
        }

        await _feeInputSelector.ReleaseAsync(reservationId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Witness>> SignAsync(ConstructedInteractiveTx transaction,
                                                  InteractiveTxContribution contribution,
                                                  IReadOnlyList<SpentOutput> otherSpentOutputs,
                                                  CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(otherSpentOutputs);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureOutputsPresent(transaction, contribution);
        if (contribution.Inputs.Count == 0)
            return Task.FromResult<IReadOnlyList<Witness>>([]);

        if (contribution.ReservationId is not { } reservationId)
            throw new InvalidOperationException("A contribution with inputs has no reservation");

        var inputIndexes = GetOurInputIndexes(transaction, contribution);

        // Mark first: from here on our witnesses may leave, so the reservation must outlive an abort (IT-ABT-01)
        _signedReservations.TryAdd(reservationId, 0);

        var signed = new SignedTransaction(transaction.TxId, (byte[])transaction.UnsignedTx.Clone());
        try
        {
            if (!_lightningSigner.SignWalletTransaction(signed, reservationId, otherSpentOutputs))
                throw new InvalidOperationException("The signer found no wallet input in the transaction");
        }
        catch
        {
            // Nothing was signed, so nothing can have left
            _signedReservations.TryRemove(reservationId, out _);
            throw;
        }

        if (!InteractiveTxTransactionReader.TryReadTransaction(signed.RawTxBytes, out var signedTx)
         || signedTx is null)
            throw new InvalidOperationException("The signed transaction does not parse");
        if (!signedTx.GetHash().ToBytes().AsSpan().SequenceEqual((byte[])transaction.TxId))
            throw new InvalidOperationException("The signer changed the transaction's txid");

        var witnesses = new List<Witness>(inputIndexes.Count);
        foreach (var index in inputIndexes)
        {
            var witScript = signedTx.Inputs[index].WitScript;
            if (witScript is null || witScript.PushCount == 0)
                throw new InvalidOperationException($"Input {index} of our contribution was not signed");

            witnesses.Add(new Witness(InteractiveTxTransactionReader.WriteWitness(witScript)));
        }

        return Task.FromResult<IReadOnlyList<Witness>>(witnesses);
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(InteractiveTxContribution contribution,
                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (contribution.ReservationId is not { } reservationId)
            return true;

        if (!await _feeInputSelector.ConfirmAsync(reservationId, cancellationToken))
            return false;

        _signedReservations.TryRemove(reservationId, out _);
        return true;
    }

    /// <summary>
    /// The change of a reservation under BOLT 3's collaborative fee rule (each input at least 107 witness weight): the
    /// change amount (0 for none), or null when the reservation cannot pay that fee.
    /// </summary>
    internal static (long ChangeSat, long FeeSat)? PlanChange(FeeInputReservation reservation, long targetSat,
                                                              int baseWeight, uint feeratePerKw)
    {
        var weight = baseWeight + GetInputsWeight(reservation);
        var excessSat = reservation.Total.Satoshi - targetSat;

        if (reservation.ChangeScript is not null)
        {
            var feeWithChange = FeeSat(feeratePerKw, weight + WalletWeights.P2WpkhOutputWeight);
            var changeSat = excessSat - feeWithChange;
            if (changeSat >= WalletWeights.P2WpkhDustLimitSat)
                return (changeSat, feeWithChange);
        }

        var feeWithoutChange = FeeSat(feeratePerKw, weight);
        return excessSat >= feeWithoutChange ? (0, excessSat) : null;
    }

    /// <summary>Our inputs' weight with BOLT 3's minimum witness weight per input.</summary>
    private static int GetInputsWeight(FeeInputReservation reservation) =>
        reservation.Inputs.Sum(i => InputBaseWeight + Math.Max(MinimumWitnessWeight, i.InputWeight - InputBaseWeight));

    private static int GetOutputWeight(int scriptLength) =>
        (8 + (scriptLength < 0xFD ? 1 : 3) + scriptLength) * 4;

    private static long FeeSat(uint feeratePerKw, long weight) => (feeratePerKw * weight + 999) / 1000;

    private void EnsureConfirmed(FeeInputReservation reservation)
    {
        foreach (var input in reservation.Inputs)
        {
            if (!_utxoMemoryRepository.TryGetUtxo(input.TxId, input.Index, out var utxo) || utxo.BlockHeight == 0)
                throw new InvalidOperationException(
                    $"Wallet output {input.TxId}:{input.Index} is not confirmed; the peer requires confirmed inputs");
        }
    }

    /// <summary>
    /// With the inputs reserved (they no longer count as available): the outputs still backing the anchors reserve
    /// plus our change must cover it (NL-379).
    /// </summary>
    private async Task EnsureReserveKeptAsync(FeeInputReservation reservation, long changeSat,
                                              CancellationToken cancellationToken)
    {
        if (_anchorReserveService is null)
            return;

        var status = await _anchorReserveService.GetStatusAsync(cancellationToken);
        if (status.RequiredReserve.IsZero)
            return;

        var keptSat = status.AvailableBalance.Satoshi + changeSat;
        if (keptSat >= status.RequiredReserve.Satoshi)
            return;

        throw new AnchorReserveException(
            $"Contributing {reservation.Total.Satoshi - changeSat} sat to an interactive transaction would leave "
          + $"{keptSat} sat in the wallet, below the anchors reserve of {status.RequiredReserve.Satoshi} sat for "
          + $"{status.AnchorsChannelCount} anchors channel(s).",
            LightningMoney.Satoshis(reservation.Total.Satoshi - changeSat) + status.RequiredReserve,
            status.AvailableBalance + reservation.Total, status.RequiredReserve);
    }

    private async Task<ContributedInput> ToContributedInputAsync(WalletInput input,
                                                                 CancellationToken cancellationToken)
    {
        var height = _utxoMemoryRepository.TryGetUtxo(input.TxId, input.Index, out var utxo) ? utxo.BlockHeight : 0;
        var prevTx = await _prevTxSource.GetTransactionAsync(input.TxId, height, cancellationToken)
                  ?? throw new InvalidOperationException(
                         $"The transaction of wallet output {input.TxId}:{input.Index} is not available from bitcoind");

        // What we send as prevtx must be what the peer checks (IT-R-01): the same txid, amount and witness program
        var inspection = _prevTxInspector.Inspect(prevTx, input.Index);
        if (!inspection.IsValid || inspection.TxId is not { } txId || txId != input.TxId
         || inspection.Amount is not { } amount || amount.Satoshi != input.Amount.Satoshi
         || inspection.ScriptPubKey is not { } script || script != input.ScriptPubKey)
            throw new InvalidOperationException(
                $"The transaction read for wallet output {input.TxId}:{input.Index} does not match it"
              + (inspection.FailureReason is null ? string.Empty : $": {inspection.FailureReason}"));

        return new ContributedInput(input.TxId, input.Index, prevTx, InputSequence, input.Amount, input.ScriptPubKey,
                                    input.InputWeight);
    }

    /// <summary>The transaction indexes of our inputs, in transaction (ascending <c>serial_id</c>) order.</summary>
    private static List<int> GetOurInputIndexes(ConstructedInteractiveTx transaction,
                                                InteractiveTxContribution contribution)
    {
        var indexes = new List<int>(contribution.Inputs.Count);
        foreach (var input in contribution.Inputs)
        {
            var matches = Enumerable.Range(0, transaction.Inputs.Count)
                                    .Where(i => transaction.Inputs[i].PrevTxId == input.PrevTxId
                                             && transaction.Inputs[i].PrevTxVout == input.PrevTxVout)
                                    .ToList();
            if (matches.Count != 1 || transaction.Inputs[matches[0]] is not
                { AddedBy: InteractiveTxParty.Local, IsShared: false } ours || ours.Sequence != input.Sequence)
                throw new InvalidOperationException(
                    $"Wallet input {input.PrevTxId}:{input.PrevTxVout} is not in the constructed transaction as ours");

            indexes.Add(matches[0]);
        }

        indexes.Sort();
        return indexes;
    }

    /// <summary>Every output we contributed is in the transaction as ours, with its amount and script.</summary>
    private static void EnsureOutputsPresent(ConstructedInteractiveTx transaction,
                                             InteractiveTxContribution contribution)
    {
        var ours = transaction.Outputs.Where(o => o is { AddedBy: InteractiveTxParty.Local, IsShared: false })
                              .ToList();
        foreach (var output in contribution.Outputs)
        {
            var index = ours.FindIndex(o => o.Amount.Satoshi == output.Amount.Satoshi
                                         && o.ScriptPubKey == output.ScriptPubKey);
            if (index < 0)
                throw new InvalidOperationException(
                    $"Our output of {output.Amount.Satoshi} sat is not in the constructed transaction");

            ours.RemoveAt(index);
        }
    }
}