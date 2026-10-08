using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NLightning.Daemon.Extensions;

using Domain.Signing;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Node;

public static class NodeSigningEnrollmentExtensions
{
    /// <summary>Runs before hosted services can create wallet state, register channels or accept requests.</summary>
    public static async Task ValidateNodeSigningEnrollmentAsync(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<NodeSigningEnrollmentStore>().ValidateAsync(
            scope.ServiceProvider.GetRequiredService<NodeSigningContext>());
    }

    public static Task ValidateNodeSigningEnrollmentAsync(NLightningDbContext database, NodeSigningContext context,
                                                          CancellationToken cancellationToken = default)
        => new NodeSigningEnrollmentStore(database).ValidateAsync(context, cancellationToken);
}