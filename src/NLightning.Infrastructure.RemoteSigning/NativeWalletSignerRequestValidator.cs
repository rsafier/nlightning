using NBitcoin;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Infrastructure.Bitcoin.Networks;
using SignedTransaction = NLightning.Domain.Bitcoin.ValueObjects.SignedTransaction;
using TxId = NLightning.Domain.Bitcoin.ValueObjects.TxId;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Purpose-specific account-zero withdrawal validation. Other signing purposes are unavailable.</summary>
public sealed class NativeWalletSignerRequestValidator(INativeWalletApprovalStore approvals,
    INativeSignerWalletDerivationRegistry derivations, IAuthenticatedNativeChainEvidence evidence,
    TimeProvider? clock = null) : INativeSignerRequestValidator
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public void Validate(NativeSignerBinding binding, NativeSignerIntent intent)
    {
        var prepared = ValidateImmutableIntent(binding, intent);
        if (prepared.Approved.Expires <= _clock.GetUtcNow().ToUnixTimeSeconds())
            throw new UnauthorizedAccessException("Owner wallet approval has expired.");
        // Capture one source batch so snapshot checks and economic validation use identical output evidence.
        evidence.RequireFresh(binding);
        var outputs = new Dictionary<(string, uint), NativeWalletInputEvidence>();
        foreach (var input in prepared.Approved.Approval.Inputs)
        {
            var output = evidence.GetOutput(binding, input.TransactionId, input.OutputIndex);
            var utxo = prepared.Utxos[(input.TransactionId, input.OutputIndex)];
            var script = derivations.GetScript(binding, input.Derivation);
            if (output.TransactionId != input.TransactionId || output.OutputIndex != input.OutputIndex
             || output.NodeId != binding.NodeId || output.OwnerId != binding.OwnerId || !output.Unspent
             || output.AmountSatoshis <= 0 || output.AmountSatoshis > 2_100_000_000_000_000L
             || utxo.Amount.MilliSatoshi != checked((ulong)output.AmountSatoshis * 1000UL)
             || utxo.Amount.Satoshi != output.AmountSatoshis
             || !output.ScriptPubKey.AsSpan().SequenceEqual(script))
                throw new UnauthorizedAccessException("Wallet snapshot does not match independently verified owned input.");
            outputs.Add((input.TransactionId, input.OutputIndex), output);
        }
        new NativeWalletTransactionValidator(new CapturedEvidence(binding, evidence, outputs), derivations.IsOwned)
            .Validate(binding, prepared.Transaction.RawTxBytes, prepared.Approved.Approval.Spending);
        if (prepared.Approved.Expires <= _clock.GetUtcNow().ToUnixTimeSeconds())
            throw new UnauthorizedAccessException("Owner wallet approval expired during independent validation.");
    }

    public void ValidateReplay(NativeSignerBinding binding, NativeSignerIntent intent)
    {
        // Exact completed receipts remain valid after broadcast and expiry; this path performs no key operation.
        _ = ValidateImmutableIntent(binding, intent);
    }

    private PreparedWalletIntent ValidateImmutableIntent(NativeSignerBinding binding, NativeSignerIntent intent)
    {
        if (intent.Operation != SignerOperations.SignWalletTransaction3)
            throw new NotSupportedException("This installed validator authorizes only reserved account-zero withdrawals.");
        var approved = approvals.GetWalletApproval(binding, intent);
        if (approved.Binding != binding || approved.RequestId != intent.RequestId
         || approved.Fingerprint != NativeSignerAuthority.Fingerprint(binding, intent))
            throw new UnauthorizedAccessException("Wallet approval does not match this immutable signing intent.");
        var args = SignerWire.Decode(intent.Payload);
        if (args.Length != SignerOperations.ArgumentCount(intent.Operation))
            throw new ArgumentException("Wallet request arguments are incomplete.");
        var unsigned = SignerWire.Read<SignedTransaction>(args[0]);
        var tx = Transaction.Load(unsigned.RawTxBytes, NBitcoinNetworkResolver.Resolve(binding.Network));
        if (unsigned.TxId != new TxId(tx.GetHash().ToBytes()) || unsigned.Signatures is { Count: > 0 }
         || tx.Inputs.Any(input => input.ScriptSig.Length != 0 || input.WitScript.PushCount != 0))
            throw new UnauthorizedAccessException("Wallet signing requires the exact unsigned transaction identity.");
        if (SignerWire.Read<Guid>(args[1]) != approved.Approval.ReservationId
         || approved.Approval.ReservationId == Guid.Empty || SignerWire.Read<SpentOutput[]>(args[2]).Length != 0)
            throw new UnauthorizedAccessException("Wallet reservation or external inputs are not authorized.");
        var snapshot = SignerWire.Read<WalletSnapshot>(args[3]);
        var required = approved.Approval.Inputs.ToDictionary(input => (input.TransactionId, input.OutputIndex));
        var outpoints = tx.Inputs.Select(input => (input.PrevOut.Hash.ToString(), input.PrevOut.N)).ToArray();
        if (required.Count == 0 || outpoints.Length != required.Count || outpoints.Distinct().Count() != outpoints.Length
         || !required.Keys.ToHashSet().SetEquals(outpoints)
         || snapshot.Utxos is null || snapshot.Reservations is null
         || snapshot.Utxos.Length != required.Count || snapshot.Reservations.Length != required.Count)
            throw new UnauthorizedAccessException("Wallet snapshot must contain the complete approved input and reservation sets.");
        var reserved = new HashSet<(string, uint)>();
        foreach (var reservation in snapshot.Reservations)
        {
            var key = (uint256.Parse(reservation.TxId.ToString()).ToString(), reservation.Index);
            if (reservation.ReservationId != approved.Approval.ReservationId || !required.ContainsKey(key) || !reserved.Add(key))
                throw new UnauthorizedAccessException("Wallet snapshot reservation differs from independent input claim.");
        }
        var utxos = new Dictionary<(string, uint), UtxoModel>();
        foreach (var utxo in snapshot.Utxos)
        {
            var key = (uint256.Parse(utxo.TxId.ToString()).ToString(), utxo.Index);
            if (!required.TryGetValue(key, out var input) || !utxos.TryAdd(key, utxo))
                throw new UnauthorizedAccessException("Wallet snapshot input is duplicated or unapproved.");
            var address = utxo.WalletAddress;
            var locator = input.Derivation;
            if (utxo.SilentPayment is not null || utxo.LockedToChannelId is not null || utxo.UsedInTransactionId is not null
             || address is null || address.AccountIndex != 0 || address.DerivationIndex is not null
             || utxo.AddressIndex != locator.Index || utxo.IsAddressChange != locator.IsChange
             || utxo.AddressType != locator.AddressType || address.Index != locator.Index
             || address.IsChange != locator.IsChange || address.AddressType != locator.AddressType
             || address.AccountName != "default" || utxo.Amount.MilliSatoshi % 1000UL != 0)
                throw new UnauthorizedAccessException("Wallet snapshot key metadata differs from the installed signer derivation.");
            var expected = derivations.GetScript(binding, locator);
            var actual = BitcoinAddress.Create(address.Address, NBitcoinNetworkResolver.Resolve(binding.Network)).ScriptPubKey.ToBytes();
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new UnauthorizedAccessException("Wallet snapshot address is not the independently approved derived script.");
        }
        return new PreparedWalletIntent(approved, unsigned, utxos);
    }

    private sealed record PreparedWalletIntent(NativeWalletApprovedIntent Approved, SignedTransaction Transaction,
                                               IReadOnlyDictionary<(string, uint), UtxoModel> Utxos);

    private sealed class CapturedEvidence(NativeSignerBinding binding, IAuthenticatedNativeChainEvidence source,
        IReadOnlyDictionary<(string, uint), NativeWalletInputEvidence> outputs) : IAuthenticatedNativeChainEvidence
    {
        public void RequireFresh(NativeSignerBinding requested)
        {
            if (requested != binding) throw new UnauthorizedAccessException("Captured wallet evidence belongs to another enrollment.");
        }
        public NativeWalletInputEvidence GetOutput(NativeSignerBinding requested, string txid, uint index)
        {
            if (requested != binding || !outputs.TryGetValue((txid, index), out var output))
                throw new UnauthorizedAccessException("Wallet input has no captured authenticated evidence.");
            return output;
        }
        public void RequireUnchanged(NativeSignerBinding requested) => source.RequireUnchanged(requested);
    }
}