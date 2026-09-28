using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
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
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// What our wallet adds to an interactive-tx negotiation (splicing plan §3.9, IT2-T3), over the persisted fee-input
/// reservations (<see cref="IFeeInputSelector"/>) and the reserved-inputs-only wallet signer
/// (<see cref="ILightningSigner.SignWalletTransaction(SignedTransaction, Guid, IReadOnlyList{SpentOutput})"/>).
/// </summary>
/// <remarks>
/// <para><b>Registration.</b> A singleton (<see cref="InteractiveTxContributorServiceCollectionExtensions"/>): the
/// in-process part of the IT-ABT-01 guard and the release/sign gate live on the instance.</para>
/// <para><b>Selection.</b> Wallet inputs are added only when <see cref="InteractiveTxContributionRequest.WalletAmount"/>
/// is positive: the selector then reserves mined P2WPKH/P2TR wallet outputs (never locked to a channel funding,
/// reserved for another spend or spent by a pending broadcast; all confirmed, so <c>require_confirmed_inputs</c> is
/// always met, and checked again against the UTXO set when the peer asked for it) worth the wallet amount plus the
/// requested outputs plus our fee: <see cref="InteractiveTxContributionRequest.ExtraWeight"/>, our outputs, our inputs
/// each counted with at least the minimum witness weight of 107 (BOLT 3 Appendix G, "Expected Fee Calculation"; a P2TR
/// key path input weighs less, the peer charges 107) and a P2WPKH change output. The change goes back to a fresh wallet
/// change address and is added only when it is at least the P2WPKH dust limit (294 sat); otherwise the excess is fee.
/// Without a wallet amount the request's outputs are returned without inputs (a splice-out whose fees come from the
/// channel balance, plan D16).</para>
/// <para><b>prevtx size.</b> A <c>tx_add_input</c> carries <c>prevtx</c> with a u16 length inside a message of at most
/// 65,535 bytes, so a wallet output paid by a larger transaction (a big batch payout) cannot be contributed
/// (<see cref="MaxPrevTxLength"/>). Its reservation is held while the selector picks again (so those outputs are not
/// picked), then released.</para>
/// <para><b>Anchors reserve (NL-379).</b> The selector may spend the reserve (it serves CPFP), so after reserving, the
/// outputs still backing the reserve plus our change must cover it, else the reservation is released and
/// <see cref="AnchorReserveException"/> is thrown, as for a withdrawal.</para>
/// <para><b>Lifetime (IT-ABT-01).</b> <see cref="ReleaseAsync"/> frees the reservation of an abandoned negotiation.
/// Once our witnesses may have left, the reservation is kept until <see cref="ConfirmAsync"/> sees an input spent.
/// The durable source of truth is the driver's persisted negotiation (<see cref="IInteractiveTxSessionDbRepository"/>,
/// stored from our <c>commitment_signed</c>, before any <c>tx_signatures</c>): <see cref="SignAsync"/> refuses to sign
/// a reservation no stored, not aborted negotiation holds, and <see cref="ReleaseAsync"/> refuses (logged) while one
/// does, also after a restart. The driver marks a negotiation <see cref="InteractiveTxSessionState.Aborted"/> (saved)
/// before it releases its reservation. Without a session store (no scope factory, or a unit of work that stores no
/// sessions) only the in-process guard applies. <see cref="ReleaseOrphanedReservationsAsync"/> frees, at startup, the
/// <c>itx:</c> reservations no stored negotiation holds (a crash before our <c>commitment_signed</c>). Release, sign and
/// confirm run one at a time, so a release never frees a reservation a concurrent signature is using.</para>
/// <para><b>Signing.</b> Before signing, the constructed transaction's bytes must be the transaction its metadata
/// describes (txid, version 2, locktime, every input's outpoint and sequence, every output's amount and script, in
/// order, no witness), and it must hold every input and every output of our contribution exactly as contributed, so a
/// wrong construction never gets our signature; only the reservation's inputs are signed (<c>SIGHASH_ALL</c>).</para>
/// </remarks>
public sealed class WalletInteractiveTxContributor : IInteractiveTxContributor
{
    /// <summary>The <c>nSequence</c> of our inputs: replaceable (BIP 125) and allowed by BOLT 2 (at most
    /// 0xFFFFFFFD).</summary>
    public const uint InputSequence = 0xFFFFFFFD;

    /// <summary>The purpose prefix of our reservations (<c>itx:&lt;purpose&gt;:&lt;channel id&gt;</c>).</summary>
    public const string ReservationPurposePrefix = "itx:";

    /// <summary>
    /// The largest <c>prevtx</c> a <c>tx_add_input</c> of ours can carry: a lightning message is at most 65,535 bytes
    /// (BOLT 8), less the type (2), <c>channel_id</c> (32), <c>serial_id</c> (8), <c>prevtx_len</c> (2),
    /// <c>prevtx_vout</c> (4) and <c>sequence</c> (4); our wallet inputs carry no TLV.
    /// </summary>
    public const int MaxPrevTxLength = ushort.MaxValue - (2 + 32 + 8 + 2 + 4 + 4);

    /// <summary>The minimum witness weight of an input in the collaborative fee calculation (BOLT 3 Appendix G,
    /// "Expected Fee Calculation").</summary>
    internal const int MinimumWitnessWeight = 107;

    /// <summary>The weight of an input without its witness: outpoint, empty scriptSig, sequence, x 4.</summary>
    internal const int InputBaseWeight = (32 + 4 + 1 + 4) * 4;

    /// <summary>The version of every interactive-tx construction (BOLT 3 Appendix G).</summary>
    private const uint TransactionVersion = 2;

    private const int MaxSelectionAttempts = 3;
    private const int MaxOversizedPrevTxRetries = 3;

    private readonly IAnchorReserveService? _anchorReserveService;
    private readonly IFeeInputSelector _feeInputSelector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILightningSigner _lightningSigner;
    private readonly ConcurrentDictionary<Guid, byte> _liveReservations = new();
    private readonly ILogger<WalletInteractiveTxContributor> _logger;
    private readonly IPrevTxInspector _prevTxInspector;
    private readonly IWalletPrevTxSource _prevTxSource;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly ConcurrentDictionary<Guid, byte> _signedReservations = new();
    private readonly IInteractiveTxTransactionParser _transactionParser;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public WalletInteractiveTxContributor(IFeeInputSelector feeInputSelector, ILightningSigner lightningSigner,
                                          IUtxoMemoryRepository utxoMemoryRepository,
                                          IWalletPrevTxSource prevTxSource, IPrevTxInspector prevTxInspector,
                                          IInteractiveTxTransactionParser transactionParser,
                                          IServiceScopeFactory? scopeFactory = null,
                                          IAnchorReserveService? anchorReserveService = null,
                                          ILogger<WalletInteractiveTxContributor>? logger = null)
    {
        _feeInputSelector = feeInputSelector;
        _lightningSigner = lightningSigner;
        _utxoMemoryRepository = utxoMemoryRepository;
        _prevTxSource = prevTxSource;
        _prevTxInspector = prevTxInspector;
        _transactionParser = transactionParser;
        _scopeFactory = scopeFactory;
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

        // Reservations whose outputs have a prevtx too large for tx_add_input: held while we pick again
        var held = new List<Guid>();
        try
        {
            var padding = 0;
            var weightAttempts = 0;
            while (true)
            {
                FeeInputReservation reservation;
                try
                {
                    reservation = await _feeInputSelector.ReserveAsync(LightningMoney.Satoshis(targetSat),
                                                                       LightningMoney.Satoshis(request.FeeratePerKw),
                                                                       baseWeight + padding, purpose,
                                                                       cancellationToken);
                }
                catch (InsufficientFundsException e) when (held.Count > 0)
                {
                    throw new InvalidOperationException(
                        "The wallet cannot fund the contribution without outputs whose previous transaction is larger "
                      + $"than the {MaxPrevTxLength} bytes a tx_add_input can carry", e);
                }

                try
                {
                    var plan = PlanChange(reservation, targetSat, baseWeight, request.FeeratePerKw);
                    if (plan is null)
                    {
                        // Our inputs' witnesses weigh less than the minimum witness weight (P2TR): ask for more
                        await _feeInputSelector.ReleaseAsync(reservation.Id, CancellationToken.None);
                        if (++weightAttempts >= MaxSelectionAttempts)
                            throw new InsufficientFundsException(
                                LightningMoney.Satoshis(targetSat
                                                      + FeeSat(request.FeeratePerKw,
                                                               baseWeight + GetInputsWeight(reservation))),
                                reservation.Total);

                        padding += reservation.Inputs.Sum(i => Math.Max(0, MinimumWitnessWeight
                                                                            - (i.InputWeight - InputBaseWeight)))
                                 + WalletWeights.P2WpkhOutputWeight;
                        continue;
                    }

                    if (request.RequireConfirmedInputs)
                        EnsureConfirmed(reservation);

                    var inputs = new List<ContributedInput>(reservation.Inputs.Count);
                    var oversized = new List<WalletInput>();
                    foreach (var input in reservation.Inputs)
                    {
                        var contributed = await ToContributedInputAsync(input, cancellationToken);
                        if (contributed.PrevTx.Length > MaxPrevTxLength)
                            oversized.Add(input);
                        else
                            inputs.Add(contributed);
                    }

                    if (oversized.Count > 0)
                    {
                        // Keep these outputs out of the next selection by holding the reservation until we are done
                        held.Add(reservation.Id);
                        if (_logger.IsEnabled(LogLevel.Warning))
                            _logger.LogWarning(
                                "Wallet output(s) {Outputs} cannot go into a tx_add_input: the previous transaction is "
                              + "larger than {Max} bytes; selecting other outputs",
                                string.Join(", ", oversized.Select(i => $"{i.TxId}:{i.Index}")), MaxPrevTxLength);

                        if (held.Count > MaxOversizedPrevTxRetries)
                            throw new InvalidOperationException(
                                "Too many wallet outputs have a previous transaction larger than the "
                              + $"{MaxPrevTxLength} bytes a tx_add_input can carry");

                        continue;
                    }

                    // The held outputs go back to the wallet before the reserve is measured
                    await ReleaseHeldAsync(held);

                    await EnsureReserveKeptAsync(reservation, plan.Value.ChangeSat, cancellationToken);

                    var outputs = request.Outputs.ToList();
                    if (plan.Value.ChangeSat > 0 && reservation.ChangeScript is { } changeScript)
                        outputs.Add(new ContributedOutput(LightningMoney.Satoshis(plan.Value.ChangeSat), changeScript,
                                                          true));

                    if (_logger.IsEnabled(LogLevel.Information))
                        _logger.LogInformation(
                            "Contributing {Inputs} wallet input(s) worth {Total} sat to {Purpose} (reservation "
                          + "{ReservationId}, change {Change} sat)", inputs.Count, reservation.Total.Satoshi, purpose,
                            reservation.Id, plan.Value.ChangeSat);

                    _liveReservations.TryAdd(reservation.Id, 0);
                    return new InteractiveTxContribution(inputs, outputs, reservation.Id);
                }
                catch
                {
                    if (!held.Contains(reservation.Id))
                        await _feeInputSelector.ReleaseAsync(reservation.Id, CancellationToken.None);

                    throw;
                }
            }
        }
        finally
        {
            await ReleaseHeldAsync(held);
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(InteractiveTxContribution contribution,
                                   CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (contribution.ReservationId is not { } reservationId)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_signedReservations.ContainsKey(reservationId))
            {
                // IT-ABT-01: our tx_signatures may be out, the transaction may still confirm
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(
                        "Not releasing reservation {ReservationId}: its inputs were signed for an interactive "
                      + "transaction, which may still confirm; it ends when an input is spent", reservationId);
                return;
            }

            var (_, session) = await FindStoredSessionAsync(reservationId, cancellationToken);
            if (session is not null)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                    _logger.LogWarning(
                        "Not releasing reservation {ReservationId}: the stored negotiation {SessionId} of channel "
                      + "{ChannelId} holds it in state {State}; mark it aborted first", reservationId,
                        session.SessionId, session.ChannelId, session.State);
                return;
            }

            await _feeInputSelector.ReleaseAsync(reservationId, cancellationToken);
            _liveReservations.TryRemove(reservationId, out _);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Witness>> SignAsync(ConstructedInteractiveTx transaction,
                                                        InteractiveTxContribution contribution,
                                                        IReadOnlyList<SpentOutput> otherSpentOutputs,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(otherSpentOutputs);
        cancellationToken.ThrowIfCancellationRequested();

        EnsureTransactionMatchesItsBytes(transaction);
        EnsureOutputsPresent(transaction, contribution);
        if (contribution.Inputs.Count == 0)
            return [];

        if (contribution.ReservationId is not { } reservationId)
            throw new InvalidOperationException("A contribution with inputs has no reservation");

        var inputIndexes = GetOurInputIndexes(transaction, contribution);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (storeAvailable, session) = await FindStoredSessionAsync(reservationId, cancellationToken);
            if (storeAvailable && session is null)
                throw new InvalidOperationException(
                    $"No stored negotiation holds reservation {reservationId}: store the negotiation (with our "
                  + "commitment_signed) before signing, so a restart never releases inputs whose signatures left "
                  + "(IT-ABT-01)");
            if (!storeAvailable && _logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "No interactive-tx session store: reservation {ReservationId} is kept after our signatures only "
                  + "while this process runs", reservationId);

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

            var signedTx = _transactionParser.TryParse(signed.RawTxBytes)
                        ?? throw new InvalidOperationException("The signed transaction does not parse");
            if (signedTx.TxId != transaction.TxId)
                throw new InvalidOperationException("The signer changed the transaction's txid");

            var witnesses = new List<Witness>(inputIndexes.Count);
            foreach (var index in inputIndexes)
            {
                if (signedTx.Inputs[index].Witness is not { } witness)
                    throw new InvalidOperationException($"Input {index} of our contribution was not signed");

                witnesses.Add(witness);
            }

            return witnesses;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(InteractiveTxContribution contribution,
                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (contribution.ReservationId is not { } reservationId)
            return true;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!await _feeInputSelector.ConfirmAsync(reservationId, cancellationToken))
                return false;

            _signedReservations.TryRemove(reservationId, out _);
            _liveReservations.TryRemove(reservationId, out _);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A reservation is all or nothing (<see cref="IFeeInputSelector.ReleaseAsync"/>), so every reservation that holds
    /// one of our inputs of <paramref name="discarded"/> is released whole, and only when each of its other inputs is
    /// either an input of ours in <paramref name="discarded"/> or a kept outpoint the wallet no longer holds (the
    /// confirmed transaction spent it, so releasing returns nothing for it). A reservation with any other input, or with
    /// a kept outpoint still in the wallet, may still serve a live spend: it is kept (logged). Only a reservation of the
    /// discarded attempt itself is released: one without our <c>itx:</c> purpose, one a stored unresolved negotiation of
    /// another transaction holds, or one handed out in this process to a negotiation that has not signed is kept, so
    /// calling again after a partial release never frees another spend that reserved the outputs given back (NL-492).
    /// The stored-negotiation guard of <see cref="ReleaseAsync"/> does not apply otherwise: the caller asserts the
    /// attempt can never confirm, and marks its negotiation settled (<c>ResolvedAt</c>) once nothing holds its inputs.
    /// </remarks>
    public async Task<int> ReleaseDiscardedAsync(ConstructedInteractiveTx discarded,
                                                 IReadOnlyCollection<(TxId TxId, uint Vout)> keptOutpoints,
                                                 CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discarded);
        ArgumentNullException.ThrowIfNull(keptOutpoints);

        var kept = keptOutpoints.ToHashSet();
        var ours = discarded.Inputs.Where(i => i is { AddedBy: InteractiveTxParty.Local, IsShared: false })
                            .Select(i => (i.PrevTxId, i.PrevTxVout))
                            .ToHashSet();
        var toRelease = ours.Where(o => !kept.Contains(o)).ToHashSet();
        if (toRelease.Count == 0)
            return 0;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var released = 0;
            var reservations = await _feeInputSelector.GetAllAsync(cancellationToken);
            var storedSessions = await LoadUnresolvedSessionsAsync(cancellationToken);
            foreach (var reservation in reservations)
            {
                var outpoints = reservation.Inputs.Select(i => (i.TxId, i.Index)).ToList();
                if (!outpoints.Any(toRelease.Contains))
                    continue;

                // An output a partial release already gave back may have been reserved again since: only the
                // reservation of the discarded attempt itself is released, never another spend's (NL-492)
                var otherHolder = storedSessions?.FirstOrDefault(
                    s => s.State != InteractiveTxSessionState.Aborted
                      && s.LocalContribution.ReservationId == reservation.Id
                      && s.ConstructedTx?.TxId != discarded.TxId);
                if (!reservation.Purpose.StartsWith(ReservationPurposePrefix, StringComparison.Ordinal)
                 || otherHolder is not null
                 || (_liveReservations.ContainsKey(reservation.Id) && !_signedReservations.ContainsKey(reservation.Id)))
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(
                            "Not releasing reservation {ReservationId} ({Purpose}) for discarded interactive transaction "
                          + "{TxId}: another spend or negotiation holds it", reservation.Id, reservation.Purpose,
                            discarded.TxId);
                    continue;
                }

                var blocking = outpoints.Where(o => !toRelease.Contains(o)
                                                 && !(kept.Contains(o)
                                                   && !_utxoMemoryRepository.TryGetUtxo(o.TxId, o.Index, out _)))
                                        .ToList();
                if (blocking.Count > 0)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(
                            "Not releasing reservation {ReservationId} of discarded interactive transaction {TxId}: it "
                          + "also holds {Outpoints}, which the discarded transaction does not free", reservation.Id,
                            discarded.TxId, string.Join(", ", blocking.Select(o => $"{o.TxId}:{o.Index}")));
                    continue;
                }

                var returned = outpoints.Count(o => toRelease.Contains(o)
                                                 && _utxoMemoryRepository.TryGetUtxo(o.TxId, o.Index, out _));
                await _feeInputSelector.ReleaseAsync(reservation.Id, cancellationToken);
                _signedReservations.TryRemove(reservation.Id, out _);
                _liveReservations.TryRemove(reservation.Id, out _);
                released += returned;
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "Released reservation {ReservationId} ({Purpose}) of discarded interactive transaction {TxId}: "
                      + "{Count} wallet output(s) are spendable again", reservation.Id, reservation.Purpose,
                        discarded.TxId, returned);
            }

            return released;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases every <c>itx:</c> reservation that no stored, not aborted negotiation holds and that this process did
    /// not hand out: a crash between the reservation and our <c>commitment_signed</c> (before it, BOLT 2 forgets the
    /// negotiation). Call it at startup, before negotiations start. Without a session store nothing is released (a
    /// signed negotiation cannot be told apart).
    /// </summary>
    /// <returns>The number of reservations released.</returns>
    public async Task<int> ReleaseOrphanedReservationsAsync(CancellationToken cancellationToken = default)
    {
        var reservations = await _feeInputSelector.GetAllAsync(cancellationToken);
        var released = 0;
        foreach (var reservation in reservations.Where(r => r.Purpose.StartsWith(ReservationPurposePrefix,
                                                                                StringComparison.Ordinal)))
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_liveReservations.ContainsKey(reservation.Id) || _signedReservations.ContainsKey(reservation.Id))
                    continue;

                var (storeAvailable, session) = await FindStoredSessionAsync(reservation.Id, cancellationToken);
                if (!storeAvailable)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(
                            "No interactive-tx session store: interactive-tx reservations are not swept at startup");
                    return released;
                }

                if (session is not null)
                    continue;

                await _feeInputSelector.ReleaseAsync(reservation.Id, cancellationToken);
                released++;
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation("Released orphaned interactive-tx reservation {ReservationId} ({Purpose})",
                                           reservation.Id, reservation.Purpose);
            }
            finally
            {
                _gate.Release();
            }
        }

        return released;
    }

    /// <summary>
    /// The change of a reservation under the collaborative fee rule (each input at least 107 witness weight): the
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

    /// <summary>Our inputs' weight with the minimum witness weight per input (never below BOLT 3 Appendix G's
    /// <c>max(n * 107, actual)</c>).</summary>
    private static int GetInputsWeight(FeeInputReservation reservation) =>
        reservation.Inputs.Sum(i => InputBaseWeight + Math.Max(MinimumWitnessWeight, i.InputWeight - InputBaseWeight));

    private static int GetOutputWeight(int scriptLength) =>
        (8 + (scriptLength < 0xFD ? 1 : 3) + scriptLength) * 4;

    private static long FeeSat(uint feeratePerKw, long weight) => (feeratePerKw * weight + 999) / 1000;

    private async Task ReleaseHeldAsync(List<Guid> held)
    {
        foreach (var id in held)
            await _feeInputSelector.ReleaseAsync(id, CancellationToken.None);

        held.Clear();
    }

    /// <summary>
    /// The stored, unresolved and not aborted negotiation holding <paramref name="reservationId"/>. Not available when
    /// there is no scope factory, no unit of work or the unit of work stores no sessions; any other failure is thrown
    /// (the caller then neither signs nor releases).
    /// </summary>
    private async Task<(bool StoreAvailable, InteractiveTxSessionModel? Session)> FindStoredSessionAsync(
        Guid reservationId, CancellationToken cancellationToken)
    {
        var sessions = await LoadUnresolvedSessionsAsync(cancellationToken);
        if (sessions is null)
            return (false, null);

        return (true, sessions.FirstOrDefault(s => s.State != InteractiveTxSessionState.Aborted
                                                && s.LocalContribution.ReservationId == reservationId));
    }

    /// <summary>The stored negotiations not resolved yet, or null without a session store.</summary>
    private async Task<IReadOnlyList<InteractiveTxSessionModel>?> LoadUnresolvedSessionsAsync(
        CancellationToken cancellationToken)
    {
        if (_scopeFactory is null)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _scopeFactory.CreateScope();
        if (scope.ServiceProvider.GetService<IUnitOfWork>() is not { } unitOfWork)
            return null;

        IInteractiveTxSessionDbRepository repository;
        try
        {
            repository = unitOfWork.InteractiveTxSessionDbRepository;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        return await repository.GetUnresolvedAsync();
    }

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

    /// <summary>
    /// The bytes we would sign are the transaction the metadata describes: same txid, version 2, locktime, inputs
    /// (outpoint, sequence) and outputs (amount, script) in order, no witness; the metadata in ascending
    /// <c>serial_id</c> order.
    /// </summary>
    private void EnsureTransactionMatchesItsBytes(ConstructedInteractiveTx transaction)
    {
        var parsed = _transactionParser.TryParse(transaction.UnsignedTx)
                  ?? throw new InvalidOperationException("The constructed transaction does not parse");

        if (parsed.TxId != transaction.TxId)
            throw new InvalidOperationException("The constructed transaction's bytes do not have its txid");
        if (parsed.Version != TransactionVersion)
            throw new InvalidOperationException($"The constructed transaction's version is {parsed.Version}, not 2");
        if (parsed.Locktime != transaction.Locktime)
            throw new InvalidOperationException("The constructed transaction's bytes do not have its locktime");
        if (parsed.Inputs.Count != transaction.Inputs.Count || parsed.Outputs.Count != transaction.Outputs.Count)
            throw new InvalidOperationException(
                "The constructed transaction's bytes do not have its inputs and outputs");

        for (var i = 0; i < parsed.Inputs.Count; i++)
        {
            var bytes = parsed.Inputs[i];
            var meta = transaction.Inputs[i];
            if (bytes.PrevTxId != meta.PrevTxId || bytes.PrevTxVout != meta.PrevTxVout
             || bytes.Sequence != meta.Sequence || bytes.Witness is not null
             || (i > 0 && transaction.Inputs[i - 1].SerialId >= meta.SerialId))
                throw new InvalidOperationException($"Input {i} of the constructed transaction's bytes is not input "
                                                  + $"{meta.SerialId} of the negotiation");
        }

        for (var i = 0; i < parsed.Outputs.Count; i++)
        {
            var bytes = parsed.Outputs[i];
            var meta = transaction.Outputs[i];
            if (bytes.Amount.Satoshi != meta.Amount.Satoshi || meta.Amount.MilliSatoshi % 1_000 != 0
             || !((byte[])bytes.ScriptPubKey).AsSpan().SequenceEqual((byte[])meta.ScriptPubKey)
             || (i > 0 && transaction.Outputs[i - 1].SerialId >= meta.SerialId))
                throw new InvalidOperationException($"Output {i} of the constructed transaction's bytes is not output "
                                                  + $"{meta.SerialId} of the negotiation");
        }
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