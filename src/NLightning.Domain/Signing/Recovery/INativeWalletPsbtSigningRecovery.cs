namespace NLightning.Domain.Signing.Recovery;

using Bitcoin.ValueObjects;

/// <summary>The funded wallet packet and publication terms frozen before native PSBT signing.</summary>
public sealed record NativeWalletPsbtPublicationIntent(byte[] FundedPsbt, byte[] UnsignedTransaction,
    IReadOnlyList<Guid> ReservationIds, long FeeSat, uint FeeRatePerKw, uint Height, string? Label);

/// <summary>Replays the original all-wallet PSBT signing envelope without selecting or classifying new inputs.</summary>
public interface INativeWalletPsbtSigningRecovery
{
    SignedTransaction? ReplayPsbtPublication(ISigningWorkflowScope workflow);
}