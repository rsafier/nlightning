using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Crypto.Hashes;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Gossip.Graph.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Onchain.Interfaces;

public static class ChannelBackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the static channel backup service, its file monitor and the restore service (singletons,
    /// idempotent). The host binds
    /// <see cref="ChannelBackupOptions"/> (<c>Node:Backup</c>) and starts/stops the <see cref="ChannelBackupMonitor"/>.
    /// Needs the node's <see cref="ISecureKeyManager"/>, <see cref="ILightningSigner"/>, the scoped
    /// <c>IUnitOfWork</c> and <see cref="IChannelMemoryRepository"/>; the restore service also the channel and peer
    /// managers, the chain monitor's <see cref="IOutpointWatcher"/>, the message factory and serializer. The restore
    /// also uses, when registered, <see cref="IOnchainChannelWatcher"/> (a funding output the peer spent before the
    /// restore), <see cref="IBitcoinChainService"/> (behind <see cref="IFundingSpendLocator"/>) and
    /// <see cref="IChannelKeyIndexReserver"/> (TryAdd'ed over the <see cref="ISecureKeyManager"/>). Both services read
    /// the peer's announced addresses from <see cref="IGraphStore"/> when it is registered (NL-431). Spliced channels
    /// (NL-478): both derive our rotated funding keys through <see cref="IChannelFundingKeySource"/> (TryAdd'ed over the
    /// signer), and the restore moves a stored recovery channel to a splice under the channel's
    /// <see cref="IChannelLockProvider"/> when one is registered.
    /// </summary>
    public static IServiceCollection AddChannelBackupServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IChannelBackupService>(sp => new ChannelBackupService(
                                                            sp.GetRequiredService<IServiceScopeFactory>(),
                                                            sp.GetRequiredService<ISecureKeyManager>(),
                                                            sp.GetRequiredService<ILightningSigner>(),
                                                            sp.GetRequiredService<IOptions<NodeOptions>>(),
                                                            sp.GetService<IOptions<ChannelBackupOptions>>(),
                                                            sp.GetService<TimeProvider>(),
                                                            sp.GetService<ILogger<ChannelBackupService>>(),
                                                            sp.GetService<IGraphStore>(),
                                                            sp.GetService<IChannelFundingKeySource>()));
        services.TryAddSingleton(sp => new ChannelBackupMonitor(sp.GetRequiredService<IChannelBackupService>(),
                                                                sp.GetRequiredService<IChannelMemoryRepository>(),
                                                                sp.GetService<IOptions<ChannelBackupOptions>>(),
                                                                sp.GetService<TimeProvider>(),
                                                                sp.GetService<ILogger<ChannelBackupMonitor>>()));
        services.TryAddSingleton<IChannelRestoreService>(sp => new ChannelRestoreService(
                                                             sp.GetRequiredService<IChannelBackupService>(),
                                                             sp.GetRequiredService<IChannelManager>(),
                                                             sp.GetRequiredService<IMessageFactory>(),
                                                             sp.GetRequiredService<IMessageSerializer>(),
                                                             sp.GetRequiredService<IOptions<NodeOptions>>(),
                                                             sp.GetRequiredService<IOutpointWatcher>(),
                                                             sp.GetRequiredService<IPeerManager>(),
                                                             sp.GetRequiredService<ISecureKeyManager>(),
                                                             sp.GetRequiredService<IServiceScopeFactory>(),
                                                             sp.GetRequiredService<ISha256>(),
                                                             sp.GetRequiredService<ILightningSigner>(),
                                                             sp.GetService<ILogger<ChannelRestoreService>>(),
                                                             sp.GetService<IFundingSpendLocator>(),
                                                             sp.GetService<IOnchainChannelWatcher>(),
                                                             sp.GetService<IChannelKeyIndexReserver>(),
                                                             sp.GetService<IGraphStore>(),
                                                             sp.GetService<IChannelMemoryRepository>(),
                                                             sp.GetService<IChannelFundingKeySource>(),
                                                             sp.GetService<IChannelLockProvider>()));
        services.TryAddSingleton<IFundingSpendLocator>(sp => sp.GetService<IBitcoinChainService>() is { } chain
                                                                 ? new ChainFundingSpendLocator(
                                                                     chain,
                                                                     sp.GetService<IOptions<ChannelBackupOptions>>(),
                                                                     sp.GetService<ILogger<ChainFundingSpendLocator>>())
                                                                 : NullFundingSpendLocator.Instance);
        services.TryAddSingleton<IChannelFundingKeySource>(sp => new SignerChannelFundingKeySource(
                                                               sp.GetRequiredService<ILightningSigner>()));
        services.TryAddSingleton<IChannelKeyIndexReserver>(sp => new SecureKeyManagerKeyIndexReserver(
                                                               sp.GetRequiredService<ISecureKeyManager>(),
                                                               sp.GetService<ILogger<SecureKeyManagerKeyIndexReserver>>()));
        return services;
    }
}