using Microsoft.Extensions.Hosting;

namespace NLightning.Daemon.Services;

using Application.Offers.Receive;

/// <summary>
/// Starts and stops the <see cref="ExpiredBolt12InvoicePruner"/>, which deletes expired unpaid BOLT 12 invoices
/// (NL-448).
/// </summary>
public sealed class ExpiredBolt12InvoicePruneHostedService : IHostedService
{
    private readonly ExpiredBolt12InvoicePruner _pruner;

    public ExpiredBolt12InvoicePruneHostedService(ExpiredBolt12InvoicePruner pruner)
    {
        _pruner = pruner;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _pruner.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _pruner.StopAsync();
}