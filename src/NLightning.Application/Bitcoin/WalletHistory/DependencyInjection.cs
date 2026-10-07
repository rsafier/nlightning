using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace NLightning.Application.Bitcoin.WalletHistory;

using Domain.Bitcoin.Wallet.Interfaces;

public static class DependencyInjection
{
    public static IServiceCollection AddWalletHistoryApplicationServices(this IServiceCollection services)
    {
        services.TryAddSingleton<WalletHistoryService>();
        services.TryAddSingleton<IWalletHistoryService>(provider => provider.GetRequiredService<WalletHistoryService>());
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<WalletHistoryService>());
        return services;
    }
}