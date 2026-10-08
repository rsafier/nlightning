using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NLightning.Daemon.Extensions;

using Configuration;
using Domain.Bitcoin.Interfaces;
using Domain.Signing;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Node;

public static class NodeSigningEnrollmentExtensions
{
    /// <summary>Runs before hosted services can create wallet state, register channels or accept requests.</summary>
    public static async Task ValidateNodeSigningEnrollmentAsync(this IHost host)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        // Standard (local key file) nodes upgrade in place: their pre-enrollment database is adopted when its channels
        // belong to this key file. Remote signers (native, VLS) never adopt existing state
        var local = !SigningOptions.Read(services.GetRequiredService<IConfiguration>()).IsRemote;
        var signer = local ? services.GetRequiredService<ILightningSigner>() : null;
        await services.GetRequiredService<NodeSigningEnrollmentStore>().ValidateAsync(
            services.GetRequiredService<NodeSigningContext>(),
            signer is null ? null : signer.GetChannelBasepoints);
    }

    public static Task ValidateNodeSigningEnrollmentAsync(NLightningDbContext database, NodeSigningContext context,
                                                          CancellationToken cancellationToken = default)
        => new NodeSigningEnrollmentStore(database).ValidateAsync(context, cancellationToken);
}