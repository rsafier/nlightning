namespace NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;

using Domain.Bitcoin.ValueObjects;
using Domain.Money;

/// <summary>Recovers node-authorized PSBT publications without publishing standalone client signatures.</summary>
public interface INativeWalletPsbtPublicationService
{
    bool HasNativePublicationRecovery { get; }
    Task<byte[]> SendOutputsCapturedAsync(IReadOnlyList<(BitcoinScript Script, LightningMoney Amount)> outputs,
        long feeRatePerKw, int minConfirmations, string label, CancellationToken cancellationToken);
    Task RecoverPublicationsAsync(CancellationToken cancellationToken);
}