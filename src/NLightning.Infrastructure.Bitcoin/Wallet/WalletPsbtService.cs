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
using Domain.Protocol.Interfaces;
using Interfaces;
using Networks;

/// <summary>
/// The wallet's PSBT and lease surface for external spenders (<see cref="IWalletPsbtService"/>, LND's walletrpc subset,
/// NL-1184), over the persisted <see cref="IFeeInputSelector"/> reservations and the reserved-inputs-only wallet signer.
/// </summary>
/// <remarks>
/// <para>Leases: one reservation per lease id and expiration, purpose <c>lnd-lease:&lt;id hex&gt;:&lt;unix
/// expiry&gt;</c>, so a lease survives a restart and every other spend, channel funding and fee selection skips its
/// outputs. Confirmed or explicitly revalidated mempool P2WPKH/P2TR wallet outputs that no channel funding, other reservation or pending broadcast of
/// ours holds can be leased. Expired leases are released, and leases whose outputs have all left the wallet (their spend
/// was processed in a block) are ended, by every call and by a timer every minute.</para>
/// <para>FundPsbt keeps the anchors reserve as <c>withdraw</c> does, with a P2WPKH or P2TR change and LND's
/// <c>max_fee_ratio</c> (NL-1186). SignPsbt and FinalizePsbt sign only wallet inputs leased here
/// (<c>SignWalletTransaction</c> refuses any wallet input not reserved, and we refuse any reserved for another spend or
/// locked to a channel funding); other parties' inputs are never touched: SignPsbt leaves them as they are, FinalizePsbt
/// needs them finalized with their UTXO (we are the last signer) and verifies the whole transaction (NL-1186).
/// PublishTransaction stores a spend of leased wallet outputs as a <see cref="BroadcastPurpose.WalletSend"/> row, one
/// that also spends others' outputs as a <see cref="BroadcastPurpose.WalletCollaborative"/> row (both rebroadcast until
/// they confirm), and sends a transaction that spends no wallet output once.</para>
/// </remarks>
public sealed partial class WalletPsbtService : IWalletPsbtService, IDisposable
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
    private readonly ISecureKeyManager? _secureKeyManager;
    private readonly IWalletMempoolCatalog? _mempoolCatalog;

    public WalletPsbtService(IFeeInputSelector feeInputSelector, IAnchorReserveService anchorReserveService,
                             IUtxoMemoryRepository utxoMemoryRepository, ILightningSigner lightningSigner,
                             IBlockchainMonitor blockchainMonitor, IServiceScopeFactory scopeFactory,
                             IOptions<NodeOptions> nodeOptions, ILogger<WalletPsbtService> logger,
                             IBitcoinChainService? bitcoinChainService = null, TimeProvider? timeProvider = null,
                             ISecureKeyManager? secureKeyManager = null, IWalletMempoolCatalog? mempoolCatalog = null)
    {
        _secureKeyManager = secureKeyManager;
        _mempoolCatalog = mempoolCatalog;
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
        if (minConfirmations == 0 && _mempoolCatalog is not null)
            await _mempoolCatalog.RefreshAsync(cancellationToken);
        await SweepAsync(cancellationToken);
        var pending = await GetPendingBroadcastOutpointsAsync();
        var height = _blockchainMonitor.LastProcessedBlockHeight;
        var result = new List<WalletUnspentOutput>();
        foreach (var utxo in _utxoMemoryRepository.GetUnreservedUtxos().Concat(minConfirmations == 0
                     ? _utxoMemoryRepository.GetUnconfirmedUtxos() : Array.Empty<UtxoModel>()))
        {
            if (_utxoMemoryRepository.TryGetFeeReservation(utxo.TxId, utxo.Index, out _) || utxo.LockedToChannelId is not null || (utxo.WalletAddress is null && utxo.SilentPayment is null)
                                                   || pending.Contains((utxo.TxId, utxo.Index)))
                continue;

            var confirmations = Confirmations(utxo.BlockHeight, height);
            if (confirmations < minConfirmations || confirmations > maxConfirmations)
                continue;

            Script script;
            try
            {
                script = WalletUtxoScript(utxo);
            }
            catch (FormatException)
            {
                continue;
            }

            result.Add(new WalletUnspentOutput(utxo.TxId, utxo.Index, utxo.Amount, utxo.AddressType,
                                               script.GetDestinationAddress(_network)?.ToString() ?? string.Empty, script.ToBytes(), confirmations, utxo.WalletAddress?.AccountName ?? "default"));
        }

        return result.OrderBy(u => u.Confirmations).ThenBy(u => u.TxId.ToString()).ThenBy(u => u.Index).ToList();
    }

    /// <inheritdoc />
    public async Task<WalletLease> LeaseAsync(byte[] lockId, TxId txId, uint index, TimeSpan duration,
                                              CancellationToken cancellationToken = default)
        => await LeaseAsync(lockId, txId, index, duration, 0, cancellationToken);

    public async Task<WalletLease> LeaseAsync(byte[] lockId, TxId txId, uint index, TimeSpan duration,
                                             uint releaseAfterSpendConfs, CancellationToken cancellationToken = default)
    {
        CheckLockId(lockId);
        if (duration <= TimeSpan.Zero)
            duration = DefaultLeaseDuration;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_mempoolCatalog is not null) await _mempoolCatalog.RefreshParentsAsync([txId], cancellationToken);
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
                await CheckLeasableAsync(txId, index, true);
            }

            var reserved = await _feeInputSelector.ReserveInputsAsync([(txId, index)], LeasePurpose(lockId, expiration, releaseAfterSpendConfs),
                                                                      _utxoMemoryRepository.TryGetUtxo(txId, index, out var leaseCoin) && leaseCoin.BlockHeight == 0,
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
        if (request.CoinSelectTemplate)
            return await FundTemplateAsync(request, cancellationToken);
        CheckLockId(request.LockId);
        if (request.Outputs.Count == 0)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "the template has no output");
        if (request.ChangeAddressType is not (AddressType.P2Wpkh or AddressType.P2Tr))
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "the change must be P2WPKH or P2TR");
        if (double.IsNaN(request.MaxFeeRatio) || request.MaxFeeRatio is < 0 or > 1)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument,
                                          $"max fee ratio {request.MaxFeeRatio} must be between 0 and 1");
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
            var purpose = LeasePurpose(request.LockId, expiration, request.ReleaseAfterSpendConfs);
            if (request.SpendUnconfirmed && request.MinConfirmations > 0)
                throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "spend_unconfirmed requires min_confs=0");
            if (request.SpendUnconfirmed)
                await (_mempoolCatalog ?? throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                    "unconfirmed wallet catalogue unavailable")).RefreshParentsAsync(request.Inputs.Select(point => point.TxId).Distinct().ToArray(), cancellationToken);
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
                                                                     extraWeight, purpose, WalletSelectionPolicy.Default with
                                                                     {
                                                                         IncludeUnconfirmed = request.SpendUnconfirmed,
                                                                         Account = request.Account,
                                                                         ConfirmationTip = _blockchainMonitor.LastProcessedBlockHeight,
                                                                         MinConfirmations = (uint)(request.SpendUnconfirmed ? Math.Max(0, request.MinConfirmations) : Math.Max(1, request.MinConfirmations))
                                                                     }, cancellationToken);
                }
                catch (InsufficientFundsException e)
                {
                    throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, e.Message);
                }

                selectionId = selection.Id;
                inputs = selection.Inputs;
                changeSat = selection.ChangeAmount.Satoshi;
                changeScript = selection.ChangeScript is { } script ? new Script((byte[])script) : null;
                if (changeScript is not null && request.ChangeAddressType == AddressType.P2Tr)
                {
                    try
                    {
                        // The selector priced a P2WPKH change: the larger P2TR output pays its extra weight (NL-1186)
                        changeSat -= FeeSat(request.FeeRatePerKw, ChangeOutputWeight(AddressType.P2Tr)
                                                                - WalletWeights.P2WpkhOutputWeight);
                        changeScript = changeSat >= ChangeDustLimit(AddressType.P2Tr)
                                           ? await NewChangeScriptAsync(AddressType.P2Tr, request.Account)
                                           : null;
                        if (changeScript is null)
                            changeSat = 0;
                    }
                    catch
                    {
                        await _feeInputSelector.ReleaseAsync(selection.Id, CancellationToken.None);
                        throw;
                    }
                }
            }
            else
            {
                foreach (var (txId, index) in request.Inputs)
                    await CheckLeasableAsync(txId, index, request.SpendUnconfirmed, request.Account);
                inputs = await _feeInputSelector.ReserveInputsAsync(request.Inputs, purpose, request.SpendUnconfirmed, cancellationToken);
                if (inputs.Count != request.Inputs.Count)
                {
                    await ReleaseReservationOfAsync(inputs, cancellationToken);
                    throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                                  "an input was taken by another spend");
                }

                try
                {
                    (changeSat, changeScript) = await GetChangeAsync(inputs, outputsSat, extraWeight,
                                                                     request.FeeRatePerKw,
                                                                     request.ChangeAddressType, request.Account);
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
                     && Confirmations(utxo.BlockHeight, height) < (request.SpendUnconfirmed ? Math.Max(0, request.MinConfirmations) : Math.Max(1, request.MinConfirmations)))
                        throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                                      $"not enough inputs with {request.MinConfirmations} "
                                                    + "confirmations");
                }

                await EnsureReserveKeptAsync(inputs.Any(input => _utxoMemoryRepository.TryGetUtxo(input.TxId, input.Index,
                    out var coin) && coin.BlockHeight == 0) ? 0 : changeSat, cancellationToken);

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
                {
                    psbt.Inputs[i].WitnessUtxo = new TxOut(Money.Satoshis(inputs[i].Amount.Satoshi),
                                                           new Script((byte[])inputs[i].ScriptPubKey));
                    if (_utxoMemoryRepository.TryGetUtxo(inputs[i].TxId, inputs[i].Index, out var walletUtxo))
                        AddKeyInfo(psbt.Inputs[i], walletUtxo);
                }

                var fee = inputs.Sum(i => i.Amount.Satoshi) - outputsSat - (changeIndex >= 0 ? changeSat : 0);
                CheckFeeRatio(fee, outputsSat, request.MaxFeeRatio);
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
        var parsed = LoadPsbt(psbt);
        var tx = parsed.GetGlobalTransaction();
        if (tx.Inputs.Count == 0)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "the PSBT has no input");

        var inputs = await ClassifyInputsAsync(tx, parsed, cancellationToken);
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            if (inputs.Ours[i])
                continue;

            // LND: we must be the last signer; another party's input is complete already
            if (inputs.Spent[i] is null)
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              $"input {i} ({tx.Inputs[i].PrevOut}) is not a wallet output and has no "
                                            + "UTXO information");
            if (!IsFinalized(parsed.Inputs[i]))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              $"input {i} ({tx.Inputs[i].PrevOut}) is not a wallet output and is not "
                                            + "finalized");
        }

        var signed = SignWalletInputs(tx, inputs);
        var final = tx.Clone();
        for (var i = 0; i < final.Inputs.Count; i++)
        {
            if (inputs.Ours[i])
            {
                final.Inputs[i].WitScript = signed.Inputs[i].WitScript;
                continue;
            }

            final.Inputs[i].ScriptSig = parsed.Inputs[i].FinalScriptSig ?? Script.Empty;
            final.Inputs[i].WitScript = parsed.Inputs[i].FinalScriptWitness ?? WitScript.Empty;
        }

        var spent = inputs.Spent.Select(o => o!).ToArray();
        var validator = final.CreateValidator(spent);
        for (var i = 0; i < final.Inputs.Count; i++)
        {
            var check = validator.ValidateInput(i);
            if (check.Error is not (null or ScriptError.OK))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              $"input {i} of {final.GetHash()} does not verify: {check.Error}");
        }

        if (final.GetHash() != tx.GetHash())
            throw new InvalidOperationException("The signer changed the transaction's id");

        for (var i = 0; i < final.Inputs.Count; i++)
        {
            if (!inputs.Ours[i])
                continue;

            parsed.Inputs[i].WitnessUtxo = spent[i];
            parsed.Inputs[i].PartialSigs.Clear();
            parsed.Inputs[i].FinalScriptWitness = final.Inputs[i].WitScript;
        }

        _logger.LogInformation("Finalized PSBT {TxId} ({Ours} leased wallet input(s), {Others} other input(s))",
                               final.GetHash(), inputs.Ours.Count(o => o), inputs.Ours.Count(o => !o));
        return new PsbtFinalizeResult(parsed.ToBytes(), final.ToBytes());
    }

    /// <inheritdoc />
    public async Task<PsbtSignResult> SignPsbtAsync(byte[] psbt, CancellationToken cancellationToken = default)
    {
        var parsed = LoadPsbt(psbt);
        var tx = parsed.GetGlobalTransaction();
        if (tx.Inputs.Count == 0)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "the PSBT has no input");

        var inputs = await ClassifyInputsAsync(tx, parsed, cancellationToken);
        var toSign = Enumerable.Range(0, tx.Inputs.Count)
                               .Where(i => inputs.Ours[i] && !IsFinalized(parsed.Inputs[i]))
                               .ToList();
        if (toSign.Count == 0)
            return new PsbtSignResult(parsed.ToBytes(), []);

        var signed = SignWalletInputs(tx, inputs);
        var signedInputs = new List<uint>();
        foreach (var i in toSign)
        {
            var pushes = signed.Inputs[i].WitScript.Pushes.ToArray();
            var input = parsed.Inputs[i];
            input.WitnessUtxo = inputs.Spent[i];
            var prevOut = tx.Inputs[i].PrevOut;
            if (_utxoMemoryRepository.TryGetUtxo(new TxId(prevOut.Hash.ToBytes()), prevOut.N, out var walletUtxo))
                AddKeyInfo(input, walletUtxo);
            if (inputs.Spent[i]!.ScriptPubKey.IsScriptType(ScriptType.Taproot))
            {
                if (pushes.Length != 1 || !TaprootSignature.TryParse(pushes[0], out var taprootSignature))
                    throw new InvalidOperationException($"The signer gave input {i} an unexpected P2TR witness");

                input.TaprootKeySignature = taprootSignature;
            }
            else
            {
                if (pushes.Length != 2)
                    throw new InvalidOperationException($"The signer gave input {i} an unexpected P2WPKH witness");

                input.PartialSigs[new PubKey(pushes[1])] = new TransactionSignature(pushes[0]);
            }

            signedInputs.Add((uint)i);
        }

        _logger.LogInformation("Signed {Count} leased wallet input(s) of PSBT {TxId}", signedInputs.Count,
                               tx.GetHash());
        return new PsbtSignResult(parsed.ToBytes(), signedInputs);
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
        catch (Exception e) when (e is FormatException or ArgumentException or EndOfStreamException or InvalidDataException)
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

            // NL-1236: LND clients (loopd) publish their sweeps again every block until they confirm; bitcoind's
            // "already known" answer is still a success, but not a fresh broadcast
            if (await SendOrRefuseAsync(tx))
                _logger.LogInformation("Published transaction {TxId} (no wallet input)", tx.GetHash());
            else
                _logger.LogDebug("Transaction {TxId} (no wallet input) is already known to bitcoind", tx.GetHash());
            return true;
        }

        var inputs = await ClassifyInputsAsync(tx, null, cancellationToken);
        var collaborative = inputs.Ours.Any(o => !o);
        long? feeSat = null;
        if (!collaborative)
        {
            feeSat = inputs.Spent.Sum(o => o!.Value.Satoshi) - tx.Outputs.Sum(o => o.Value.Satoshi);
            if (feeSat < 0)
                throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "the outputs exceed the inputs");
        }

        // LND (btcwallet PublishTransaction): a transaction bitcoind refuses is an error and nothing is kept. So it is sent
        // first; only an accepted (or already known) one gets the row that rebroadcasts it until it confirms
        if (_bitcoinChainService is not null)
            await SendOrRefuseAsync(tx);

        // A collaborative transaction (others' inputs too, NL-1186): its fee and what it pays away are not ours alone, so
        // it is no withdrawal; the chain monitor books its wallet movements only
        var weight = WalletSpendService.GetWeight(tx);
        var row = new BroadcastTransactionModel(new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes()),
                                                collaborative
                                                    ? BroadcastPurpose.WalletCollaborative
                                                    : BroadcastPurpose.WalletSend, null,
                                                _blockchainMonitor.LastProcessedBlockHeight,
                                                feeSat is { } fee ? (uint)(fee * 1000 / Math.Max(1, weight)) : 0,
                                                fee: feeSat is { } known ? LightningMoney.Satoshis(known) : null)
        {
            Label = string.IsNullOrWhiteSpace(label) ? null : label
        };
        var published = await _blockchainMonitor.SaveAndPublishAsync(row);
        _logger.LogInformation("Published {Kind} {TxId} (fee {Fee}): {Outcome}",
                               collaborative ? "collaborative wallet transaction" : "wallet spend", tx.GetHash(),
                               feeSat is { } paid ? $"{paid} sat" : "shared",
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
    internal static string LeasePurpose(byte[] lockId, DateTimeOffset expiration, uint releaseAfterSpendConfs = 0) =>
        $"{LeasePurposePrefix}{Convert.ToHexString(lockId).ToLowerInvariant()}:"
      + expiration.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
      + (releaseAfterSpendConfs == 0 ? "" : $":{releaseAfterSpendConfs}");

    /// <summary>Reads a lease purpose back.</summary>
    internal static bool TryParseLease(string purpose, out byte[] lockId, out DateTimeOffset expiration)
    {
        lockId = [];
        expiration = default;
        if (!purpose.StartsWith(LeasePurposePrefix, StringComparison.Ordinal))
            return false;

        var parts = purpose[LeasePurposePrefix.Length..].Split(':');
        if (parts.Length is not (2 or 3) || (parts.Length == 3 && !uint.TryParse(parts[2], out _)) || parts[0].Length != 64
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

    /// <summary>
    /// Sends <paramref name="tx"/>; bitcoind's refusal becomes LND's error (<see cref="MapRefusal"/>), an "already known"
    /// answer counts as published (LND ignores those too). True when bitcoind took it now, false when it already had it
    /// (mempool or chain).
    /// </summary>
    private async Task<bool> SendOrRefuseAsync(Transaction tx)
    {
        try
        {
            await _bitcoinChainService!.SendTransactionAsync(tx);
            return true;
        }
        catch (NBitcoin.RPC.RPCException e)
        {
            if (MapRefusal(e.Message) is { } error)
            {
                _logger.LogWarning("bitcoind refused {TxId}: {Reason}", tx.GetHash(), e.Message);
                throw new WalletPsbtException(WalletPsbtError.PublishRefused, error);
            }

            return false;
        }
    }

    /// <summary>
    /// LND's reading of bitcoind's reject reason (<c>lnwallet/btcwallet</c> <c>mapRpcclientError</c> over btcwallet's
    /// <c>chain</c> errors): null when the transaction is already in the mempool or the chain (a success), else the error
    /// text LND returns.
    /// </summary>
    internal static string? MapRefusal(string reason)
    {
        var text = reason.ToLowerInvariant();
        if (text.Contains("txn-already-in-mempool") || text.Contains("txn-already-known")
         || text.Contains("already in block chain") || text.Contains("outputs already in utxo set"))
            return null;
        if (text.Contains("txn-mempool-conflict") || text.Contains("missingorspent") || text.Contains("missing-inputs")
         || text.Contains("missing inputs"))
            return "transaction rejected: output already spent";
        if (text.Contains("min relay fee not met") || text.Contains("mempool min fee not met"))
            return $"transaction rejected by the mempool because of low fees: {reason}";
        if (text.Contains("insufficient fee"))
            return "insufficient fee";
        if (text.Contains("txn-same-nonwitness-data-in-mempool"))
            return "txn same nonwitness data in mempool";
        return reason;
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
    private async Task CheckLeasableAsync(TxId txId, uint index, bool includeUnconfirmed = false, string? account = null)
    {
        if (!_utxoMemoryRepository.TryGetUtxo(txId, index, out var utxo))
            throw new WalletPsbtException(WalletPsbtError.NotFound, $"{txId}:{index} is not a wallet output");
        if (utxo.LockedToChannelId is not null)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                          $"{txId}:{index} is locked to a channel funding");
        if (_utxoMemoryRepository.TryGetFeeReservation(txId, index, out _))
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "output already locked");
        if (account is not null && (utxo.WalletAddress?.AccountName ?? "default") != account)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "input belongs to another wallet account");
        if (utxo.BlockHeight == 0 && (!includeUnconfirmed || !_utxoMemoryRepository.GetUnconfirmedUtxos()
                .Any(coin => coin.TxId == txId && coin.Index == index)))
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

        var again = await _feeInputSelector.ReserveInputsAsync(rest, reservation.Purpose,
            rest.Any(point => _utxoMemoryRepository.TryGetUtxo(point.TxId, point.Index, out var coin) && coin.BlockHeight == 0), cancellationToken);
        if (again.Count != rest.Count)
            _logger.LogWarning("{Lost} leased output(s) of {Purpose} were taken while one lease of the group ended",
                               rest.Count - again.Count, reservation.Purpose);
    }

    private Script WalletUtxoScript(UtxoModel utxo)
    {
        if (utxo.SilentPayment is { } silentPayment)
            return new Script([0x51, 0x20, .. silentPayment.OutputKey]);
        return BitcoinAddress.Create(utxo.WalletAddress!.Address, _network).ScriptPubKey;
    }

    /// <summary>
    /// Each input of <paramref name="tx"/>: a wallet output (which must be leased here: never a channel funding's, never
    /// one reserved for another spend of this node) or another party's, whose spent output comes from the PSBT's UTXO
    /// fields when there is a PSBT.
    /// </summary>
    private async Task<ClassifiedInputs> ClassifyInputsAsync(Transaction tx, PSBT? psbt,
                                                             CancellationToken cancellationToken)
    {
        if (_mempoolCatalog is not null && tx.Inputs.Any(input =>
            _utxoMemoryRepository.TryGetFeeReservation(new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N, out _) &&
            (!_utxoMemoryRepository.TryGetUtxo(new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N, out var coin) || coin.BlockHeight == 0)))
            await _mempoolCatalog.RefreshParentsAsync(tx.Inputs.Select(input => new TxId(input.PrevOut.Hash.ToBytes())).Distinct().ToArray(), cancellationToken);
        var spent = new TxOut?[tx.Inputs.Count];
        var ours = new bool[tx.Inputs.Count];
        var purposes = new Dictionary<Guid, string?>();
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            var prevOut = tx.Inputs[i].PrevOut;
            var txId = new TxId(prevOut.Hash.ToBytes());
            if (!_utxoMemoryRepository.TryGetUtxo(txId, prevOut.N, out var utxo))
            {
                if (_utxoMemoryRepository.TryGetFeeReservation(txId, prevOut.N, out _))
                    throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "leased input is no longer available on the current chain/mempool");
                spent[i] = psbt is null ? null : ForeignSpentOutput(psbt.Inputs[i], prevOut);
                continue;
            }

            if (utxo.LockedToChannelId is not null)
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              $"input {prevOut} is locked to a channel funding");
            if (utxo.WalletAddress is null && utxo.SilentPayment is null)
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              $"input {prevOut} is not a wallet output");
            if (!_utxoMemoryRepository.TryGetFeeReservation(txId, prevOut.N, out var reservationId))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              $"input {prevOut} is not leased");
            if (!purposes.TryGetValue(reservationId, out var purpose))
            {
                purpose = (await _feeInputSelector.GetAsync(reservationId, cancellationToken))?.Purpose;
                purposes[reservationId] = purpose;
            }

            if (purpose is null || !purpose.StartsWith(LeasePurposePrefix, StringComparison.Ordinal))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                              $"input {prevOut} is reserved for another spend of this node");

            spent[i] = new TxOut(Money.Satoshis(utxo.Amount.Satoshi), WalletUtxoScript(utxo));
            ours[i] = true;
        }

        return new ClassifiedInputs(spent, ours);
    }

    /// <summary>
    /// The signed copy of <paramref name="tx"/> with every wallet input signed (the others untouched, their spent outputs
    /// given to the signer for BIP 341), or <paramref name="tx"/> itself when it has no wallet input.
    /// </summary>
    private Transaction SignWalletInputs(Transaction tx, ClassifiedInputs inputs)
    {
        if (!inputs.Ours.Any(o => o))
            return tx;

        var others = new List<SpentOutput>();
        for (var i = 0; i < tx.Inputs.Count; i++)
        {
            if (inputs.Ours[i] || inputs.Spent[i] is not { } output)
                continue;

            var prevOut = tx.Inputs[i].PrevOut;
            others.Add(new SpentOutput(new TxId(prevOut.Hash.ToBytes()), prevOut.N,
                                       LightningMoney.Satoshis(output.Value.Satoshi), output.ScriptPubKey.ToBytes()));
        }

        var signed = new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());
        bool any;
        try
        {
            any = _lightningSigner.SignWalletTransaction(signed, others);
        }
        catch (SignerException e)
        {
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, $"cannot sign: {e.Message}");
        }

        if (!any)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "no wallet input was signed");

        var result = Transaction.Load(signed.RawTxBytes, _network);
        if (result.GetHash() != tx.GetHash())
            throw new InvalidOperationException("The signer changed the transaction's id");
        return result;
    }

    /// <summary>The output another party's PSBT input spends: its witness UTXO, else its previous transaction's.</summary>
    private static TxOut? ForeignSpentOutput(PSBTInput input, OutPoint prevOut)
    {
        if (input.WitnessUtxo is { } witnessUtxo)
            return witnessUtxo;

        return input.NonWitnessUtxo is { } previous && previous.GetHash() == prevOut.Hash
                                                    && prevOut.N < previous.Outputs.Count
                   ? previous.Outputs[(int)prevOut.N]
                   : null;
    }

    /// <summary>
    /// The public key data of a wallet input, as LND adds it (BIP 174): the derivation path from the master key's
    /// fingerprint and, for P2TR, the internal key a finalizer needs for the key path. Nothing without the key manager.
    /// </summary>
    private void AddKeyInfo(PSBTInput input, UtxoModel utxo)
    {
        // A BIP352 output is a raw output key, not an ordinary BIP86 account derivation. Its default address index
        // and change flag must never produce a fictitious HD path or an extra TapTweak internal key in the PSBT.
        if (utxo.SilentPayment is not null || utxo.WalletAddress is null)
            return;
        var accountIndex = utxo.WalletAddress.AccountIndex;
        var accountInfo = accountIndex == 0 ? _secureKeyManager?.GetDepositAccount(utxo.AddressType)
            : _secureKeyManager?.GetDepositAccount(utxo.AddressType, accountIndex);
        if (accountInfo is not { } account)
            return;

        try
        {
            var branch = utxo.IsAddressChange ? 1u : 0u;
            var pubKey = ExtPubKey.Parse(account.ExtendedPublicKey, _network).Derive(branch).Derive(utxo.WalletAddress.DerivationIndex ?? utxo.AddressIndex)
                                  .PubKey;
            var path = new RootedKeyPath(new HDFingerprint(account.MasterFingerprint),
                                         KeyPath.Parse($"{account.DerivationPath}/{branch}/{utxo.WalletAddress.DerivationIndex ?? utxo.AddressIndex}"));
            if (utxo.AddressType == AddressType.P2Tr)
            {
                input.TaprootInternalKey = pubKey.TaprootInternalKey;
                input.HDTaprootKeyPaths[new TaprootPubKey(pubKey.TaprootInternalKey.ToBytes())] =
                    new TaprootKeyPath(path);
            }
            else
            {
                input.AddKeyPath(pubKey, path);
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            _logger.LogDebug(e, "No key path for wallet output {TxId}:{Index}", utxo.TxId, utxo.Index);
        }
    }

    private static bool IsFinalized(PSBTInput input) => input.FinalScriptWitness is not null
                                                     || input.FinalScriptSig is not null;

    private PSBT LoadPsbt(byte[] psbt)
    {
        ArgumentNullException.ThrowIfNull(psbt);
        try
        {
            return PSBT.Load(psbt, _network);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or EndOfStreamException or InvalidDataException)
        {
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, $"the PSBT does not parse: {e.Message}");
        }
    }

    /// <summary>
    /// The change of a spend of the given inputs: a fresh change address of <paramref name="changeType"/> when it is not
    /// dust.
    /// </summary>
    private async Task<(long ChangeSat, Script? ChangeScript)> GetChangeAsync(IReadOnlyList<WalletInput> inputs,
                                                                             long outputsSat, int extraWeight,
                                                                             long feeRatePerKw,
                                                                             AddressType changeType, string account = "default")
    {
        var totalSat = inputs.Sum(i => i.Amount.Satoshi);
        var weight = extraWeight + inputs.Sum(i => i.InputWeight);
        var feeWithChange = FeeSat(feeRatePerKw, weight + ChangeOutputWeight(changeType));
        if (totalSat - outputsSat - feeWithChange >= ChangeDustLimit(changeType))
            return (totalSat - outputsSat - feeWithChange, await NewChangeScriptAsync(changeType, account));

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

    private async Task<bool> HasConfirmedLeaseSpendAsync(FeeInputReservation reservation, CancellationToken ct, uint? requiredOverride = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var tip = _blockchainMonitor.LastProcessedBlockHeight;
        if (uow.WalletTransactionDbRepository is not { } repository) return false;
        var required = Math.Max(1u, requiredOverride ?? LeaseReleaseConfirmations(reservation.Purpose));
        if (tip + 1ul < required) return false;
        var lastEligibleHeight = tip - required + 1;
        var remaining = reservation.Inputs.Select(input => (input.TxId, input.Index)).ToHashSet();
        const int pageSize = 128;
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await repository.GetHistoryPageAsync(0, lastEligibleHeight, false, offset, pageSize, ct);
            foreach (var record in page)
            {
                if (record.BlockHeight is not { } height || height > lastEligibleHeight) continue;
                Transaction tx;
                try { tx = Transaction.Load(record.RawTransaction, _network); }
                catch (FormatException) { continue; }
                foreach (var input in tx.Inputs) remaining.Remove((new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N));
                if (remaining.Count == 0) return true;
            }
            if (page.Count < pageSize) return false;
        }
    }

    private static uint LeaseReleaseConfirmations(string purpose)
    {
        var parts = purpose.Split(':');
        return parts.Length == 4 && uint.TryParse(parts[3], out var confirmations) ? confirmations : 0;
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

            if (reservation.Inputs.All(i => !_utxoMemoryRepository.TryGetUtxo(i.TxId, i.Index, out _)) &&
                await HasConfirmedLeaseSpendAsync(reservation, cancellationToken))
            {
                await _feeInputSelector.ConfirmAsync(reservation.Id, cancellationToken);
                continue;
            }

            if (LeaseReleaseConfirmations(reservation.Purpose) > 0 &&
                await HasConfirmedLeaseSpendAsync(reservation, cancellationToken, 1))
                continue; // The configured confirmation hold survives ordinary lease expiry.
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

    /// <summary>The spent output of each input (null: another party's, unknown) and whether it is a leased wallet
    /// output.</summary>
    private sealed record ClassifiedInputs(TxOut?[] Spent, bool[] Ours);

    /// <summary>The weight of a change output of <paramref name="type"/>: P2WPKH 124, P2TR 172.</summary>
    private static int ChangeOutputWeight(AddressType type) =>
        type == AddressType.P2Tr ? (8 + 1 + 34) * 4 : WalletWeights.P2WpkhOutputWeight;

    /// <summary>The dust limit of a change output of <paramref name="type"/>: P2WPKH 294, P2TR 330.</summary>
    private static long ChangeDustLimit(AddressType type) =>
        type == AddressType.P2Tr ? 330 : WalletWeights.P2WpkhDustLimitSat;

    /// <summary>A fresh change address of <paramref name="type"/> (handed out once, NL-280).</summary>
    private async Task<Script> NewChangeScriptAsync(AddressType type, string account = "default")
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var wallet = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
        var address = account == "default" || account.Length == 0
            ? await wallet.GetUnusedAddressAsync(type, true)
            : await scope.ServiceProvider.GetRequiredService<WalletAccountService>().NextAsync(account, type, true, CancellationToken.None);
        return BitcoinAddress.Create(address.Address, _network).ScriptPubKey;
    }

    /// <summary>
    /// LND's <c>sanityCheckFee</c> (<c>max_fee_ratio</c>): the fee may be at most <paramref name="maxFeeRatio"/> of the
    /// outputs' total; 0 is no limit.
    /// </summary>
    internal static void CheckFeeRatio(long feeSat, long outputsSat, double maxFeeRatio)
    {
        if (maxFeeRatio <= 0)
            return;

        var maxFee = (long)(outputsSat * maxFeeRatio);
        if (feeSat > maxFee)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                                          $"fee {FormatBtc(feeSat)} on total output value {FormatBtc(outputsSat)} with "
                                        + $"max fee ratio of {maxFeeRatio.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string FormatBtc(long sat) =>
        (sat / 100_000_000m).ToString("0.########", CultureInfo.InvariantCulture) + " BTC";
}