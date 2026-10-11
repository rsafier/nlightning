using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Constants;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;

public sealed partial class WalletPsbtService
{
    private async Task<PsbtFundResult> FundTemplateAsync(PsbtFundRequest request, CancellationToken cancellationToken)
    {
        CheckLockId(request.LockId);
        var template = LoadFundingTemplate(request.TemplatePsbt ?? []);
        var source = template.GetGlobalTransaction();
        var transaction = source.Inputs.Count == 0 ? _network.CreateTransaction() : source.Clone();
        if (source.Inputs.Count == 0)
        {
            // Zero-input templates are valid funding requests, but are not serializable stand-alone transactions.
            transaction.Version = source.Version;
            transaction.LockTime = source.LockTime;
            foreach (var output in source.Outputs) transaction.Outputs.Add(output.Clone());
        }
        if (transaction.Outputs.Count == 0 || transaction.Inputs.Select(input => input.PrevOut).Distinct().Count() != transaction.Inputs.Count)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "template needs outputs and unique inputs");
        if (request.FeeRatePerKw is < WalletSpendService.MinFeeRatePerKw or > WalletSpendService.MaxFeeRatePerKw ||
            double.IsNaN(request.MaxFeeRatio) || request.MaxFeeRatio is < 0 or > 1)
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "invalid fee rate or maximum fee ratio");
        if (request.ExistingChangeOutputIndex is { } change && (change < 0 || change >= transaction.Outputs.Count))
            throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "existing change output index is out of range");
        for (var index = 0; index < transaction.Outputs.Count; index++)
        {
            var output = transaction.Outputs[index];
            if (request.ExistingChangeOutputIndex == index && output.Value.Satoshi == 0) continue;
            if (output.Value.Satoshi < WalletSpendService.GetDustThreshold(output.ScriptPubKey))
                throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "template contains a dust output");
        }
        if (_blockchainMonitor.IsChainProcessingHalted)
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "chain processing is halted");
        await _gate.WaitAsync(cancellationToken);
        Guid? selectionId = null;
        try
        {
            await SweepLockedAsync(cancellationToken);
            if (request.SpendUnconfirmed && request.MinConfirmations != 0)
                throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "spend_unconfirmed requires min_confs=0");
            if (request.SpendUnconfirmed)
                await (_mempoolCatalog ?? throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                    "unconfirmed wallet catalogue unavailable")).RefreshAsync(cancellationToken);
            var classified = await ClassifyInputsAsync(transaction, template, cancellationToken);
            long existingSat = 0;
            var weight = WalletSpendService.BaseWeight + transaction.Outputs.Sum(output => WalletSpendService.GetOutputWeight(output.ScriptPubKey));
            for (var index = 0; index < template.Inputs.Count; index++)
            {
                var supplied = template.Inputs[index].WitnessUtxo
                    ?? throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "coin_select inputs require witness_utxo");
                if (classified.Spent[index] is not { } proven || proven.Value != supplied.Value || proven.ScriptPubKey != supplied.ScriptPubKey)
                    throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "template input differs from its proven previous output");
                existingSat = checked(existingSat + supplied.Value.Satoshi);
                weight = checked(weight + TemplateInputWeight(supplied.ScriptPubKey, template.Inputs[index]));
            }
            var outputSat = transaction.Outputs.Sum(output => output.Value.Satoshi);
            var newChange = request.ExistingChangeOutputIndex is null;
            var changeWeight = newChange ? ChangeOutputWeight(request.ChangeAddressType) : 0;
            IReadOnlyList<WalletInput> added = [];
            var duration = request.LockDuration > TimeSpan.Zero ? request.LockDuration : DefaultLeaseDuration;
            var expiration = Expiration(duration);
            if (existingSat - outputSat < FeeSat(request.FeeRatePerKw, weight + changeWeight))
            {
                var selection = await _feeInputSelector.ReserveAsync(LightningMoney.Satoshis(Math.Max(0, outputSat - existingSat)),
                    LightningMoney.Satoshis(request.FeeRatePerKw), weight, LeasePurpose(request.LockId, expiration, request.ReleaseAfterSpendConfs),
                    WalletSelectionPolicy.Default with
                    {
                        IncludeUnconfirmed = request.SpendUnconfirmed,
                        Account = request.Account,
                        ConfirmationTip = _blockchainMonitor.LastProcessedBlockHeight,
                        MinConfirmations = (uint)(request.SpendUnconfirmed ? 0 : Math.Max(1, request.MinConfirmations)),
                        PreferP2TrChange = request.ChangeAddressType == AddressType.P2Tr
                    }, cancellationToken);
                selectionId = selection.Id;
                added = selection.Inputs;
                foreach (var input in added)
                {
                    if (transaction.Inputs.Any(existing => existing.PrevOut.Hash == new uint256(input.TxId) && existing.PrevOut.N == input.Index))
                        throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "selected input already exists in template");
                    if (!_utxoMemoryRepository.TryGetUtxo(input.TxId, input.Index, out var coin) ||
                        Confirmations(coin.BlockHeight, _blockchainMonitor.LastProcessedBlockHeight) < (request.SpendUnconfirmed ? 0 : Math.Max(1, request.MinConfirmations)))
                        throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "not enough sufficiently confirmed wallet inputs");
                    transaction.Inputs.Add(new TxIn(new OutPoint(new uint256(input.TxId), input.Index)) { Sequence = new Sequence(0xFFFFFFFD) });
                    weight = checked(weight + input.InputWeight);
                }
            }
            var fee = FeeSat(request.FeeRatePerKw, weight + changeWeight);
            var changeSat = checked(existingSat + added.Sum(input => input.Amount.Satoshi) - outputSat - fee);
            if (changeSat < 0)
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "template and selected inputs cannot pay the fee");
            var changeIndex = request.ExistingChangeOutputIndex ?? -1;
            if (changeIndex >= 0)
            {
                transaction.Outputs[changeIndex].Value += Money.Satoshis(changeSat);
                if (transaction.Outputs[changeIndex].Value.Satoshi < WalletSpendService.GetDustThreshold(transaction.Outputs[changeIndex].ScriptPubKey))
                    throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "recalculated existing change is dust");
            }
            else if (changeSat >= ChangeDustLimit(request.ChangeAddressType))
            {
                changeIndex = transaction.Outputs.Count;
                transaction.Outputs.Add(Money.Satoshis(changeSat), await NewChangeScriptAsync(request.ChangeAddressType, request.Account));
            }
            else
            {
                fee = checked(fee + changeSat);
                changeSat = 0;
            }
            // A caller-selected existing change script may belong to a foreign participant. It cannot back our reserve.
            var ownedChange = newChange && added.All(input => _utxoMemoryRepository.TryGetUtxo(input.TxId, input.Index, out var coin)
                && coin.BlockHeight > 0) && classified.Ours.Select((ours, index) => (ours, index)).Where(item => item.ours).All(item =>
                    _utxoMemoryRepository.TryGetUtxo(new TxId(transaction.Inputs[item.index].PrevOut.Hash.ToBytes()),
                        transaction.Inputs[item.index].PrevOut.N, out var coin) && coin.BlockHeight > 0);
            await EnsureReserveKeptAsync(ownedChange && changeIndex >= 0 ? changeSat : 0, cancellationToken);
            CheckFeeRatio(fee, outputSat, request.MaxFeeRatio);
            var funded = PSBT.FromTransaction(transaction, _network).UpdateFrom(template);
            for (var index = 0; index < template.Inputs.Count; index++) funded.Inputs[index].UpdateFrom(template.Inputs[index]);
            for (var index = 0; index < template.Outputs.Count; index++) funded.Outputs[index].UpdateFrom(template.Outputs[index]);
            for (var index = 0; index < added.Count; index++)
            {
                var input = added[index];
                var destination = funded.Inputs[template.Inputs.Count + index];
                destination.WitnessUtxo = new TxOut(Money.Satoshis(input.Amount.Satoshi), new Script((byte[])input.ScriptPubKey));
                if (_utxoMemoryRepository.TryGetUtxo(input.TxId, input.Index, out var coin)) AddKeyInfo(destination, coin);
            }
            return new PsbtFundResult(funded.ToBytes(), changeIndex,
                added.Select(input => ToLease(request.LockId, expiration, input)).ToArray(), LightningMoney.Satoshis(fee));
        }
        catch
        {
            if (selectionId is { } id) await _feeInputSelector.ReleaseAsync(id, CancellationToken.None);
            throw;
        }
        finally { _gate.Release(); }
    }

    private static int TemplateInputWeight(Script script, PSBTInput input)
    {
        if (script.IsScriptType(ScriptType.Taproot)) return WalletWeights.GetInputWeight(AddressType.P2Tr);
        if (script.IsScriptType(ScriptType.P2WPKH)) return WalletWeights.GetInputWeight(AddressType.P2Wpkh);
        if (input.FinalScriptWitness is { } witness)
            return checked(164 + witness.ToBytes().Length + (input.FinalScriptSig?.ToBytes().Length ?? 0) * 4);
        throw new WalletPsbtException(WalletPsbtError.InvalidArgument, "foreign input needs a supported key-path script or final witness for weight estimation");
    }
}