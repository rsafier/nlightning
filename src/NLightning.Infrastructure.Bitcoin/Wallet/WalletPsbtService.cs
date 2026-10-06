using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Constants;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Interfaces;
using Networks;

/// <summary>
/// The wallet's PSBT and lease surface for external spenders (<see cref="IWalletPsbtService"/>, LND's walletrpc subset,
/// NL-1184), over the persisted <see cref="IFeeInputSelector"/> reservations and the reserved-inputs-only wallet signer.
/// </summary>
/// <remarks>
/// <para>Leases: one reservation per lease id and expiration, purpose <c>lnd-lease:&lt;id hex&gt;:&lt;unix
/// expiry&gt;</c>, so a lease survives a restart and every other spend, channel funding and fee selection skips its
/// outputs. Only confirmed P2WPKH/P2TR wallet outputs that no channel funding, other reservation or pending broadcast of
/// ours holds can be leased. Expired leases are released, and leases whose outputs have all left the wallet (their spend
/// was processed in a block) are ended, by every call and by a timer every minute.</para>
/// <para>FundPsbt keeps the anchors reserve as <c>withdraw</c> does; FinalizePsbt signs a PSBT only when every input is a
/// leased wallet output (<c>SignWalletTransaction</c> refuses any wallet input not reserved, and we refuse any input not
/// leased here); PublishTransaction stores a spend of leased wallet outputs as a <see cref="BroadcastPurpose.WalletSend"/>
/// row (rebroadcast until it confirms) and sends a transaction that spends no wallet output once.</para>
/// </remarks>
public sealed class WalletPsbtService : IWalletPsbtService, IDisposable
{
    /// <summary>The purpose prefix of a lease reservation.</summary>
    public const string LeasePurposePrefix = "lnd-lease:";

    /// <summary>LND's default lease (<c>DefaultLockDuration</c>): 10 minutes.</summary>
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(10);

    /// <summary>LND's internal lock id (<c>LndInternalLockID</c>), the default id of FundPsbt's leases.</summary>
    public static readonly byte[] DefaultLockId =
        Convert.FromHexString("ede19a92ed321a4705f8a1cccc1d4f6182545d4bb4fae08bd5937831b7e38f98");

    private readonly IAnchorReserveService _anchorReserveService;
    private readonly IBitcoinChainService? _bitcoinChainService;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IFeeInputSelector _feeInputSelector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<WalletPsbtService> _logger;
    private readonly Network _network;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _sweepTimer;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public WalletPsbtService(IFeeInputSelector feeInputSelector, IAnchorReserveService anchorReserveService,
                             IUtxoMemoryRepository utxoMemoryRepository, ILightningSigner lightningSigner,
                             IBlockchainMonitor blockchainMonitor, IServiceScopeFactory scopeFactory,
                             IOptions<NodeOptions> nodeOptions, ILogger<WalletPsbtService> logger,
                             IBitcoinChainService? bitcoinChainService = null, TimeProvider? timeProvider = null)
    {
        _feeInputSelector = feeInputSelector;
        _anchorReserveService = anchorReserveService;
        _utxoMemoryRepository = utxoMemoryRepository;
        _lightningSigner = lightningSigner;
        _blockchainMonitor = blockchainMonitor;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _bitcoinChainService = bitcoinChainService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
        _sweepTimer = _timeProvider.CreateTimer(_ => _ = SweepInBackgroundAsync(), null, TimeSpan.FromMinutes(1),
                                                TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WalletUnspentOutput>> ListUnspentAsync(uint minConfirmations,
                                                                           uint maxConfirmations,
                                                                           CancellationToken cancellationToken = default)
    {
        await SweepAsync(cancellationToken);
        var pending = await GetPendingBroadcastOutpointsAsync();
        var height = _blockchainMonitor.LastProcessedBlockHeight;
        var result = new List<WalletUnspentOutput>();
        foreach (var utxo in _utxoMemoryRepository.GetUnreservedUtxos())
        {
            if (utxo.LockedToChannelId is not null || utxo.WalletAddress is null
                                                   || pending.Contains((utxo.TxId, utxo.Index)))
                continue;

            var confirmations = Confirmations(utxo.BlockHeight, height);
            if (confirmations < minConfirmations || confirmations > maxConfirmations)
                continue;

            Script script;
            try
            {
                script = BitcoinAddress.Create(utxo.WalletAddress.Address, _network).ScriptPubKey;
            }
            catch (FormatException)
            {
                continue;
            }

            result.Add(new WalletUnspentOutput(utxo.TxId, utxo.Index, utxo.Amount, utxo.AddressType,
                                               utxo.WalletAddress.Address, script.ToBytes(), confirmations));
        }

        return result.OrderBy(u => u.Confirmations).ThenBy(u => u.TxId.ToString()).ThenBy(u => u.Index).ToList();
    }

    /// <inheritdoc />
    public async Task<WalletLease> LeaseAsync(byte[] lockId, TxId txId, uint index, TimeSpan duration,
                                              CancellationToken cancellationToken = default)
    {
        CheckLockId(lockId);
        if (duration <= TimeSpan.Zero)
            duration = DefaultLeaseDuration;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SweepLockedAsync(cancellationToken);
            var expiration = Expiration(duration);
            if (_utxoMemoryRepository.TryGetFeeReservation(txId, index, out var reservationId))
            {
                // A lease of the same id is extended (LND); anything else holds the output
                var reservation = await _feeInputSelector.GetAsync(reservationId, cancellationToken);
                if (reservation is null || !TryParseLease(reservation.Purpose, out var heldId, out _)
                                        || !heldId.AsSpan().SequenceEqual(lockId))
                    throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "output already locked");

                await SplitOffAsync(reservation, txId, index, cancellationToken);
            }
            else
            {
                await CheckLeasableAsync(txId, index);
            }

            var reserved = await _feeInputSelector.ReserveInputsAsync([(txId, index)], LeasePurpose(lockId, expiration),
                                                                      cancellationToken);
            if (reserved.Count != 1)
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              "the output was taken by another spend");

            _logger.LogInformation("Leased wallet output {TxId}:{Index} until {Expiration}", txId, index, expiration);
            return ToLease(lockId, expiration, reserved[0]);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(byte[] lockId, TxId txId, uint index, CancellationToken cancellationToken = default)
    {
        CheckLockId(lockId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SweepLockedAsync(cancellationToken);
            if (!_utxoMemoryRepository.TryGetFeeReservation(txId, index, out var reservationId)
             || await _feeInputSelector.GetAsync(reservationId, cancellationToken) is not { } reservation
             || !TryParseLease(reservation.Purpose, out var heldId, out _))
                throw new WalletPsbtException(WalletPsbtError.NotFound, "output is not leased");
            if (!heldId.AsSpan().SequenceEqual(lockId))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              "output is leased with a different id");

            await SplitOffAsync(reservation, txId, index, cancellationToken);
            _logger.LogInformation("Released the lease of wallet output {TxId}:{Index}", txId, index);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WalletLease>> ListLeasesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SweepLockedAsync(cancellationToken);
            var leases = new List<WalletLease>();
            foreach (var reservation in await _feeInputSelector.GetAllAsync(cancellationToken))
            {
                if (!TryParseLease(reservation.Purpose, out var lockId, out var expiration))
                    continue;

                leases.AddRange(reservation.Inputs.Select(input => ToLease(lockId, expiration, input)));
            }

            return leases;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PsbtFundResult> FundPsbtAsync(PsbtFundRequest request,
                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckLockId(request.LockId);
        if (request.Outputs.Count == 0)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "the template has no output");
        if (request.FeeRatePerKw is < WalletSpendService.MinFeeRatePerKw or > WalletSpendService.MaxFeeRatePerKw)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument,
                                          $"the fee rate must be {WalletSpendService.MinFeeRatePerKw} to "
                                        + $"{WalletSpendService.MaxFeeRatePerKw} sat/kw");

        var outputs = new List<TxOut>();
        foreach (var (script, amount) in request.Outputs)
        {
            var scriptPubKey = new Script((byte[])script);
            if (amount.MilliSatoshi % 1_000 != 0 || amount.Satoshi < WalletSpendService.GetDustThreshold(scriptPubKey))
                throw new WalletPsbtException(WalletPsbtError.InvalidArgument,
                                              $"output of {amount.Satoshi} sat to {scriptPubKey} is dust or not whole "
                                            + "satoshis");
            outputs.Add(new TxOut(Money.Satoshis(amount.Satoshi), scriptPubKey));
        }

        if (_blockchainMonitor.IsChainProcessingHalted)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "chain processing is halted");

        var duration = request.LockDuration > TimeSpan.Zero ? request.LockDuration : DefaultLeaseDuration;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SweepLockedAsync(cancellationToken);
            var expiration = Expiration(duration);
            var purpose = LeasePurpose(request.LockId, expiration);
            var outputsSat = outputs.Sum(o => o.Value.Satoshi);
            var extraWeight = WalletSpendService.BaseWeight
                            + outputs.Sum(o => WalletSpendService.GetOutputWeight(o.ScriptPubKey));

            IReadOnlyList<WalletInput> inputs;
            Guid? selectionId = null;
            long changeSat;
            Script? changeScript;
            if (request.Inputs.Count == 0)
            {
                FeeInputReservation selection;
                try
                {
                    selection = await _feeInputSelector.ReserveAsync(LightningMoney.Satoshis(outputsSat),
                                                                     LightningMoney.Satoshis(request.FeeRatePerKw),
                                                                     extraWeight, purpose, cancellationToken);
                }
                catch (InsufficientFundsException e)
                {
                    throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, e.Message);
                }

                selectionId = selection.Id;
                inputs = selection.Inputs;
                changeSat = selection.ChangeAmount.Satoshi;
                changeScript = selection.ChangeScript is { } script ? new Script((byte[])script) : null;
            }
            else
            {
                foreach (var (txId, index) in request.Inputs)
                    await CheckLeasableAsync(txId, index);
                inputs = await _feeInputSelector.ReserveInputsAsync(request.Inputs, purpose, cancellationToken);
                if (inputs.Count != request.Inputs.Count)
                {
                    await ReleaseReservationOfAsync(inputs, cancellationToken);
                    throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                                  "an input was taken by another spend");
                }

                try
                {
                    (changeSat, changeScript) = await GetChangeAsync(inputs, outputsSat, extraWeight,
                                                                     request.FeeRatePerKw);
                }
                catch
                {
                    await ReleaseReservationOfAsync(inputs, CancellationToken.None);
                    throw;
                }
            }

            try
            {
                var height = _blockchainMonitor.LastProcessedBlockHeight;
                foreach (var input in inputs)
                {
                    if (_utxoMemoryRepository.TryGetUtxo(input.TxId, input.Index, out var utxo)
                     && Confirmations(utxo.BlockHeight, height) < Math.Max(1, request.MinConfirmations))
                        throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                                      $"not enough inputs with {request.MinConfirmations} "
                                                    + "confirmations");
                }

                await EnsureReserveKeptAsync(changeSat, cancellationToken);

                var tx = _network.CreateTransaction();
                tx.Version = (uint)request.Version;
                tx.LockTime = new LockTime(request.LockTime);
                foreach (var input in inputs)
                    tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])input.TxId), input.Index))
                    {
                        Sequence = new Sequence(0xFFFFFFFD)
                    });
                foreach (var output in outputs)
                    tx.Outputs.Add(output);

                var changeIndex = -1;
                if (changeScript is not null)
                {
                    changeIndex = tx.Outputs.Count;
                    tx.Outputs.Add(Money.Satoshis(changeSat), changeScript);
                }

                var psbt = PSBT.FromTransaction(tx, _network);
                for (var i = 0; i < inputs.Count; i++)
                    psbt.Inputs[i].WitnessUtxo = new TxOut(Money.Satoshis(inputs[i].Amount.Satoshi),
                                                           new Script((byte[])inputs[i].ScriptPubKey));

                var fee = inputs.Sum(i => i.Amount.Satoshi) - outputsSat - (changeIndex >= 0 ? changeSat : 0);
                _logger.LogInformation("Funded a PSBT of {Outputs} output(s) with {Inputs} leased wallet input(s) "
                                     + "(fee {Fee} sat, change {Change} sat)", outputs.Count, inputs.Count, fee,
                                       changeIndex >= 0 ? changeSat : 0);
                return new PsbtFundResult(psbt.ToBytes(), changeIndex,
                                          inputs.Select(i => ToLease(request.LockId, expiration, i)).ToList(),
                                          LightningMoney.Satoshis(fee));
            }
            catch
            {
                if (selectionId is { } id)
                    await _feeInputSelector.ReleaseAsync(id, CancellationToken.None);
                else
                    await ReleaseReservationOfAsync(inputs, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PsbtFinalizeResult> FinalizePsbtAsync(byte[] psbt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(psbt);
        PSBT parsed;
        try
        {
            parsed = PSBT.Load(psbt, _network);
        }
        catch (FormatException e)
        {
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, $"the PSBT does not parse: {e.Message}");
        }

        var tx = parsed.GetGlobalTransaction();
        var spent = await RequireLeasedWalletInputsAsync(tx, cancellationToken);
        if (spent.Count == 0)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "the PSBT has no input");

        var signed = new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());
        bool any;
        try
        {
            any = _lightningSigner.SignWalletTransaction(signed, []);
        }
        catch (SignerException e)
        {
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, $"cannot sign: {e.Message}");
        }

        if (!any)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "no wallet input was signed");

        var result = Transaction.Load(signed.RawTxBytes, _network);
        var validator = result.CreateValidator(spent.ToArray());
        for (var i = 0; i < result.Inputs.Count; i++)
        {
            var check = validator.ValidateInput(i);
            if (check.Error is not (null or ScriptError.OK))
                throw new InvalidOperationException($"Input {i} of {result.GetHash()} does not verify: {check.Error}");
        }

        if (result.GetHash() != tx.GetHash())
            throw new InvalidOperationException("The signer changed the transaction's id");

        for (var i = 0; i < result.Inputs.Count; i++)
        {
            parsed.Inputs[i].WitnessUtxo = spent[i];
            parsed.Inputs[i].FinalScriptWitness = result.Inputs[i].WitScript;
        }

        _logger.LogInformation("Finalized PSBT {TxId} ({Inputs} leased wallet input(s))", result.GetHash(),
                               result.Inputs.Count);
        return new PsbtFinalizeResult(parsed.ToBytes(), result.ToBytes());
    }

    /// <inheritdoc />
    public async Task<bool> PublishAsync(byte[] rawTransaction, string? label,
                                         CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawTransaction);
        Transaction tx;
        try
        {
            tx = Transaction.Load(rawTransaction, _network);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or EndOfStreamException)
        {
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, $"the transaction does not parse: {e.Message}");
        }

        var walletInputs = tx.Inputs.Count(i => _utxoMemoryRepository.TryGetUtxo(new TxId(i.PrevOut.Hash.ToBytes()),
                                                                               i.PrevOut.N, out _));
        if (walletInputs == 0)
        {
            // Not a spend of ours (a sweep to our wallet, someone else's transaction): sent once, like LND's
            // PublishTransaction of a foreign transaction, without a row the chain monitor would book or resend
            if (_bitcoinChainService is null)
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "no chain service to publish with");

            await _bitcoinChainService.SendTransactionAsync(tx);
            _logger.LogInformation("Published transaction {TxId} (no wallet input)", tx.GetHash());
            return true;
        }

        var spent = await RequireLeasedWalletInputsAsync(tx, cancellationToken);
        var feeSat = spent.Sum(o => o.Value.Satoshi) - tx.Outputs.Sum(o => o.Value.Satoshi);
        if (feeSat < 0)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "the outputs exceed the inputs");

        var weight = WalletSpendService.GetWeight(tx);
        var row = new BroadcastTransactionModel(new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes()),
                                                BroadcastPurpose.WalletSend, null,
                                                _blockchainMonitor.LastProcessedBlockHeight,
                                                (uint)(feeSat * 1000 / Math.Max(1, weight)),
                                                fee: LightningMoney.Satoshis(feeSat))
        {
            Label = string.IsNullOrWhiteSpace(label) ? null : label
        };
        var published = await _blockchainMonitor.SaveAndPublishAsync(row);
        _logger.LogInformation("Published wallet spend {TxId} (fee {Fee} sat): {Outcome}", tx.GetHash(), feeSat,
                               published ? "accepted" : "refused now, resent after every block");
        return published;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sweepTimer.Dispose();
        _gate.Dispose();
    }

    /// <summary>The lease purpose of <paramref name="lockId"/> until <paramref name="expiration"/>.</summary>
    internal static string LeasePurpose(byte[] lockId, DateTimeOffset expiration) =>
        $"{LeasePurposePrefix}{Convert.ToHexString(lockId).ToLowerInvariant()}:"
      + expiration.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a lease purpose back.</summary>
    internal static bool TryParseLease(string purpose, out byte[] lockId, out DateTimeOffset expiration)
    {
        lockId = [];
        expiration = default;
        if (!purpose.StartsWith(LeasePurposePrefix, StringComparison.Ordinal))
            return false;

        var parts = purpose[LeasePurposePrefix.Length..].Split(':');
        if (parts.Length != 2 || parts[0].Length != 64
                              || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture,
                                                out var unix))
            return false;

        try
        {
            lockId = Convert.FromHexString(parts[0]);
            expiration = DateTimeOffset.FromUnixTimeSeconds(unix);
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static uint Confirmations(uint blockHeight, uint tipHeight) =>
        blockHeight == 0 || blockHeight > tipHeight ? 0 : tipHeight - blockHeight + 1;

    private static void CheckLockId(byte[] lockId)
    {
        if (lockId is not { Length: 32 })
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "the lease id must be 32 bytes");
    }

    private static WalletLease ToLease(byte[] lockId, DateTimeOffset expiration, WalletInput input) =>
        new(lockId, input.TxId, input.Index, expiration, input.Amount, input.ScriptPubKey);

    private DateTimeOffset Expiration(TimeSpan duration) =>
        DateTimeOffset.FromUnixTimeSeconds((_timeProvider.GetUtcNow() + duration).ToUnixTimeSeconds());

    /// <summary>A wallet output a new lease may take: held, mined, P2WPKH/P2TR, free, not spent by our broadcasts.</summary>
    private async Task CheckLeasableAsync(TxId txId, uint index)
    {
        if (!_utxoMemoryRepository.TryGetUtxo(txId, index, out var utxo))
            throw new WalletPsbtException(WalletPsbtError.NotFound, $"{txId}:{index} is not a wallet output");
        if (utxo.LockedToChannelId is not null)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                          $"{txId}:{index} is locked to a channel funding");
        if (_utxoMemoryRepository.TryGetFeeReservation(txId, index, out _))
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "output already locked");
        if (utxo.BlockHeight == 0)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                          $"{txId}:{index} is unconfirmed; only confirmed outputs can be leased");
        if (utxo.AddressType is not (AddressType.P2Wpkh or AddressType.P2Tr))
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, $"{txId}:{index} cannot be signed");
        if ((await GetPendingBroadcastOutpointsAsync()).Contains((txId, index)))
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                          $"{txId}:{index} is spent by a pending transaction of this node");
    }

    /// <summary>
    /// Ends the lease of one input of <paramref name="reservation"/>: the reservation is released and its other inputs
    /// leased again under the same purpose.
    /// </summary>
    private async Task SplitOffAsync(FeeInputReservation reservation, TxId txId, uint index,
                                     CancellationToken cancellationToken)
    {
        await _feeInputSelector.ReleaseAsync(reservation.Id, cancellationToken);
        var rest = reservation.Inputs.Where(i => i.TxId != txId || i.Index != index)
                              .Select(i => (i.TxId, i.Index)).ToList();
        if (rest.Count == 0)
            return;

        var again = await _feeInputSelector.ReserveInputsAsync(rest, reservation.Purpose, cancellationToken);
        if (again.Count != rest.Count)
            _logger.LogWarning("{Lost} leased output(s) of {Purpose} were taken while one lease of the group ended",
                               rest.Count - again.Count, reservation.Purpose);
    }

    /// <summary>The spent outputs of <paramref name="tx"/>, every one of which must be a leased wallet output.</summary>
    private async Task<List<TxOut>> RequireLeasedWalletInputsAsync(Transaction tx, CancellationToken cancellationToken)
    {
        var spent = new List<TxOut>();
        var purposes = new Dictionary<Guid, string?>();
        foreach (var input in tx.Inputs)
        {
            var txId = new TxId(input.PrevOut.Hash.ToBytes());
            if (!_utxoMemoryRepository.TryGetUtxo(txId, input.PrevOut.N, out var utxo) || utxo.WalletAddress is null)
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              $"input {input.PrevOut} is not a wallet output");
            if (!_utxoMemoryRepository.TryGetFeeReservation(txId, input.PrevOut.N, out var reservationId))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              $"input {input.PrevOut} is not leased");
            if (!purposes.TryGetValue(reservationId, out var purpose))
            {
                purpose = (await _feeInputSelector.GetAsync(reservationId, cancellationToken))?.Purpose;
                purposes[reservationId] = purpose;
            }

            if (purpose is null || !purpose.StartsWith(LeasePurposePrefix, StringComparison.Ordinal))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              $"input {input.PrevOut} is reserved for another spend of this node");

            spent.Add(new TxOut(Money.Satoshis(utxo.Amount.Satoshi),
                                BitcoinAddress.Create(utxo.WalletAddress.Address, _network).ScriptPubKey));
        }

        return spent;
    }

    /// <summary>The change of a spend of the given inputs: a fresh P2WPKH change address when it is not dust.</summary>
    private async Task<(long ChangeSat, Script? ChangeScript)> GetChangeAsync(IReadOnlyList<WalletInput> inputs,
                                                                             long outputsSat, int extraWeight,
                                                                             long feeRatePerKw)
    {
        var totalSat = inputs.Sum(i => i.Amount.Satoshi);
        var weight = extraWeight + inputs.Sum(i => i.InputWeight);
        var feeWithChange = FeeSat(feeRatePerKw, weight + WalletWeights.P2WpkhOutputWeight);
        if (totalSat - outputsSat - feeWithChange >= WalletWeights.P2WpkhDustLimitSat)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var wallet = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
            var address = await wallet.GetUnusedAddressAsync(AddressType.P2Wpkh, true);
            return (totalSat - outputsSat - feeWithChange,
                    BitcoinAddress.Create(address.Address, _network).ScriptPubKey);
        }

        if (totalSat - outputsSat < FeeSat(feeRatePerKw, weight))
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                          $"the inputs ({totalSat} sat) do not cover the outputs ({outputsSat} sat) and "
                                        + $"the fee ({FeeSat(feeRatePerKw, weight)} sat)");

        return (0, null);
    }

    /// <summary>Withdraw's anchors reserve rule: the free confirmed outputs left plus the change must cover it.</summary>
    private async Task EnsureReserveKeptAsync(long changeSat, CancellationToken cancellationToken)
    {
        var status = await _anchorReserveService.GetStatusAsync(cancellationToken);
        if (status.RequiredReserve.IsZero)
            return;

        var keptSat = status.AvailableBalance.Satoshi + changeSat;
        if (keptSat < status.RequiredReserve.Satoshi)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                          $"the spend would leave {keptSat} sat in the wallet, below the anchors "
                                        + $"reserve of {status.RequiredReserve.Satoshi} sat");
    }

    private async Task ReleaseReservationOfAsync(IReadOnlyList<WalletInput> inputs,
                                                 CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        foreach (var input in inputs)
        {
            if (_utxoMemoryRepository.TryGetFeeReservation(input.TxId, input.Index, out var id))
                ids.Add(id);
        }

        foreach (var id in ids)
            await _feeInputSelector.ReleaseAsync(id, cancellationToken);
    }

    private async Task<HashSet<(TxId TxId, uint Index)>> GetPendingBroadcastOutpointsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await PendingBroadcastOutpoints.GetAsync(uow, _network, _logger);
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SweepLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends the leases whose outputs have all been spent in a block and releases the expired ones.</summary>
    private async Task SweepLockedAsync(CancellationToken cancellationToken)
    {
        // Before the chain monitor loaded the wallet every output looks spent: nothing to decide yet
        if (_blockchainMonitor.LastProcessedBlockHeight == 0)
            return;

        var now = _timeProvider.GetUtcNow();
        foreach (var reservation in await _feeInputSelector.GetAllAsync(cancellationToken))
        {
            if (!TryParseLease(reservation.Purpose, out _, out var expiration))
                continue;

            if (reservation.Inputs.All(i => !_utxoMemoryRepository.TryGetUtxo(i.TxId, i.Index, out _)))
            {
                await _feeInputSelector.ConfirmAsync(reservation.Id, cancellationToken);
                continue;
            }

            if (expiration > now)
                continue;

            await _feeInputSelector.ReleaseAsync(reservation.Id, cancellationToken);
            _logger.LogInformation("Lease {Purpose} of {Count} wallet output(s) expired", reservation.Purpose,
                                   reservation.Inputs.Count);
        }
    }

    private async Task SweepInBackgroundAsync()
    {
        try
        {
            await SweepAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // Stopping
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not sweep the expired wallet leases");
        }
    }

    private static long FeeSat(long feeRatePerKw, long weight) => (feeRatePerKw * weight + 999) / 1000;
}