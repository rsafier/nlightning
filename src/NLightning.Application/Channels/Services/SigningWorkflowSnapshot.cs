using System.Security.Cryptography;
using System.Text;

namespace NLightning.Application.Channels.Services;

using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Signing.Recovery;

/// <summary>Versioned, length-delimited application state identity, independent of wire encodings.</summary>
public static class SigningWorkflowSnapshot
{
    /// <summary>Opening inputs are bound before an engine snapshot or received signature exists.</summary>
    public static SigningWorkflowDescriptor CreateOpening(ChannelModel channel, SigningWorkflowKind kind)
    {
        if (channel.RemoteKeySet is not { } remote || channel.FundingOutput?.TransactionId is null
         || channel.FundingOutput.Index is null || channel.LocalCommitmentNumber != 0 || channel.RemoteCommitmentNumber != 0)
            throw new InvalidOperationException("VLS opening requires complete commitment-zero funding inputs.");
        var inputs = ChannelCommitments.Create(channel.ChannelId, CommitmentParams.FromChannel(channel),
            channel.LocalBalance.MilliSatoshi, channel.RemoteBalance.MilliSatoshi,
            checked((uint)channel.ChannelParams.FeeRateAmountPerKw.Satoshi),
            remote.CurrentPerCommitmentCompactPoint, remote.CurrentPerCommitmentCompactPoint);
        var descriptor = Create(channel, inputs, kind);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(descriptor.SnapshotFingerprint);
        writer.Write(channel.LocalUpfrontShutdownScript?.ToString() ?? "");
        writer.Write(channel.RemoteUpfrontShutdownScript?.ToString() ?? "");
        if (kind is SigningWorkflowKind.Activate or SigningWorkflowKind.Funding)
        {
            writer.Write(channel.LastReceivedSignature is { } received ? Convert.ToHexString(received.Value) : "");
            writer.Write(channel.LastSentSignature is { } sent ? Convert.ToHexString(sent.Value) : "");
            if (kind == SigningWorkflowKind.Funding)
            {
                writer.Write(channel.LastReceivedPartialSignature is { } receivedPartial
                    ? Convert.ToHexString(receivedPartial.ToBytes()) : "");
            }
        }
        writer.Flush();
        return descriptor with { SnapshotFingerprint = SHA256.HashData(stream.ToArray()) };
    }

    public static SigningWorkflowDescriptor Create(ChannelModel channel, ChannelCommitments commitments,
                                                    SigningWorkflowKind kind)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(1);
        writer.Write(channel.ChannelId.ToString());
        writer.Write(channel.RemoteNodeId.ToString());
        writer.Write(channel.LocalKeySet.KeyIndex);
        writer.Write(channel.LocalFundingPubKey.ToString());
        writer.Write(channel.RemoteFundingPubKey?.ToString() ?? "");
        WriteKeySet(channel.LocalKeySet);
        writer.Write(channel.RemoteKeySet is not null);
        if (channel.RemoteKeySet is { } remoteKeys) WriteKeySet(remoteKeys);
        writer.Write(channel.ChannelParams.Local.ToSelfDelay);
        writer.Write(channel.ChannelParams.Remote.ToSelfDelay);
        writer.Write(channel.SentCommitDiff.HasValue);
        if (channel.SentCommitDiff is { } sentDiff) WriteBytes(sentDiff);
        writer.Write(commitments.LocalCommit.Number);
        writer.Write(commitments.RemoteCommit.Number);
        writer.Write(commitments.LocalBalanceMsat);
        writer.Write(commitments.RemoteBalanceMsat);
        writer.Write(commitments.LocalNextHtlcId);
        writer.Write(commitments.RemoteNextHtlcId);
        writer.Write(commitments.RemoteNextPerCommitmentPoint?.ToString() ?? "");
        writer.Write(commitments.Params.LocalIsFunder);
        writer.Write(commitments.Params.FundingSatoshis);
        writer.Write((int)commitments.Params.Format);
        WriteParty(commitments.Params.Local);
        WriteParty(commitments.Params.Remote);
        writer.Write(commitments.Params.MaxDustHtlcExposureMsat.HasValue);
        if (commitments.Params.MaxDustHtlcExposureMsat is { } exposure) writer.Write(exposure);
        writer.Write(commitments.Params.HasInferredLimits);
        WriteFunding(commitments.Params.Funding);
        writer.Write(commitments.PendingFundings.Count);
        foreach (var funding in commitments.PendingFundings) WriteFunding(funding);
        writer.Write(commitments.RemoteNextNonces.Count);
        foreach (var nonce in commitments.RemoteNextNonces.OrderBy(n => n.Key.ToString(), StringComparer.Ordinal))
        {
            writer.Write(nonce.Key.ToString());
            writer.Write(nonce.Value.ToString());
        }
        WriteSpec(commitments.LocalCommit.Spec);
        WriteSignatures(commitments.LocalCommit.RemoteSignatures);
        writer.Write(commitments.LocalCommit.PendingFundingSignatures.Count);
        foreach (var signature in commitments.LocalCommit.PendingFundingSignatures)
        {
            writer.Write(signature.FundingTxId.ToString());
            WriteSignatures(signature.Signatures);
        }
        WriteSpec(commitments.RemoteCommit.Spec);
        writer.Write(commitments.RemoteCommit.PerCommitmentPoint.ToString());
        writer.Write(commitments.RemoteNextCommit is not null);
        if (commitments.RemoteNextCommit is { } next)
        {
            writer.Write(next.Commit.Number);
            WriteSpec(next.Commit.Spec);
            writer.Write(next.Commit.PerCommitmentPoint.ToString());
            WriteSignatures(next.SentSignatures);
            writer.Write(next.PendingFundingSignatures.Count);
            foreach (var signature in next.PendingFundingSignatures)
            {
                writer.Write(signature.FundingTxId.ToString());
                WriteSignatures(signature.Signatures);
            }
        }
        writer.Write(commitments.FeeUpdates.Count);
        foreach (var fee in commitments.FeeUpdates)
        {
            writer.Write(fee.Sequence); writer.Write(fee.FeeratePerKw); writer.Write((int)fee.State);
        }
        writer.Write(commitments.Htlcs.Count);
        foreach (var htlc in commitments.Htlcs.Values.OrderBy(h => h.Direction).ThenBy(h => h.Id))
        {
            writer.Write((int)htlc.Direction); writer.Write(htlc.Id); writer.Write(htlc.AmountMsat);
            writer.Write(htlc.PaymentHash.ToString()); writer.Write(htlc.CltvExpiry); writer.Write((int)htlc.State);
            WriteBytes(htlc.OnionRoutingPacket); writer.Write(htlc.PathKey?.ToString() ?? "");
            writer.Write(htlc.KnownPreimage is { } preimage ? Convert.ToHexString((ReadOnlySpan<byte>)preimage) : "");
            WriteBytes(htlc.WireCustomRecords);
            writer.Write(htlc.Removal is not null);
            if (htlc.Removal is { } removal)
            {
                writer.Write((int)removal.Kind);
                writer.Write(removal.PaymentPreimage is { } removedPreimage
                                 ? Convert.ToHexString((ReadOnlySpan<byte>)removedPreimage) : "");
                WriteBytes(removal.Reason); writer.Write(removal.FailureCode); WriteBytes(removal.Sha256OfOnion);
                WriteBytes(removal.AttributionData); WriteBytes(removal.FulfillmentPayload);
            }
        }
        writer.Flush();
        return new SigningWorkflowDescriptor(channel.ChannelId, kind, commitments.LocalCommit.Number,
            commitments.RemoteCommit.Number, SHA256.HashData(stream.ToArray()));

        void WriteBytes(ReadOnlyMemory<byte> value) { writer.Write(value.Length); writer.Write(value.Span); }
        void WriteKeySet(ChannelKeySetModel keys)
        {
            writer.Write(keys.KeyIndex); writer.Write(keys.FundingCompactPubKey.ToString());
            writer.Write(keys.RevocationCompactBasepoint.ToString()); writer.Write(keys.PaymentCompactBasepoint.ToString());
            writer.Write(keys.DelayedPaymentCompactBasepoint.ToString()); writer.Write(keys.HtlcCompactBasepoint.ToString());
        }
        void WriteParty(CommitmentParty party)
        {
            writer.Write(party.DustLimitSatoshis); writer.Write(party.ChannelReserveSatoshis);
            writer.Write(party.HtlcMinimumMsat); writer.Write(party.MaxAcceptedHtlcs);
            writer.Write(party.MaxHtlcValueInFlightMsat);
        }
        void WriteFunding(ChannelFunding? funding)
        {
            writer.Write(funding is not null);
            if (funding is null) return;
            writer.Write(funding.FundingTxId.ToString()); writer.Write(funding.OutputIndex);
            writer.Write(funding.CapacitySatoshis); writer.Write(funding.LocalFundingPubKey.ToString());
            writer.Write(funding.RemoteFundingPubKey.ToString()); writer.Write(funding.LocalFundingKeyIndex);
            writer.Write(funding.LocalBalanceDeltaMsat); writer.Write(funding.RemoteBalanceDeltaMsat);
            writer.Write((int)funding.Kind); writer.Write((int)funding.Status); writer.Write(funding.FundingKeysUnknown);
        }
        void WriteSpec(CommitmentSpec spec)
        {
            writer.Write((int)spec.Holder); writer.Write(spec.FeeratePerKw);
            writer.Write(spec.LocalMsat); writer.Write(spec.RemoteMsat); writer.Write(spec.Htlcs.Count);
            foreach (var htlc in spec.Htlcs)
            {
                writer.Write((int)htlc.Direction); writer.Write(htlc.Id); writer.Write(htlc.AmountMsat);
                writer.Write(htlc.PaymentHash.ToString()); writer.Write(htlc.CltvExpiry);
            }
        }
        void WriteSignatures(CommitmentSignatures? signatures)
        {
            writer.Write(signatures is not null);
            if (signatures is null) return;
            WriteBytes((ReadOnlyMemory<byte>)signatures.Signature);
            writer.Write(signatures.HtlcSignatures.Count);
            foreach (var signature in signatures.HtlcSignatures) WriteBytes((ReadOnlyMemory<byte>)signature);
            writer.Write(signatures.PartialSignature.HasValue);
            if (signatures.PartialSignature is { } partial) WriteBytes((byte[])partial);
        }
    }
}