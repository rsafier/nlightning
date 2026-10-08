using System.Text.Json;
using NBitcoin;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.Bitcoin.Networks;
using SignedTransaction = NLightning.Domain.Bitcoin.ValueObjects.SignedTransaction;
using TxId = NLightning.Domain.Bitcoin.ValueObjects.TxId;
using WireRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.Infrastructure.RemoteSigning;

public sealed partial class RemoteSigningWorkflowCoordinator
{
    public SignedTransaction? ReplayPsbtPublication(ISigningWorkflowScope workflow)
    {
        if (workflow is not WorkflowScope active || !ReferenceEquals(_active.Value, active)
         || active.Kind != SigningWorkflowKind.WalletPsbtPublication)
            throw Blocked("PSBT publication recovery requires its activated workflow scope.");
        var (intent, packet) = ReadPsbtPublicationIntent(active.PublicationIntent, _connection.Identity.Network);
        var requests = active.SavedRequests;
        if (requests.Count == 0) return null;
        if (requests.Count != 1 || requests[0].Operation != SignerOperations.SignWalletTransaction2)
            throw Blocked("PSBT publication recovery contains an unexpected request sequence.");
        var request = requests[0];
        var response = Execute(request.Operation, request.Envelope, request.ArgumentFingerprint,
            envelope => RemoteSignerConnection.ToWorkflowStatus(_connection.Reconcile(WireRequest.Parser.ParseFrom(envelope))),
            envelope => _connection.ExecutePayload(WireRequest.Parser.ParseFrom(envelope)));
        var result = SignerWire.Decode(response);
        if (result.Length != 2 || !SignerWire.Read<bool>(result[0]))
            throw Blocked("The recovered PSBT receipt did not sign every wallet input.");
        var signed = SignerWire.Read<SignedTransaction>(result[1]);
        var tx = Transaction.Load(signed.RawTxBytes, NBitcoinNetworkResolver.Resolve(_connection.Identity.Network));
        if (signed.TxId != new TxId(tx.GetHash().ToBytes())
         || !tx.WithOptions(TransactionOptions.None).ToBytes().AsSpan().SequenceEqual(intent.UnsignedTransaction)
         || tx.Inputs.Any(input => input.ScriptSig.Length != 0 || input.WitScript.PushCount == 0))
            throw Blocked("The recovered PSBT transaction differs from its frozen unsigned publication intent.");
        var validator = tx.CreateValidator(packet.Inputs.Select(input => input.WitnessUtxo!).ToArray());
        for (var index = 0; index < tx.Inputs.Count; index++)
            if (validator.ValidateInput(index).Error is { } error && error != ScriptError.OK)
                throw Blocked("The recovered PSBT receipt does not satisfy its frozen previous-output script.");
        return signed;
    }

    private static (NativeWalletPsbtPublicationIntent Intent, PSBT Packet) ReadPsbtPublicationIntent(byte[]? encoded,
                                                                                                   string network)
    {
        if (encoded is null) throw Blocked("PSBT publication lost its immutable intent.");
        var intent = JsonSerializer.Deserialize<NativeWalletPsbtPublicationIntent>(encoded)
            ?? throw Blocked("PSBT publication intent is missing.");
        if (intent.FundedPsbt is not { Length: > 0 } || intent.UnsignedTransaction is not { Length: > 0 }
         || intent.ReservationIds is not { Count: > 0 and <= 1024 }
         || intent.ReservationIds.Any(id => id == Guid.Empty)
         || intent.ReservationIds.Distinct().Count() != intent.ReservationIds.Count || intent.FeeSat < 0)
            throw Blocked("PSBT publication intent has invalid funded packet or reservation terms.");
        var packet = PSBT.Load(intent.FundedPsbt, NBitcoinNetworkResolver.Resolve(network));
        var tx = packet.GetGlobalTransaction();
        if (!tx.ToBytes().AsSpan().SequenceEqual(intent.UnsignedTransaction)
         || tx.Inputs.Count is <= 0 or > 1024 || tx.Outputs.Count == 0
         || tx.Inputs.Select(input => input.PrevOut).Distinct().Count() != tx.Inputs.Count
         || tx.Inputs.Any(input => input.ScriptSig.Length != 0 || input.WitScript.PushCount != 0)
         || packet.Inputs.Any(input => input.WitnessUtxo is null || input.WitnessUtxo.Value.Satoshi <= 0))
            throw Blocked("PSBT publication packet does not match its complete unsigned wallet input set.");
        var inputValue = packet.Inputs.Aggregate(0L, (total, input) => checked(total + input.WitnessUtxo!.Value.Satoshi));
        var outputValue = tx.Outputs.Aggregate(0L, (total, output) => checked(total + output.Value.Satoshi));
        if (tx.Outputs.Any(output => output.Value.Satoshi <= 0) || checked(inputValue - outputValue) != intent.FeeSat)
            throw Blocked("PSBT publication packet differs from its frozen fee.");
        return (intent, packet);
    }

    private static void ValidatePsbtPublicationEnvelope(JsonElement[] args, byte[]? encoded, string network)
    {
        var (intent, packet) = ReadPsbtPublicationIntent(encoded, network);
        var unsigned = SignerWire.Read<SignedTransaction>(args[0]);
        var tx = packet.GetGlobalTransaction();
        if (!unsigned.RawTxBytes.AsSpan().SequenceEqual(intent.UnsignedTransaction)
         || unsigned.TxId != new TxId(tx.GetHash().ToBytes())
         || SignerWire.Read<Domain.Bitcoin.Wallet.Models.SpentOutput[]>(args[1]).Length != 0)
            throw Blocked("PSBT signing request differs from its frozen all-wallet publication intent.");
        var snapshot = SignerWire.Read<WalletSnapshot>(args[2]);
        if (snapshot.Utxos is null || snapshot.Reservations is null || snapshot.Utxos.Length != tx.Inputs.Count
         || snapshot.Reservations.Length != tx.Inputs.Count)
            throw Blocked("PSBT signing snapshot does not contain its complete frozen input and lease sets.");
        var inputs = tx.Inputs.Select(input => (new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N)).ToHashSet();
        if (!inputs.SetEquals(snapshot.Utxos.Select(utxo => (utxo.TxId, utxo.Index)))
         || !inputs.SetEquals(snapshot.Reservations.Select(reservation => (reservation.TxId, reservation.Index)))
         || !intent.ReservationIds.ToHashSet().SetEquals(snapshot.Reservations.Select(reservation => reservation.ReservationId)))
            throw Blocked("PSBT signing snapshot differs from its frozen wallet inputs or reservations.");
        for (var index = 0; index < tx.Inputs.Count; index++)
        {
            var outpoint = tx.Inputs[index].PrevOut;
            var utxo = snapshot.Utxos.Single(input => input.TxId == new TxId(outpoint.Hash.ToBytes()) && input.Index == outpoint.N);
            var previous = packet.Inputs[index].WitnessUtxo!;
            if (utxo.Amount.MilliSatoshi % 1000UL != 0 || utxo.Amount.Satoshi != previous.Value.Satoshi
             || utxo.LockedToChannelId is not null || utxo.UsedInTransactionId is not null)
                throw Blocked("PSBT signing snapshot differs from its frozen available previous outputs.");
            var script = utxo.WalletAddress is { } address
                ? BitcoinAddress.Create(address.Address, NBitcoinNetworkResolver.Resolve(network)).ScriptPubKey
                : utxo.SilentPayment is { } silent ? new Script(new byte[] { 0x51, 0x20 }.Concat(silent.OutputKey).ToArray())
                : throw Blocked("PSBT signing snapshot lacks its frozen wallet script.");
            if (script != previous.ScriptPubKey)
                throw Blocked("PSBT signing snapshot differs from its frozen previous-output script.");
        }
    }
}