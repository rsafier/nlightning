using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Factories;
using NLightning.Domain.Channels.Commitments;
using NLightning.Domain.Channels.Enums;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Snapshot checks only; reserve preservation and fee-spike buffers require validated transitions.</summary>
public static class NativeChannelStaticPolicy
{
    public static void Validate(NativeChannelEnrollment enrollment, CommitmentSpec spec,
                                ulong? maximumDustExposureMsat = null)
    {
        ValidateDirection(spec, HtlcDirection.Outgoing, enrollment.Remote);
        ValidateDirection(spec, HtlcDirection.Incoming, enrollment.Local);
        var holder = spec.Holder == CommitmentSide.Local ? enrollment.Local : enrollment.Remote;
        var format = enrollment.HasAnchors ? CommitmentFormat.Anchors : CommitmentFormat.StaticRemoteKey;
        var cost = CommitmentFeeCalculator.FunderCostMsat(spec, holder.DustLimitSatoshis, format);
        var funderBalance = enrollment.IsInitiator ? spec.LocalMsat : spec.RemoteMsat;
        if (funderBalance < cost)
            throw new UnauthorizedAccessException("The funder cannot pay the independently reconstructed commitment fee and anchors.");
        if (maximumDustExposureMsat is { } maximum
         && CommitmentFeeCalculator.TrimmedHtlcTotalMsat(spec, holder.DustLimitSatoshis, format) > maximum)
            throw new UnauthorizedAccessException("Trimmed HTLC exposure exceeds the enrolled signer policy.");
    }

    private static void ValidateDirection(CommitmentSpec spec, HtlcDirection direction, NativeChannelParty receiver)
    {
        var offered = spec.Htlcs.Where(htlc => htlc.Direction == direction).ToArray();
        if (offered.Length > receiver.MaximumHtlcs
         || offered.Any(htlc => htlc.AmountMsat < receiver.HtlcMinimumMsat)
         || offered.Aggregate(0UL, (amount, htlc) => checked(amount + htlc.AmountMsat)) > receiver.MaximumInFlightMsat)
            throw new UnauthorizedAccessException("HTLC set exceeds the receiver's independently enrolled limits.");
    }
}