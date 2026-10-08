using NBitcoin;
using NLightning.Infrastructure.Bitcoin.Networks;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed record NativeWalletInputEvidence(string TransactionId, uint OutputIndex, long AmountSatoshis,
                                               byte[] ScriptPubKey, string OwnerId, string NodeId, bool Unspent);
public sealed record NativeApprovedOutput(byte[] ScriptPubKey, long AmountSatoshis);
public sealed record NativeWalletSpendingIntent(IReadOnlyList<NativeApprovedOutput> Destinations,
                                              long MaximumFeeSatoshis);

/// <summary>
/// Installed by signer administration with authenticated transport and independent freshness checks. Neither node
/// WalletSnapshot data nor an RPC-selected evidence provider may implement this authority boundary.
/// </summary>
public interface IAuthenticatedNativeChainEvidence
{
    void RequireFresh(NativeSignerBinding binding);
    NativeWalletInputEvidence GetOutput(NativeSignerBinding binding, string transactionId, uint outputIndex);
}

/// <summary>Validates wallet spending against independent evidence and signer-derived change scripts before key use.</summary>
public sealed class NativeWalletTransactionValidator(IAuthenticatedNativeChainEvidence evidence,
                                                     Func<NativeSignerBinding, byte[], bool> isSignerOwnedChange)
{
    public void Validate(NativeSignerBinding binding, byte[] rawTransaction, NativeWalletSpendingIntent intent)
    {
        if (intent.MaximumFeeSatoshis < 0 || intent.Destinations.Count == 0
         || intent.Destinations.Any(x => x.AmountSatoshis <= 0 || x.ScriptPubKey.Length == 0))
            throw new ArgumentException("Spending intent requires positive destinations and a nonnegative fee limit.");
        evidence.RequireFresh(binding);
        var transaction = Transaction.Load(rawTransaction, NBitcoinNetworkResolver.Resolve(binding.Network));
        if (transaction.Inputs.Count == 0 || transaction.Outputs.Count == 0)
            throw new InvalidOperationException("Transaction must have inputs and outputs.");
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        long inputValue = 0;
        foreach (var input in transaction.Inputs)
        {
            var txId = input.PrevOut.Hash.ToString();
            var index = input.PrevOut.N;
            if (!inputs.Add(txId + ":" + index)) throw new InvalidOperationException("Duplicate transaction input.");
            var previous = evidence.GetOutput(binding, txId, index);
            if (previous.TransactionId != txId || previous.OutputIndex != index || !previous.Unspent
             || previous.NodeId != binding.NodeId || previous.OwnerId != binding.OwnerId
             || previous.AmountSatoshis <= 0 || !isSignerOwnedChange(binding, previous.ScriptPubKey))
                throw new UnauthorizedAccessException("Input is not an independently verified unspent signer-owned output.");
            inputValue = checked(inputValue + previous.AmountSatoshis);
        }
        var remaining = intent.Destinations.ToList();
        long outputValue = 0;
        foreach (var output in transaction.Outputs)
        {
            var amount = output.Value.Satoshi;
            if (amount <= 0) throw new InvalidOperationException("Zero or negative outputs are not authorized.");
            outputValue = checked(outputValue + amount);
            var script = output.ScriptPubKey.ToBytes();
            var approved = remaining.FindIndex(x => x.AmountSatoshis == amount && x.ScriptPubKey.AsSpan().SequenceEqual(script));
            if (approved >= 0) remaining.RemoveAt(approved);
            else if (!isSignerOwnedChange(binding, script))
                throw new UnauthorizedAccessException("Output destination or amount is not owner approved.");
        }
        if (remaining.Count != 0) throw new UnauthorizedAccessException("Transaction omitted an owner-approved destination.");
        var fee = checked(inputValue - outputValue);
        if (fee < 0 || fee > intent.MaximumFeeSatoshis)
            throw new UnauthorizedAccessException("Transaction fee exceeds independently verified owner intent.");
    }

}