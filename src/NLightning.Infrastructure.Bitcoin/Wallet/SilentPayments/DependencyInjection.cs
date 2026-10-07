using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Bitcoin.Wallet.SilentPayments;

using Crypto.SilentPayments;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Options;

public static class DependencyInjection
{
    public static IServiceCollection AddSilentPaymentBitcoinServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ISilentPaymentCrypto, SilentPaymentCrypto>();
        services.TryAddSingleton<ISilentPaymentKeySource>(sp =>
        {
            var keyManager = sp.GetRequiredService<ISecureKeyManager>();
            if (keyManager is ISilentPaymentKeySource source)
            {
                if (sp.GetRequiredService<IOptions<SilentPaymentsOptions>>().Value.Enabled && !source.RecoverableElsewhere)
                    sp.GetRequiredService<ILogger<SilentPaymentScanner>>().LogWarning(
                        "This legacy silent payment wallet can be restored with its node key file, but not by standard external silent payment wallets.");
                return source;
            }
            if (sp.GetRequiredService<IOptions<SilentPaymentsOptions>>().Value.Enabled)
                throw new InvalidOperationException("The signer does not support silent payment keys.");
            return new UnavailableKeySource();
        });
        services.TryAddSingleton<IBlockPrevoutSource>(sp => new BlockPrevoutSource(
            sp.GetRequiredService<IOptions<BitcoinOptions>>(), sp.GetRequiredService<IOptions<NodeOptions>>(),
            sp.GetRequiredService<IOptions<SilentPaymentsOptions>>().Value.PrevoutSource));
        services.TryAddSingleton<SilentPaymentScanner>();
        return services;
    }

    private sealed class UnavailableKeySource : ISilentPaymentKeySource
    {
        public bool RecoverableElsewhere => false;
        public CompactPubKey ScanPubKey => throw Unavailable();
        public CompactPubKey SpendPubKey => throw Unavailable();
        public void ComputeScanSharedSecret(ReadOnlySpan<byte> point, Span<byte> destination) => throw Unavailable();
        public void GetLabelTweak(uint label, Span<byte> destination) => throw Unavailable();
        public CompactPubKey GetLabelPoint(uint label) => throw Unavailable();
        private static InvalidOperationException Unavailable() => new("Silent payments are disabled and the signer has no silent payment keys.");
    }
}