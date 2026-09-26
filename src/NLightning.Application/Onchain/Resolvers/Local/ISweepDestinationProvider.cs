namespace NLightning.Application.Onchain.Resolvers.Local;

/// <summary>
/// Where a sweep of an output that pays us sends the funds (BOLT 5: "a convenient address"): a script of our wallet,
/// so the chain monitor credits the output as a deposit.
/// </summary>
public interface ISweepDestinationProvider
{
    /// <summary>The scriptPubKey of the sweep's single output.</summary>
    Task<byte[]> GetDestinationScriptAsync(CancellationToken cancellationToken);
}