using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Constants;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Accounting.Labels;
using Crypto.SilentPayments;
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
/// The on-chain <c>withdraw</c> (ClientCommand 25): pays an external address from the wallet's confirmed outputs,
/// through the persisted <see cref="IFeeInputSelector"/> reservations and the reserved-inputs-only wallet signer.
/// </summary>
/// <remarks>
/// <para>Selection: the fee input selector picks the largest unreserved confirmed outputs (never one locked to a
/// channel funding, reserved for a fee or spent by a pending broadcast) for the amount plus the fee, and a P2WPKH
/// change output of a fresh wallet change address when the rest is not dust. The amount is passed as the selector's
/// target, so the reservation's <see cref="FeeInputReservation.Fee"/> is amount plus fee.</para>
/// <para>Anchors reserve (NL-379): after the inputs are reserved (so a concurrent funding or spend sees them taken),
/// the outputs still backing the reserve plus this spend's change must cover it, else the reservation is released and
/// <see cref="AnchorReserveException"/> is thrown. "All" sends everything the selector could spend minus the fee and
/// the reserve, which returns as change.</para>
/// <para>Broadcast: the signed spend is stored as a <see cref="BroadcastPurpose.WalletSend"/> row before it is sent
/// (the chain monitor resends it after every block until it confirms). The reservation is released when anything
/// fails before the row is stored, and kept after (the pending row keeps the outputs out of every other selection
/// either way). Reservations whose inputs have all left the wallet (their spend was processed in a block) are ended
/// with <see cref="IFeeInputSelector.ConfirmAsync"/> at the next withdraw, as the chain monitor does at startup; those
/// none of whose inputs a pending broadcast spends (a crash or an unverifiable save between the reservation and the
/// row) are released then and by <see cref="ReleaseOrphanedReservationsAsync"/>, which the host runs once at startup.
/// Withdrawals run one at a time, so that sweep never sees one between its reservation and its row.</para>
/// </remarks>
public sealed class WalletSpendService : IWalletSpendService
{
    /// <summary>The purpose of the fee input reservations of a withdraw.</summary>
    public const string ReservationPurpose = "withdraw";

    /// <summary>BOLT 3's floor, 1 sat/vB: bitcoind's default minimum relay fee.</summary>
    internal const long MinFeeRatePerKw = 253;

    /// <summary>A sanity cap on a requested fee rate: 1,000 sat/vB.</summary>
    internal const long MaxFeeRatePerKw = 250_000;

    /// <summary>
    /// The weight of everything but the inputs and outputs: version and locktime (8 bytes), the input and output counts
    /// (one byte each up to 252) x 4, plus the segwit marker and flag (2 weight units).
    /// </summary>
    internal const int BaseWeight = (4 + 4 + 1 + 1) * 4 + 2;

    private readonly IWalletPsbtService? _psbt;
    private readonly IAnchorReserveService _anchorReserveService;
    private readonly IBitcoinChainService? _bitcoinChainService;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IFeeInputSelector _feeInputSelector;
    private readonly IFeeService _feeService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<WalletSpendService> _logger;
    private readonly Network _network;
    private readonly Domain.Protocol.ValueObjects.BitcoinNetwork _bitcoinNetwork;
    private readonly SilentPaymentsOptions _silentPayments;
    private readonly ISilentPaymentCrypto _silentPaymentCrypto;
    private readonly ISilentPaymentKeySource? _silentPaymentKeys;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public WalletSpendService(IFeeInputSelector feeInputSelector, IAnchorReserveService anchorReserveService,
                              IUtxoMemoryRepository utxoMemoryRepository, ILightningSigner lightningSigner,
                              IBlockchainMonitor blockchainMonitor, IFeeService feeService,
                              IServiceScopeFactory scopeFactory, IOptions<NodeOptions> nodeOptions,
                              ILogger<WalletSpendService> logger, IBitcoinChainService? bitcoinChainService = null,
                              IWalletPsbtService? psbt = null, IOptions<SilentPaymentsOptions>? silentPayments = null,
                              ISilentPaymentCrypto? silentPaymentCrypto = null,
                              ISilentPaymentKeySource? silentPaymentKeys = null)
    {
        _psbt = psbt;
        _silentPayments = silentPayments?.Value ?? new SilentPaymentsOptions();
        _silentPaymentCrypto = silentPaymentCrypto ?? new SilentPaymentCrypto();
        _silentPaymentKeys = silentPaymentKeys;
        _bitcoinNetwork = nodeOptions.Value.BitcoinNetwork;
        _feeInputSelector = feeInputSelector;
        _anchorReserveService = anchorReserveService;
        _utxoMemoryRepository = utxoMemoryRepository;
        _lightningSigner = lightningSigner;
        _blockchainMonitor = blockchainMonitor;
        _feeService = feeService;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _bitcoinChainService = bitcoinChainService;
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
    }

    /// <inheritdoc />
    public async Task<byte[]> SendOutputsAsync(IReadOnlyList<(BitcoinScript Script, LightningMoney Amount)> outputs,
                                               long feeRatePerKw, int minConfirmations, string label,
                                               CancellationToken cancellationToken = default)
    {
        var psbt = _psbt ?? throw new NotSupportedException("No wallet PSBT service.");
        if (outputs.Count is 0 or > 100 || minConfirmations < 1 || label.Length > 500)
            throw new ArgumentException("Invalid output count, confirmations or label.");
        var rate = await GetFeeRatePerKwAsync(LightningMoney.Satoshis(feeRatePerKw), cancellationToken);
        var lockId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var funded = await psbt.FundPsbtAsync(new PsbtFundRequest(outputs, [], rate, minConfirmations, lockId,
            TimeSpan.FromMinutes(10)), cancellationToken);
        PsbtFinalizeResult final;
        try
        {
            final = await psbt.FinalizePsbtAsync(funded.Psbt, cancellationToken);
        }
        catch
        {
            foreach (var lease in funded.Leases)
                await psbt.ReleaseAsync(lockId, lease.TxId, lease.Index, CancellationToken.None);
            throw;
        }
        // PublishAsync persists the accepted wallet transaction and keeps the leases until its inputs are spent.
        // On an ambiguous publish failure retain the leases: releasing could enable a conflicting wallet spend.
        if (!await psbt.PublishAsync(final.RawFinalTx, label, cancellationToken))
            throw new InvalidOperationException("Wallet transaction publication was rejected.");
        return final.RawFinalTx;
    }

    /// <inheritdoc />
    public async Task<WalletWithdrawResult> WithdrawAsync(WalletWithdrawRequest request,
                                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_blockchainMonitor.IsChainProcessingHalted)
            throw new WalletSpendException(WalletSpendError.ChainProcessingHalted,
                                           ChainProcessingHalt.Refusal("withdraw"));

        if (IsSilentPaymentAddress(request.Address))
            return await SendAsync([new WalletRecipient(request.Address, request.Amount)], request.FeeRatePerKw,
                                   request.MaxFee, request.Labels, cancellationToken);
        var destination = ParseAddress(request.Address, _network).ScriptPubKey;
        var feeRatePerKw = await GetFeeRatePerKwAsync(request.FeeRatePerKw, cancellationToken);

        // One withdrawal at a time, so the orphan sweep never sees one between its reservation and its row
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Reservations of earlier withdrawals whose spend a processed block holds, or that lost their spend
            await EndStaleReservationsLockedAsync(cancellationToken);

            return await WithdrawLockedAsync(request, destination, feeRatePerKw, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<WalletWithdrawResult> SendAsync(IReadOnlyList<WalletRecipient> recipients,
                                                     LightningMoney? feeRatePerKw = null,
                                                     LightningMoney? maxFee = null, SourceLabels? labels = null,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        if (recipients.Count is 0 or > 100 || (recipients.Count > 1 && recipients.Any(r => r.Amount is null)))
            throw new ArgumentException("Send needs 1 to 100 recipients; send-all needs a single recipient.", nameof(recipients));
        if (_blockchainMonitor.IsChainProcessingHalted)
            throw new WalletSpendException(WalletSpendError.ChainProcessingHalted, ChainProcessingHalt.Refusal("withdraw"));
        var addresses = recipients.ToArray();
        var destinations = new Script[addresses.Length];
        var silentAddresses = new List<SilentPaymentAddress>();
        var silentIndices = new List<int>();
        for (var i = 0; i < addresses.Length; i++)
        {
            var recipient = addresses[i];
            if (IsSilentPaymentAddress(recipient.Address))
            {
                var silent = ParseSilentPayment(recipient.Address);
                silentAddresses.Add(silent);
                silentIndices.Add(i);
                destinations[i] = new Script(new byte[] { 0x51, 0x20 }.Concat(new byte[32]).ToArray());
            }
            else
                destinations[i] = ParseAddress(recipient.Address, _network).ScriptPubKey;
            var minimum = silentIndices.Contains(i) ? Math.Max(330, _silentPayments.MinSendSat)
                                                    : GetDustThreshold(destinations[i]);
            if (recipient.Amount is { } amount && (amount.MilliSatoshi % 1000 != 0 || amount.Satoshi < minimum))
                throw new WalletSpendException(WalletSpendError.DustAmount, $"Recipient amount must be whole satoshis and at least {minimum} sat.");
        }
        var rate = await GetFeeRatePerKwAsync(feeRatePerKw, cancellationToken);
        var silentChange = silentAddresses.Count > 0 && _silentPayments.ChangeToSilentPayment;
        if (silentChange && (!_silentPayments.Receive || _silentPaymentKeys is null))
            throw new WalletSpendException(WalletSpendError.InvalidAddress,
                "Silent payment change requires Receive=true and the local silent payment keys.");
        var policy = new WalletSelectionPolicy(silentAddresses.Count > 0, silentAddresses.Count > 0,
            _silentPayments.AvoidMixing, silentChange, Math.Max(330, _silentPayments.MinReceiveSat));
        var extraWeight = BaseWeight + destinations.Sum(GetOutputWeight);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EndStaleReservationsLockedAsync(cancellationToken);
            var reserve = _anchorReserveService.GetRequiredReserve();
            var amounts = addresses.Select(r => r.Amount?.Satoshi ?? 0).ToArray();
            if (addresses[0].Amount is null)
                amounts[0] = await GetSendAllAmountAsync(extraWeight, rate, reserve,
                    silentAddresses.Count > 0 ? Math.Max(330, _silentPayments.MinSendSat) : GetDustThreshold(destinations[0]),
                    policy.PreferP2TrChange);
            var total = checked(amounts.Sum());
            var reservation = await _feeInputSelector.ReserveAsync(LightningMoney.Satoshis(total),
                LightningMoney.Satoshis(rate), extraWeight, ReservationPurpose, policy, cancellationToken);
            var stored = false;
            try
            {
                await EnsureReserveKeptAsync(reservation, total, cancellationToken);
                var frozenInputs = reservation.Inputs.Select(i => (i.TxId, i.Index)).ToArray();
                Script? silentChangeScript = null;
                var deriveSilentChange = policy.ChangeToSilentPayment &&
                    reservation.ChangeAmount.Satoshi >= policy.MinimumSilentChangeSat;
                if (deriveSilentChange)
                    silentAddresses.Add(GetSilentPaymentChangeAddress());
                if (policy.SilentPaymentSend)
                {
                    if (reservation.Inputs.Count == 0 || reservation.Inputs.Any(i =>
                        i.AddressType is not (AddressType.P2Wpkh or AddressType.P2Tr)))
                        throw new WalletSpendException(WalletSpendError.InvalidAddress, "Silent payment requires eligible wallet inputs.");
                    var scripts = _lightningSigner.ComputeSilentPaymentOutputs(reservation.Id, silentAddresses.ToArray(), frozenInputs);
                    if (scripts.Count != silentIndices.Count + (deriveSilentChange ? 1 : 0))
                        throw new InvalidOperationException("Signer returned an incorrect silent payment output count.");
                    for (var i = 0; i < scripts.Count; i++)
                    {
                        var script = (byte[])scripts[i];
                        if (script.Length != 34 || script[0] != 0x51 || script[1] != 0x20)
                            throw new InvalidOperationException("Signer returned an invalid silent payment output.");
                        if (i < silentIndices.Count)
                            destinations[silentIndices[i]] = new Script(script);
                        else
                            silentChangeScript = new Script(script);
                    }
                }
                var signed = BuildAndSign(reservation, destinations.Zip(amounts, (script, amount) =>
                    (Script: script, AmountSat: amount)).ToArray(), frozenInputs, silentChangeScript,
                    shuffleOutputs: policy.SilentPaymentSend);
                var fee = reservation.Total.Satoshi - total - reservation.ChangeAmount.Satoshi;
                if (maxFee is not null && LightningMoney.Satoshis(fee) > maxFee)
                    throw new WalletSpendException(WalletSpendError.FeeAboveLimit, "The transaction fee exceeds the requested maximum.");
                labels ??= SourceLabels.None;
                var row = new BroadcastTransactionModel(signed.Signed, BroadcastPurpose.WalletSend, null,
                    _blockchainMonitor.LastProcessedBlockHeight, (uint)rate, fee: LightningMoney.Satoshis(fee))
                {
                    Label = labels.Label,
                    Tags = labels.CanonicalTags
                };
                bool published;
                try
                {
                    published = await _blockchainMonitor.SaveAndPublishAsync(row);
                    stored = true;
                }
                catch
                {
                    stored = await IsStoredAsync(row.TransactionId);
                    throw;
                }
                return new WalletWithdrawResult(signed.Signed.TxId, LightningMoney.Satoshis(total),
                    LightningMoney.Satoshis(fee), reservation.ChangeAmount, LightningMoney.Satoshis(rate),
                    GetWeight(signed.Transaction), reservation.Inputs.Count, reserve, published)
                {
                    DestinationOutputIndex = checked((uint)signed.Transaction.Outputs.ToList().FindIndex(output =>
                        output.Value.Satoshi == amounts[0] && output.ScriptPubKey == destinations[0]))
                };
            }
            catch
            {
                if (!stored)
                    await _feeInputSelector.ReleaseAsync(reservation.Id, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private SilentPaymentAddress GetSilentPaymentChangeAddress()
    {
        var keys = _silentPaymentKeys ?? throw new InvalidOperationException("Silent payment change keys are unavailable.");
        if (!_silentPaymentCrypto.TrySumPublicKeys([keys.SpendPubKey, keys.GetLabelPoint(0)], out var labeledSpend))
            throw new InvalidOperationException("Silent payment change label produces an invalid public key.");
        return new SilentPaymentAddress(0, keys.ScanPubKey, labeledSpend, SilentPaymentAddressCodec.GetHrp(_bitcoinNetwork));
    }

    private static bool IsSilentPaymentAddress(string? text) => text is not null &&
        (text.StartsWith("sp1", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("tsp1", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("sprt1", StringComparison.OrdinalIgnoreCase));

    private SilentPaymentAddress ParseSilentPayment(string text)
    {
        if (!_silentPayments.Enabled || !_silentPayments.Send)
            throw new WalletSpendException(WalletSpendError.InvalidAddress, "Silent payment sending is disabled.");
        if (_bitcoinNetwork.Name == "mainnet" && !_silentPayments.AllowMainnet)
            throw new WalletSpendException(WalletSpendError.InvalidAddress, "Silent payment sending on mainnet requires AllowMainnet.");
        if (!SilentPaymentAddressCodec.TryDecode(text, _bitcoinNetwork, out var address, out var reason))
            throw new WalletSpendException(WalletSpendError.InvalidAddress, reason);
        if (!_silentPaymentCrypto.IsValidPoint(address.ScanKey) || !_silentPaymentCrypto.IsValidPoint(address.SpendKey))
            throw new WalletSpendException(WalletSpendError.InvalidAddress, "Silent payment address contains an invalid curve point.");
        return address;
    }

    /// <inheritdoc />
    public Task<WalletWithdrawEstimate> EstimateWithdrawFeeAsync(WalletWithdrawRequest request,
                                                                       CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount is not { } requested)
            throw new ArgumentException("An estimate needs an amount.", nameof(request));

        Script destination;
        if (IsSilentPaymentAddress(request.Address))
        {
            ParseSilentPayment(request.Address);
            if (requested.Satoshi < _silentPayments.MinSendSat)
                throw new WalletSpendException(WalletSpendError.DustAmount, "Silent payment amount is below MinSendSat.");
            destination = new Script(new byte[] { 0x51, 0x20 }.Concat(new byte[32]).ToArray());
        }
        else
            destination = ParseAddress(request.Address, _network).ScriptPubKey;
        return EstimateOutputFeeAsync(destination.ToBytes(), requested, request.FeeRatePerKw, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<WalletWithdrawEstimate> EstimateOutputFeeAsync(BitcoinScript script, LightningMoney requested,
        LightningMoney? requestedFeeRate, CancellationToken cancellationToken = default)
    {
        var destination = new Script((byte[])script);
        if (script.Length is 0 or > 10_000)
            throw new ArgumentException("Invalid quote script size.");
        var dustLimit = GetDustThreshold(destination);
        if (requested.MilliSatoshi % 1_000 != 0 || requested.Satoshi < dustLimit)
            throw new WalletSpendException(WalletSpendError.DustAmount,
                                           $"{requested.MilliSatoshi / 1_000.0:0.###} sat is not a whole amount at or "
                                         + $"above the dust limit of the destination ({dustLimit} sat).");

        var feeRatePerKw = await GetFeeRatePerKwAsync(requestedFeeRate, cancellationToken);
        var amountSat = requested.Satoshi;
        var weight = BaseWeight + GetOutputWeight(destination) + WalletWeights.P2WpkhOutputWeight;
        long totalSat = 0;
        var inputs = 0;
        foreach (var (outputSat, inputWeight) in (await GetSpendableOutputsAsync()).OrderByDescending(o => o.AmountSat))
        {
            totalSat += outputSat;
            weight += inputWeight;
            inputs++;
            if (totalSat >= amountSat + FeeSat(feeRatePerKw, weight))
                return new WalletWithdrawEstimate(LightningMoney.Satoshis(FeeSat(feeRatePerKw, weight)),
                                                  LightningMoney.Satoshis(feeRatePerKw), weight, inputs);
        }

        throw new InsufficientFundsException(LightningMoney.Satoshis(amountSat + FeeSat(feeRatePerKw, weight)),
                                             LightningMoney.Satoshis(totalSat));
    }

    /// <inheritdoc />
    public async Task<int> ReleaseOrphanedReservationsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await EndStaleReservationsLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WalletWithdrawResult> WithdrawLockedAsync(WalletWithdrawRequest request, Script destination,
                                                                 long feeRatePerKw,
                                                                 CancellationToken cancellationToken)
    {

        var extraWeight = BaseWeight + GetOutputWeight(destination);
        var dustLimit = GetDustThreshold(destination);
        var reserve = _anchorReserveService.GetRequiredReserve();

        long amountSat;
        if (request.Amount is { } requested)
        {
            if (requested.MilliSatoshi % 1_000 != 0)
                throw new WalletSpendException(WalletSpendError.DustAmount,
                                               "The amount must be a whole number of satoshis.");

            amountSat = requested.Satoshi;
            if (amountSat < dustLimit)
                throw new WalletSpendException(WalletSpendError.DustAmount,
                                               $"{amountSat} sat is below the dust limit of the destination "
                                             + $"({dustLimit} sat).");
        }
        else
        {
            amountSat = await GetSendAllAmountAsync(extraWeight, feeRatePerKw, reserve, dustLimit);
        }

        var reservation = await _feeInputSelector.ReserveAsync(LightningMoney.Satoshis(amountSat),
                                                               LightningMoney.Satoshis(feeRatePerKw), extraWeight,
                                                               ReservationPurpose, cancellationToken);
        var stored = false;
        try
        {
            await EnsureReserveKeptAsync(reservation, amountSat, cancellationToken);

            var signed = BuildAndSign(reservation, destination, amountSat);
            var feeSat = reservation.Total.Satoshi - amountSat - reservation.ChangeAmount.Satoshi;
            if (request.MaxFee is { } maxFee && LightningMoney.Satoshis(feeSat) > maxFee)
                throw new WalletSpendException(WalletSpendError.FeeAboveLimit,
                                               $"The fee would be {feeSat} sat, above the limit of "
                                             + $"{maxFee.MilliSatoshi / 1_000.0:0.###} sat.");
            var weight = GetWeight(signed.Transaction);

            // The absolute fee rides on the row (NL-604): the accounting feed records it when the spend confirms
            var row = new BroadcastTransactionModel(signed.Signed, BroadcastPurpose.WalletSend, null,
                                                    _blockchainMonitor.LastProcessedBlockHeight,
                                                    (uint)feeRatePerKw, fee: LightningMoney.Satoshis(feeSat))
            {
                // NL-602 A3-T1: the operator's label and tags, copied into the WalletSent event at confirmation
                Label = request.Labels.Label,
                Tags = request.Labels.CanonicalTags
            };
            bool published;
            try
            {
                published = await _blockchainMonitor.SaveAndPublishAsync(row);
                stored = true;
            }
            catch
            {
                // The row may have been saved before a later step failed: the pending row then owns the inputs
                stored = await IsStoredAsync(row.TransactionId);
                throw;
            }

            if (published)
                _logger.LogInformation(
                    "Withdrew {Amount} sat to {Address} in {TxId} (fee {Fee} sat, change {Change} sat, {Inputs} input(s))",
                    amountSat, request.Address, signed.Transaction.GetHash(), feeSat, reservation.ChangeAmount.Satoshi,
                    reservation.Inputs.Count);
            else
                _logger.LogWarning(
                    "Withdrawal {TxId} to {Address} was stored but bitcoind refused it; it is sent again after every block",
                    signed.Transaction.GetHash(), request.Address);

            return new WalletWithdrawResult(signed.Signed.TxId, LightningMoney.Satoshis(amountSat),
                                            LightningMoney.Satoshis(feeSat), reservation.ChangeAmount,
                                            LightningMoney.Satoshis(feeRatePerKw), weight,
                                            reservation.Inputs.Count, reserve, published);
        }
        catch
        {
            if (!stored)
                await _feeInputSelector.ReleaseAsync(reservation.Id, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// The destination address on <paramref name="network"/>; a <see cref="WalletSpendException"/> tells an address of
    /// another network from text that is no address.
    /// </summary>
    internal static BitcoinAddress ParseAddress(string? address, Network network)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new WalletSpendException(WalletSpendError.InvalidAddress, "No destination address given.");

        address = address.Trim();
        try
        {
            return BitcoinAddress.Create(address, network);
        }
        catch (FormatException)
        {
            // Tell the operator which network the address belongs to
            foreach (var other in new[]
                     {
                         Network.Main, Network.TestNet, NBitcoin.Bitcoin.Instance.Testnet4, Network.RegTest,
                         NBitcoin.Bitcoin.Instance.Signet
                     })
            {
                if (other == network)
                    continue;

                try
                {
                    _ = BitcoinAddress.Create(address, other);
                    throw new WalletSpendException(WalletSpendError.WrongNetwork,
                                                   $"{address} is a {other.ChainName} address; this node runs on "
                                                 + $"{network.ChainName}.");
                }
                catch (FormatException)
                {
                    // Not that network either
                }
            }

            throw new WalletSpendException(WalletSpendError.InvalidAddress,
                                           $"{address} is not a valid Bitcoin address.");
        }
    }

    /// <summary>
    /// Bitcoin Core's dust threshold of an output paying <paramref name="scriptPubKey"/> at the default dust relay fee
    /// (3 sat/vB): P2WPKH 294, P2TR and P2WSH 330, P2SH 540, P2PKH 546 sat.
    /// </summary>
    internal static long GetDustThreshold(Script scriptPubKey)
    {
        var length = scriptPubKey.Length;
        long size = 8 + CompactSizeLength(length) + length;

        // The input that would spend it: outpoint, sequence, script length, and a witness discounted by 4 or a
        // scriptSig of a signature and a key
        size += scriptPubKey.IsScriptType(ScriptType.Witness) || scriptPubKey.IsScriptType(ScriptType.Taproot)
                    ? 32 + 4 + 1 + 107 / 4 + 4
                    : 32 + 4 + 1 + 107 + 4;
        return size * 3;
    }

    /// <summary>The weight of an output paying <paramref name="scriptPubKey"/>.</summary>
    internal static int GetOutputWeight(Script scriptPubKey)
    {
        var length = scriptPubKey.Length;
        return (8 + CompactSizeLength(length) + length) * 4;
    }

    private async Task<long> GetFeeRatePerKwAsync(LightningMoney? requested, CancellationToken cancellationToken)
    {
        long feeRatePerKw;
        if (requested is { } rate)
        {
            feeRatePerKw = rate.Satoshi;
            if (feeRatePerKw < MinFeeRatePerKw)
                throw new WalletSpendException(WalletSpendError.FeeRateTooLow,
                                               $"The fee rate {feeRatePerKw} sat/kw is below the relay minimum of "
                                             + $"{MinFeeRatePerKw} sat/kw (1 sat/vB).");
            if (feeRatePerKw > MaxFeeRatePerKw)
                throw new WalletSpendException(WalletSpendError.FeeRateTooHigh,
                                               $"The fee rate {feeRatePerKw} sat/kw is above the cap of "
                                             + $"{MaxFeeRatePerKw} sat/kw ({MaxFeeRatePerKw / 250} sat/vB).");
        }
        else
        {
            feeRatePerKw = Math.Max(MinFeeRatePerKw,
                                    (await _feeService.GetFeeRatePerKwAsync(cancellationToken)).Satoshi);
        }

        uint? mempoolMin = null;
        if (_bitcoinChainService is not null)
        {
            try
            {
                mempoolMin = await _bitcoinChainService.GetMempoolMinFeeRatePerKwAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogDebug(e, "Could not read bitcoind's mempool minimum fee; not checked");
            }
        }

        if (mempoolMin is { } min && feeRatePerKw < min)
        {
            if (requested is not null)
                throw new WalletSpendException(WalletSpendError.FeeRateTooLow,
                                               $"The fee rate {feeRatePerKw} sat/kw is below bitcoind's mempool "
                                             + $"minimum of {min} sat/kw; the transaction would not relay.");

            feeRatePerKw = min;
        }

        return feeRatePerKw;
    }

    /// <summary>
    /// "All": every output the selector could spend, minus the fee of spending them all to the destination and, with a
    /// reserve, the reserve (at least a non-dust change output) that returns as change.
    /// </summary>
    private async Task<long> GetSendAllAmountAsync(int extraWeight, long feeRatePerKw, LightningMoney reserve,
                                                   long dustLimit, bool preferP2TrChange = false)
    {
        var candidates = await GetSpendableOutputsAsync();
        var totalSat = candidates.Sum(c => c.AmountSat);
        var keepSat = reserve.IsZero ? 0 : Math.Max(reserve.Satoshi, preferP2TrChange ? 330 : WalletWeights.P2WpkhDustLimitSat);
        var weight = extraWeight + candidates.Sum(c => (long)c.InputWeight)
                   + (keepSat > 0 ? (preferP2TrChange ? 172 : WalletWeights.P2WpkhOutputWeight) : 0);
        var feeSat = FeeSat(feeRatePerKw, weight);
        var amountSat = totalSat - feeSat - keepSat;
        if (amountSat >= dustLimit)
            return amountSat;

        if (keepSat > 0)
            throw new AnchorReserveException(
                $"Nothing to withdraw: {totalSat} sat confirmed and spendable, the fee is {feeSat} sat and the anchors "
              + $"reserve of {reserve.Satoshi} sat stays in the wallet.", LightningMoney.Satoshis(feeSat + keepSat
                                                                                                 + dustLimit),
                LightningMoney.Satoshis(totalSat), reserve);

        throw new InsufficientFundsException(LightningMoney.Satoshis(feeSat + dustLimit),
                                             LightningMoney.Satoshis(totalSat));
    }

    /// <summary>
    /// The outputs <see cref="FeeInputSelector"/> would consider: unreserved, mined, of a known P2WPKH or P2TR wallet
    /// address, and not spent by one of our pending broadcasts.
    /// </summary>
    private async Task<List<(long AmountSat, int InputWeight)>> GetSpendableOutputsAsync()
    {
        HashSet<(TxId TxId, uint Index)> excluded;
        using (var scope = _scopeFactory.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            excluded = await PendingBroadcastOutpoints.GetAsync(uow, _network, _logger);
        }

        var outputs = new List<(long AmountSat, int InputWeight)>();
        foreach (var utxo in _utxoMemoryRepository.GetUnreservedUtxos())
        {
            if (excluded.Contains((utxo.TxId, utxo.Index)) || utxo.BlockHeight == 0 || (utxo.WalletAddress is null && utxo.SilentPayment is null)
             || utxo.AddressType is not (AddressType.P2Wpkh or AddressType.P2Tr))
                continue;

            outputs.Add((utxo.Amount.Satoshi, WalletWeights.GetInputWeight(utxo.AddressType)));
        }

        return outputs;
    }

    /// <summary>
    /// With the inputs reserved (so they no longer count as available): the outputs still backing the reserve plus
    /// this spend's change must cover it.
    /// </summary>
    private async Task EnsureReserveKeptAsync(FeeInputReservation reservation, long amountSat,
                                              CancellationToken cancellationToken)
    {
        var status = await _anchorReserveService.GetStatusAsync(cancellationToken);
        if (status.RequiredReserve.IsZero)
            return;

        var keptSat = status.AvailableBalance.Satoshi + reservation.ChangeAmount.Satoshi;
        if (keptSat >= status.RequiredReserve.Satoshi)
            return;

        var spendableSat = Math.Max(0, status.AvailableBalance.Satoshi + reservation.Total.Satoshi
                                       - status.RequiredReserve.Satoshi);
        var hint = spendableSat > 0
                       ? $"about {spendableSat} sat minus the fee can be withdrawn (use 'all')"
                       : "nothing can be withdrawn while the channels need it";
        throw new AnchorReserveException(
            $"Withdrawing {amountSat} sat would leave {keptSat} sat in the wallet, below the anchors reserve of "
          + $"{status.RequiredReserve.Satoshi} sat for {status.AnchorsChannelCount} anchors channel(s); {hint}.",
            LightningMoney.Satoshis(amountSat) + status.RequiredReserve,
            status.AvailableBalance + reservation.Total, status.RequiredReserve);
    }

    private (SignedTransaction Signed, Transaction Transaction) BuildAndSign(FeeInputReservation reservation,
                                                                           Script destination, long amountSat)
    {
        return BuildAndSign(reservation, [(destination, amountSat)], reservation.Inputs.Select(i => (i.TxId, i.Index)).ToArray());
    }

    private (SignedTransaction Signed, Transaction Transaction) BuildAndSign(FeeInputReservation reservation,
        IReadOnlyList<(Script Script, long AmountSat)> outputs, IReadOnlyList<(TxId TxId, uint Index)> frozenInputs,
        Script? silentChangeScript = null, bool shuffleOutputs = false)
    {
        if (!reservation.Inputs.Select(i => (i.TxId, i.Index)).SequenceEqual(frozenInputs))
            throw new InvalidOperationException("Wallet inputs changed after silent payment derivation; derive again before signing.");
        var tx = _network.CreateTransaction();
        tx.Version = 2;
        tx.LockTime = LockTime.Zero;
        foreach (var input in reservation.Inputs)
            tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])input.TxId), input.Index))
            {
                // Opt in to replacement (BIP 125)
                Sequence = new Sequence(0xFFFFFFFD)
            });

        foreach (var output in outputs)
            tx.Outputs.Add(Money.Satoshis(output.AmountSat), output.Script);
        if (silentChangeScript is not null)
            tx.Outputs.Add(Money.Satoshis(reservation.ChangeAmount.Satoshi), silentChangeScript);
        else if (reservation.ChangeScript is { } changeScript)
            tx.Outputs.Add(Money.Satoshis(reservation.ChangeAmount.Satoshi), new Script((byte[])changeScript));
        if (shuffleOutputs)
            for (var i = tx.Outputs.Count - 1; i > 0; i--)
            {
                var swap = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
                (tx.Outputs[i], tx.Outputs[swap]) = (tx.Outputs[swap], tx.Outputs[i]);
            }

        var signed = new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());
        if (!_lightningSigner.SignWalletTransaction(signed, reservation.Id, []))
            throw new InvalidOperationException($"The signer signed no input of withdrawal {tx.GetHash()}");

        // Check every input with the script interpreter, independently of the signer's own check
        var result = Transaction.Load(signed.RawTxBytes, _network);
        if (!result.Inputs.Select(i => (new TxId(i.PrevOut.Hash.ToBytes()), i.PrevOut.N)).SequenceEqual(frozenInputs))
            throw new InvalidOperationException("Signer changed the frozen inputs; silent payment derivation must be repeated.");
        var spentOutputs = reservation.Inputs
                                      .Select(i => new TxOut(Money.Satoshis(i.Amount.Satoshi),
                                                             new Script((byte[])i.ScriptPubKey)))
                                      .ToArray();
        var validator = result.CreateValidator(spentOutputs);
        for (var i = 0; i < result.Inputs.Count; i++)
        {
            var check = validator.ValidateInput(i);
            if (check.Error is not (null or ScriptError.OK))
                throw new InvalidOperationException(
                    $"Input {i} of withdrawal {result.GetHash()} does not verify: {check.Error}");
        }

        if (result.GetHash() != tx.GetHash())
            throw new InvalidOperationException("The signer changed the withdrawal's txid");

        return (signed, result);
    }

    /// <summary>
    /// Under <see cref="_gate"/>: ends the withdraw reservations none of whose inputs is still in the wallet (the chain
    /// monitor removed them when it processed the block holding their spend), and releases those none of whose inputs a
    /// pending broadcast spends: a crash, or a failed or unverifiable save, between the reservation and its
    /// <see cref="BroadcastPurpose.WalletSend"/> row left them without a spend, and nothing else would ever free them.
    /// </summary>
    /// <returns>How many reservations were released.</returns>
    private async Task<int> EndStaleReservationsLockedAsync(CancellationToken cancellationToken)
    {
        List<FeeInputReservation> reservations;
        try
        {
            reservations = (await _feeInputSelector.GetAllAsync(cancellationToken))
                          .Where(r => r.Purpose == ReservationPurpose)
                          .ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not read the fee input reservations; completed withdrawals are ended later");
            return 0;
        }

        if (reservations.Count == 0)
            return 0;

        HashSet<(TxId TxId, uint Index)>? pendingSpends;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            pendingSpends = await PendingBroadcastOutpoints.GetAsync(uow, _network, _logger);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unknown: keep every reservation, which never double-spends
            _logger.LogWarning(e, "Could not read the pending broadcasts; withdraw reservations are checked later");
            pendingSpends = null;
        }

        var released = 0;
        foreach (var reservation in reservations)
        {
            if (!reservation.Inputs.Any(i => _utxoMemoryRepository.TryGetUtxo(i.TxId, i.Index, out _)))
            {
                await _feeInputSelector.ConfirmAsync(reservation.Id, cancellationToken);
                continue;
            }

            if (pendingSpends is null || reservation.Inputs.Any(i => pendingSpends.Contains((i.TxId, i.Index))))
                continue;

            _logger.LogWarning(
                "Releasing withdraw reservation {ReservationId} ({Inputs} input(s), {Total} sat): no pending "
              + "withdrawal spends its inputs", reservation.Id, reservation.Inputs.Count, reservation.Total.Satoshi);
            await _feeInputSelector.ReleaseAsync(reservation.Id, cancellationToken);
            released++;
        }

        return released;
    }

    private async Task<bool> IsStoredAsync(TxId txId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            return await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId) is not null;
        }
        catch (Exception e)
        {
            // Unknown: keep the reservation, which never double-spends
            _logger.LogWarning(e, "Could not check whether withdrawal {TxId} was stored; its inputs stay reserved",
                               txId);
            return true;
        }
    }

    private static int CompactSizeLength(int value) => value < 0xFD ? 1 : value <= 0xFFFF ? 3 : 5;

    private static long FeeSat(long feeRatePerKw, long weight) => (feeRatePerKw * weight + 999) / 1000;

    /// <summary>BIP 141 weight: the size without witnesses x 3 plus the full size.</summary>
    internal static int GetWeight(Transaction transaction) =>
        transaction.GetSerializedSize(TransactionOptions.None) * 3
      + transaction.GetSerializedSize(TransactionOptions.Witness);
}