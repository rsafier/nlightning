using System.Text.Json;
using System.Text.Json.Nodes;
using NBitcoin;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Commitments;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Signing.Vls;
using CompactSignature = NLightning.Domain.Crypto.ValueObjects.CompactSignature;
using Hash = NLightning.Domain.Crypto.ValueObjects.Hash;
using TxId = NLightning.Domain.Bitcoin.ValueObjects.TxId;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>VLS semantic policy operations; generic channel signing is explicitly refused.</summary>
public sealed partial class VlsLightningSigner(VlsSignerConnection connection, VlsChannelMappingRegistry mappings,
    IChannelSigningInfoSource? signingInfoSource = null, IUtxoMemoryRepository? wallet = null) : ILightningSigner, IVlsChannelSigner, IVlsGossipSigner
{
    private readonly Dictionary<ChannelId, ChannelSigningInfo> _registered = [];
    public uint CreateNewChannel(out ChannelBasepoints basepoints, out CompactPubKey firstPerCommitmentPoint) =>
        throw new NotSupportedException("VLS allocation requires peer identity.");
    public uint CreateNewChannel(CompactPubKey peer, out ChannelBasepoints basepoints, out CompactPubKey firstPerCommitmentPoint)
    {
        var (keyIndex, result) = mappings.Allocate(peer);
        basepoints = ReadBasepoints(result);
        firstPerCommitmentPoint = GetPerCommitmentPoint(keyIndex, 0);
        return keyIndex;
    }
    public ChannelBasepoints GetChannelBasepoints(uint channelKeyIndex) => ReadBasepoints(connection.Invoke(VlsOperations.Basepoints,
        Command("basepoints", ChannelHex(mappings.GetByIndex(channelKeyIndex).VlsChannelId))));
    public ChannelBasepoints GetChannelBasepoints(ChannelId channelId) => GetChannelBasepoints(Info(channelId).ChannelKeyIndex);
    public CompactPubKey GetNodePublicKey() => connection.Identity.NodePublicKey;
    public CompactPubKey GetPerCommitmentPoint(uint channelKeyIndex, ulong commitmentNumber) => Point(ChannelHex(mappings.GetByIndex(channelKeyIndex).VlsChannelId), commitmentNumber);
    public CompactPubKey GetPerCommitmentPoint(ChannelId channelId, ulong commitmentNumber) => Point(Channel(channelId), commitmentNumber);
    public void RegisterChannel(ChannelId channelId, ChannelSigningInfo signingInfo)
    {
        if (signingInfo.IsSimpleTaproot || signingInfo.IsDualFunded || signingInfo.LocalFundingKeyIndex != 0 || signingInfo.Fundings is { Count: > 1 }) throw Unsupported();
        mappings.Bind(channelId, signingInfo.ChannelKeyIndex);
        lock (_registered)
        {
            if (_registered.TryGetValue(channelId, out var prior) && (prior.FundingTxId != signingInfo.FundingTxId || prior.FundingOutputIndex != signingInfo.FundingOutputIndex || prior.LocalFundingPubKey != signingInfo.LocalFundingPubKey || prior.RemoteFundingPubKey != signingInfo.RemoteFundingPubKey))
                throw new SignerException("VLS channel funding registration changed.");
            _registered[channelId] = signingInfo;
        }
        if (signingInfo.DataLossDetected) MarkDataLoss(channelId);
        if (signingInfo.BroadcastSignedCommitmentNumber is { } closed) MarkBroadcastSigned(channelId, closed);
    }
    public void UnregisterChannel(ChannelId channelId) { lock (_registered) _registered.Remove(channelId); }
    public void EnsureChannelSetup(ChannelModel channel)
    {
        if (channel.FundingOutput is null || channel.RemoteKeySet is null) throw new SignerException("VLS setup requires funding and peer keys.");
        var p = channel.RemoteKeySet;
        var setup = new JsonObject
        {
            ["is_outbound"] = channel.IsInitiator,
            ["channel_value_sat"] = channel.FundingOutput.Amount.Satoshi,
            ["push_value_msat"] = channel.IsInitiator ? channel.RemoteBalance.MilliSatoshi : channel.LocalBalance.MilliSatoshi,
            ["funding_outpoint"] = new JsonObject { ["txid"] = channel.FundingOutput.TransactionId!.Value.ToInternalHex(), ["vout"] = channel.FundingOutput.Index },
            ["holder_selected_contest_delay"] = channel.ChannelParams.Local.ToSelfDelay,
            ["counterparty_selected_contest_delay"] = channel.ChannelParams.Remote.ToSelfDelay,
            ["holder_shutdown_script"] = ScriptArray(channel.LocalUpfrontShutdownScript),
            ["counterparty_shutdown_script"] = ScriptArray(channel.RemoteUpfrontShutdownScript),
            ["commitment_type"] = channel.ChannelParams.OptionAnchorOutputs ? "AnchorsZeroFeeHtlc" : "StaticRemoteKey",
            ["counterparty_points"] = new JsonObject { ["funding_pubkey"] = p.FundingCompactPubKey.ToString(), ["revocation_basepoint"] = p.RevocationCompactBasepoint.ToString(), ["payment_point"] = p.PaymentCompactBasepoint.ToString(), ["delayed_payment_basepoint"] = p.DelayedPaymentCompactBasepoint.ToString(), ["htlc_basepoint"] = p.HtlcCompactBasepoint.ToString() }
        };
        var command = Command("setup", Channel(channel.ChannelId)); command["setup"] = setup;
        command["shutdown_path"] = ShutdownPath(channel.LocalUpfrontShutdownScript);
        connection.Invoke(VlsOperations.Setup, command);
    }
    public CommitmentSignatures SignCounterpartyCommitment(ChannelModel channel, CommitmentTransactionModel commitment)
    {
        var command = CommitmentCommand("sign_remote", channel.ChannelId, commitment, remote: true);
        RecordPreimages(channel);
        command["point"] = commitment.PerCommitmentPoint?.ToString() ?? throw new SignerException("Commitment point required.");
        var result = connection.Invoke(VlsOperations.SignRemote, command);
        return new(Signature(result.GetProperty("signature")), result.GetProperty("htlc_signatures").EnumerateArray().Select(Signature).ToArray());
    }
    public void ValidateHolderCommitment(ChannelModel channel, CommitmentTransactionModel commitment, CompactSignature signature, IReadOnlyList<CompactSignature> htlcSignatures)
    {
        var command = CommitmentCommand("validate_holder", channel.ChannelId, commitment, remote: false);
        RecordPreimages(channel);
        command["signature"] = Hex(signature.Value);
        command["htlc_signatures"] = new JsonArray(htlcSignatures.Select(s => JsonValue.Create(Hex(s.Value))).Cast<JsonNode?>().ToArray());
        connection.Invoke(VlsOperations.ValidateHolder, command);
    }
    public void ActivateChannel(ChannelModel channel) => connection.Invoke(VlsOperations.Activate, Command("activate", Channel(channel.ChannelId)));
    public Secret RevokeHolderCommitment(ChannelId channelId, ulong revokedCommitmentNumber)
    {
        var command = Command("revoke_holder", Channel(channelId)); command["number"] = checked(revokedCommitmentNumber + 1);
        var result = connection.Invoke(VlsOperations.RevokeHolder, command);
        return new Secret(Convert.FromHexString(result.GetProperty("secret").GetString() ?? throw new SignerException("VLS did not release prior secret.")));
    }
    public void ValidatePeerRevocation(ChannelId channelId, ulong revokedCommitmentNumber, Secret secret, CompactPubKey? nextPerCommitmentPoint = null)
    {
        var command = Command("validate_revocation", Channel(channelId)); command["number"] = revokedCommitmentNumber; command["secret"] = Hex((byte[])secret); command["next_point"] = nextPerCommitmentPoint?.ToString();
        connection.Invoke(VlsOperations.ValidateRevocation, command);
    }
    public CompactSignature SignMutualClose(ChannelModel channel, ulong holderSatoshis, ulong peerSatoshis, BitcoinScript? holderScript, BitcoinScript? peerScript)
    {
        var command = Command("mutual_close", Channel(channel.ChannelId)); command["holder_sat"] = holderSatoshis; command["peer_sat"] = peerSatoshis;
        command["holder_script"] = holderScript?.ToString(); command["peer_script"] = peerScript?.ToString(); command["shutdown_path"] = ShutdownPath(holderScript);
        return Signature(connection.Invoke(VlsOperations.MutualClose, command).GetProperty("signature"));
    }
    public Secret RevealPerCommitmentSecret(ChannelId channelId, ulong commitmentNumber) => RevokeHolderCommitment(channelId, commitmentNumber);
    public void AdvanceLocalCommitment(ChannelId channelId, ulong newLocalCommitmentNumber) => throw new NotSupportedException("Use VLS holder activation/revocation semantics.");
    public CompactSignature SignNodeMessage(Hash messageHash) => throw new NotSupportedException("VLS requires typed gossip messages, not arbitrary digest signing.");
    public bool VerifyNodeMessage(Hash messageHash, CompactSignature signature, CompactPubKey nodeId)
    {
        try { return new PubKey(nodeId).Verify(new uint256(messageHash), ParseSignature(signature)); } catch { return false; }
    }
    public IReadOnlyList<CompactSignature> SignRemoteHtlcTransactions(ChannelId channelId, IReadOnlyList<HtlcSigningContext> htlcTransactions) => throw Unsupported();
    public void ValidateLocalHtlcSignatures(ChannelId channelId, IReadOnlyList<HtlcSigningContext> htlcTransactions, IReadOnlyList<CompactSignature> signatures) => throw Unsupported();
    public SignedTransaction SignLocalCommitmentForBroadcast(ChannelId channelId, ulong commitmentNumber, SignedTransaction unsignedCommitment, CompactSignature remoteSignature)
    {
        var info = Info(channelId);
        if (info.DataLossDetected) throw new SignerException("VLS channel has data loss.");
        var command = Command("force_close", Channel(channelId)); command["number"] = commitmentNumber;
        var local = Signature(connection.Invoke(VlsOperations.ForceClose, command).GetProperty("signature"));
        var tx = Transaction.Load(unsignedCommitment.RawTxBytes, Network.RegTest);
        var localPub = new PubKey(info.LocalFundingPubKey); var remotePub = new PubKey(info.RemoteFundingPubKey);
        var localFirst = string.CompareOrdinal(localPub.ToHex(), remotePub.ToHex()) < 0;
        var funding = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, localFirst ? localPub : remotePub, localFirst ? remotePub : localPub);
        if (tx.Inputs.Count != 1 || tx.Inputs[0].PrevOut.Hash != new uint256(info.FundingTxId) || tx.Inputs[0].PrevOut.N != info.FundingOutputIndex)
            throw new SignerException("Force-close transaction funding input mismatch.");
        var hash = tx.GetSignatureHash(funding, 0, SigHash.All, new TxOut(Money.Satoshis(info.FundingSatoshis / 1000), funding.WitHash.ScriptPubKey), HashVersion.WitnessV0);
        if (!ParseSignature(local).IsLowS || !ParseSignature(remoteSignature).IsLowS || !localPub.Verify(hash, ParseSignature(local)) || !remotePub.Verify(hash, ParseSignature(remoteSignature)))
            throw new SignerException("Force-close transaction does not match validated VLS commitment signatures.");
        var ours = new TransactionSignature(ParseSignature(local), SigHash.All).ToBytes();
        var theirs = new TransactionSignature(ParseSignature(remoteSignature), SigHash.All).ToBytes();
        var order = localFirst;
        tx.Inputs[0].WitScript = new WitScript(new[] { Array.Empty<byte>(), order ? ours : theirs, order ? theirs : ours, funding.ToBytes() });
        unsignedCommitment.RawTxBytes = tx.ToBytes(); return unsignedCommitment;
    }
    public void MarkDataLoss(ChannelId channelId) { connection.Invoke(VlsOperations.MarkDataLoss, Command("mark_data_loss", Channel(channelId))); lock (_registered) { var info = Info(channelId); _registered[channelId] = info with { DataLossDetected = true }; } }
    public void MarkBroadcastSigned(ChannelId channelId, ulong commitmentNumber)
    {
        var command = Command("verify_broadcast_mark", Channel(channelId)); command["number"] = commitmentNumber;
        connection.Invoke(VlsOperations.VerifyBroadcastMark, command);
    }
    public bool TryGetBroadcastSignedCommitment(ChannelId channelId, out ulong commitmentNumber)
    {
        var result = connection.Invoke(VlsOperations.BroadcastStatus, Command("broadcast_status", Channel(channelId))).GetProperty("number");
        commitmentNumber = result.ValueKind == JsonValueKind.Null ? 0 : result.GetUInt64(); return result.ValueKind != JsonValueKind.Null;
    }
    public bool SignFundingTransaction(ChannelId channelId, SignedTransaction unsignedTransaction)
    {
        if (wallet is null) throw new SignerException("VLS wallet context unavailable.");
        var tx = Transaction.Load(unsignedTransaction.RawTxBytes, Network.RegTest);
        var inputs = new JsonArray(); var prev = new JsonArray(); var outputs = new JsonArray();
        foreach (var input in tx.Inputs)
        {
            if (!wallet.TryGetUtxo(new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N, out var utxo) || utxo.LockedToChannelId != channelId || utxo.WalletAddress is null || utxo.WalletAddress.AccountIndex != 0)
                throw new SignerException("Funding input is not locked to the VLS channel wallet.");
            var pub = new VlsSecureKeyManager(connection).GetWalletPublicKey(utxo.WalletAddress.DerivationIndex ?? utxo.AddressIndex, utxo.IsAddressChange, utxo.AddressType);
            var script = new PubKey(pub).WitHash.ScriptPubKey;
            if (BitcoinAddress.Create(utxo.WalletAddress.Address, Network.RegTest).ScriptPubKey != script) throw new SignerException("Wallet address does not match VLS derivation.");
            inputs.Add("m/" + VlsSecureKeyManager.WalletIndex(utxo.WalletAddress.DerivationIndex ?? utxo.AddressIndex, utxo.IsAddressChange));
            prev.Add(new JsonObject { ["value"] = utxo.Amount.Satoshi, ["script_pubkey"] = script.ToHex() });
        }
        foreach (var output in tx.Outputs) outputs.Add(FindWalletPath(output.ScriptPubKey) ?? "m");
        var command = new JsonObject { ["op"] = "wallet_sign", ["transaction"] = tx.ToHex(), ["input_paths"] = inputs, ["prev_outputs"] = prev, ["output_paths"] = outputs };
        var result = connection.Invoke(VlsOperations.WalletSign, command);
        var witnesses = result.GetProperty("witnesses").EnumerateArray().ToArray();
        if (witnesses.Length != tx.Inputs.Count) throw new SignerException("VLS wallet witness count mismatch.");
        for (var i = 0; i < witnesses.Length; i++) tx.Inputs[i].WitScript = new WitScript(witnesses[i].EnumerateArray().Select(w => Convert.FromHexString(w.GetString()!)).ToArray());
        unsignedTransaction.RawTxBytes = tx.ToBytes(); return true;
    }
    public CompactSignature SignChannelTransaction(ChannelId channelId, SignedTransaction unsignedTransaction) => throw Unsupported();
    public void ValidateSignature(ChannelId channelId, CompactSignature signature, SignedTransaction unsignedTransaction)
    {
        // Verification uses public funding information only; this never grants a signing capability.
        var info = Info(channelId);
        var tx = Transaction.Load(unsignedTransaction.RawTxBytes, Network.RegTest);
        if (tx.Inputs.Count != 1 || tx.Inputs[0].PrevOut.Hash != new uint256(info.FundingTxId)
            || tx.Inputs[0].PrevOut.N != info.FundingOutputIndex)
            throw new SignerException("Channel transaction funding input mismatch.");
        var local = new PubKey(info.LocalFundingPubKey); var remote = new PubKey(info.RemoteFundingPubKey);
        var localFirst = string.CompareOrdinal(local.ToHex(), remote.ToHex()) < 0;
        var funding = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, localFirst ? local : remote, localFirst ? remote : local);
        var hash = tx.GetSignatureHash(funding, 0, SigHash.All,
            new TxOut(Money.Satoshis(info.FundingSatoshis / 1000), funding.WitHash.ScriptPubKey), HashVersion.WitnessV0);
        var parsed = ParseSignature(signature);
        if (!parsed.IsLowS || !remote.Verify(hash, parsed)) throw new SignerException("Peer funding signature does not verify.");
    }
    private ChannelSigningInfo Info(ChannelId id)
    {
        lock (_registered) if (_registered.TryGetValue(id, out var info)) return info;
        if (signingInfoSource is not null && signingInfoSource.TryGet(id, out var saved)) { RegisterChannel(id, saved); return saved; }
        throw new SignerException("VLS channel registration unavailable.");
    }
    private string Channel(ChannelId id) => ChannelHex(mappings.GetByChannelId(id).VlsChannelId);
    private static string ChannelHex(byte[]? bytes) => Convert.ToHexString(bytes ?? throw new SignerException("VLS channel allocation incomplete.")).ToLowerInvariant();
    private CompactPubKey Point(string channel, ulong number) { var command = Command("point", channel); command["number"] = number; return new(Convert.FromHexString(connection.Invoke(VlsOperations.Point, command).GetProperty("point").GetString()!)); }
    private static JsonObject Command(string op, string channel) => new() { ["op"] = op, ["channel"] = channel };
    private static NBitcoin.Crypto.ECDSASignature ParseSignature(CompactSignature signature) => NBitcoin.Crypto.ECDSASignature.TryParseFromCompact(signature.Value, out var parsed) ? parsed : throw new SignerException("Invalid ECDSA signature.");
    private static CompactSignature Signature(JsonElement element) => new(Convert.FromHexString(element.GetString()!));
    private static ChannelBasepoints ReadBasepoints(JsonElement element) => new(new(Convert.FromHexString(element.GetProperty("funding").GetString()!)), new(Convert.FromHexString(element.GetProperty("revocation").GetString()!)), new(Convert.FromHexString(element.GetProperty("payment").GetString()!)), new(Convert.FromHexString(element.GetProperty("delay").GetString()!)), new(Convert.FromHexString(element.GetProperty("htlc").GetString()!)));
    private JsonObject CommitmentCommand(string op, ChannelId channel, CommitmentTransactionModel model, bool remote)
    {
        if (model.IsSimpleTaproot) throw Unsupported();
        return new JsonObject
        {
            ["op"] = op,
            ["channel"] = Channel(channel),
            ["number"] = model.Number,
            ["feerate"] = checked((uint)model.FeeRatePerKw),
            ["holder_sat"] = remote ? model.ToRemoteOutput?.Amount.Satoshi ?? 0 : model.ToLocalOutput?.Amount.Satoshi ?? 0,
            ["peer_sat"] = remote ? model.ToLocalOutput?.Amount.Satoshi ?? 0 : model.ToRemoteOutput?.Amount.Satoshi ?? 0,
            ["offered"] = Htlcs(model.OfferedHtlcOutputs),
            ["received"] = Htlcs(model.ReceivedHtlcOutputs)
        };
    }
    private static JsonArray Htlcs(IEnumerable<HtlcOutputInfo> outputs) => new(outputs.Select(h => (JsonNode)new JsonObject { ["value_sat"] = h.Amount.Satoshi, ["payment_hash"] = Bytes((byte[])h.PaymentHash), ["cltv_expiry"] = h.CltvExpiry }).ToArray());
    private void RecordPreimages(ChannelModel channel)
    {
        var values = channel.Commitments?.Htlcs.Values.Select(h => h.KnownPreimage ?? h.Removal?.PaymentPreimage).Where(p => p is not null).Select(p => Hex((byte[])p!.Value)).Distinct().Order().ToArray() ?? [];
        if (values.Length == 0) return;
        var command = Command("payment_preimages", Channel(channel.ChannelId)); command["preimages"] = new JsonArray(values.Select(v => JsonValue.Create(v)).Cast<JsonNode?>().ToArray());
        connection.Invoke(VlsOperations.PaymentPreimages, command);
    }
    private string? ShutdownPath(BitcoinScript? script) => script is null ? null : FindWalletPath(new Script((byte[])script.Value)) ?? throw new SignerException("Shutdown destination is not a VLS wallet address.");
    private string? FindWalletPath(Script script)
    {
        // Wallet address indexes are public metadata. A bounded prototype lookup supports fresh-node funding/close.
        var keys = new VlsSecureKeyManager(connection);
        for (uint index = 0; index < 1024; index++) foreach (var change in new[] { false, true })
                if (new PubKey(keys.GetWalletPublicKey(index, change, NLightning.Domain.Bitcoin.Enums.AddressType.P2Wpkh)).WitHash.ScriptPubKey == script)
                    return "m/" + VlsSecureKeyManager.WalletIndex(index, change);
        return null;
    }
    private static JsonArray Bytes(byte[] bytes) => new(bytes.Select(b => JsonValue.Create(b)).Cast<JsonNode?>().ToArray());
    private static JsonNode? ScriptArray(BitcoinScript? script) => script is null ? null : Bytes((byte[])script.Value);
    public CompactSignature SignChannelUpdate(byte[] unsignedCanonicalPayload) => Signature(connection.Invoke(VlsOperations.ChannelUpdate, new JsonObject { ["op"] = "sign_channel_update", ["payload"] = Hex(unsignedCanonicalPayload) }).GetProperty("signature"));
    public CompactSignature SignNodeAnnouncement(byte[] unsignedCanonicalPayload) => Signature(connection.Invoke(VlsOperations.NodeAnnouncement, new JsonObject { ["op"] = "sign_node_announcement", ["payload"] = Hex(unsignedCanonicalPayload) }).GetProperty("signature"));
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static NotSupportedException Unsupported() => new("Operation is outside the VLS ECDSA semantic prototype.");
}