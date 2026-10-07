using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace NLightning.Application.Bitcoin.SilentPayments;

using Domain.Bitcoin.SilentPayments.Interfaces;

public static class DependencyInjection
{
    public static IServiceCollection AddSilentPaymentApplicationServices(this IServiceCollection services)
    {
        services.TryAddSingleton<SilentPaymentService>();
        services.TryAddSingleton<ISilentPaymentService>(sp => sp.GetRequiredService<SilentPaymentService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SilentPaymentService>());
        return services;
    }
}