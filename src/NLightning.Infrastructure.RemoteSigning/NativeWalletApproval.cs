namespace NLightning.Infrastructure.RemoteSigning;

public sealed record NativeWalletApprovedInput(string TransactionId, uint OutputIndex,
                                               NativeWalletKeyLocator Derivation);

/// <summary>Owner-installed spending terms, independently bound to the complete immutable operation payload.</summary>
public sealed record NativeWalletApproval(Guid ReservationId, IReadOnlyList<NativeWalletApprovedInput> Inputs,
                                          NativeWalletSpendingIntent Spending);

public sealed record NativeWalletApprovedIntent(NativeSignerBinding Binding, string RequestId,
                                                string Fingerprint, long Expires, NativeWalletApproval Approval);

public interface INativeWalletApprovalStore
{
    NativeWalletApprovedIntent GetWalletApproval(NativeSignerBinding binding, NativeSignerIntent intent);
}