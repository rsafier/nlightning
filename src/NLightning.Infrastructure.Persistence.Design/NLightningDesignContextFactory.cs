using Microsoft.EntityFrameworkCore.Design;

namespace NLightning.Infrastructure.Persistence.Design;

using Contexts;
using Factories;

/// <summary>
/// The design-time factory as <c>dotnet ef</c> looks for it: in the startup assembly. It is the persistence project's
/// <see cref="NLightningContextFactory"/>, which picks the provider from the NLIGHTNING_* variable that is set.
/// </summary>
public class NLightningDesignContextFactory : IDesignTimeDbContextFactory<NLightningDbContext>
{
    public NLightningDbContext CreateDbContext(string[] args) => new NLightningContextFactory().CreateDbContext(args);
}