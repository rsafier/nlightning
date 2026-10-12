using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Node.PeerStorage;

using Domain.Node.PeerStorage;
using Infrastructure.Node.PeerStorage;

/// <summary>
/// Registers BOLT 1 peer storage (<c>option_provide_storage</c>).
/// </summary>
public static class PeerStorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="PeerStorageService"/> as the <see cref="IPeerStorageService"/> the peer services use
    /// (<c>PeerServiceFactory</c> resolves it; without it both messages are dropped), the node-key
    /// <see cref="IPeerStorageCipher"/> and the default <see cref="IPeerBackupBlobProvider"/>
    /// (<see cref="ChannelListPeerBackupBlobProvider"/>). All TryAdd singletons: a host that registers its own blob
    /// provider (the static channel backup) before this call keeps it. Bind <see cref="PeerStorageOptions"/> from
    /// <see cref="PeerStorageOptions.SectionName"/>; the defaults apply otherwise.
    /// </summary>
    public static IServiceCollection AddPeerStorageServices(this IServiceCollection services)
    {
        services.AddOptions<PeerStorageOptions>();
        services.TryAddSingleton<IPeerStorageCipher, PeerStorageCipher>();
        services.TryAddSingleton<IPeerBackupBlobProvider, ChannelListPeerBackupBlobProvider>();
        services.TryAddSingleton<PeerStorageService>();
        services.TryAddSingleton<IPeerStorageService>(sp => sp.GetRequiredService<PeerStorageService>());
        return services;
    }
}