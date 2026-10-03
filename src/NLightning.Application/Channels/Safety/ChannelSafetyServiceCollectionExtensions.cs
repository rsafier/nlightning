using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.Safety;

using Interfaces;

/// <summary>
/// Registers the channel safety services (BOLT2 plan N9-T2/T4): <see cref="IChannelFailureService"/>,
/// <see cref="IHtlcExpiryMonitor"/>, <see cref="IChannelErrorSender"/> and <see cref="LocalCommitmentBroadcastBuilder"/>,
/// all singletons (TryAdd, so idempotent and replaceable).
/// </summary>
/// <remarks>
/// They need the node graph (<c>AddApplicationServices</c> for <c>IChannelOperations</c>, the lock provider and the
/// payments' <c>IncomingOnionProcessor</c>; <c>AddBitcoinInfrastructure</c> for the signer, the commitment builder, the
/// chain monitor and chain service; <c>AddSerializationInfrastructureServices</c> for the message serializer; the
/// repositories for <c>IUnitOfWork</c>). The host starts them once the channels are loaded
/// (<c>IChannelFailureService.Start()</c> and <c>IHtlcExpiryMonitor.Start()</c> after <c>PeerManager.StartAsync</c>)
/// and stops the monitor before the chain monitor. The daemon binds <see cref="ChannelSafetyOptions"/> from
/// <see cref="ChannelSafetyOptions.SectionName"/>; without it the defaults apply.
/// </remarks>
public static class ChannelSafetyServiceCollectionExtensions
{
    public static IServiceCollection AddChannelSafetyServices(this IServiceCollection services)
    {
        services.AddOptions<ChannelSafetyOptions>();
        services.TryAddSingleton<LocalCommitmentBroadcastBuilder>();
        services.TryAddSingleton<IChannelErrorSender, PeerChannelErrorSender>();
        services.TryAddSingleton<ChannelFailureService>();
        services.TryAddSingleton<IChannelFailureService>(sp => sp.GetRequiredService<ChannelFailureService>());
        services.TryAddSingleton<HtlcExpiryMonitor>();
        services.TryAddSingleton<IHtlcExpiryMonitor>(sp => sp.GetRequiredService<HtlcExpiryMonitor>());
        return services;
    }
}