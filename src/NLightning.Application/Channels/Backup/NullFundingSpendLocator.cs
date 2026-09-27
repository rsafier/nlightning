namespace NLightning.Application.Channels.Backup;

using Interfaces;
using Models;

/// <summary>The <see cref="IFundingSpendLocator"/> without a chain service: it can tell nothing.</summary>
public sealed class NullFundingSpendLocator : IFundingSpendLocator
{
    public static readonly NullFundingSpendLocator Instance = new();

    private NullFundingSpendLocator()
    {
    }

    /// <inheritdoc />
    public Task<FundingSpendLocation> LocateAsync(ChannelBackupEntry entry, CancellationToken cancellationToken) =>
        Task.FromResult(new FundingSpendLocation(FundingSpendStatus.ChainUnavailable,
                                                 Error: "no chain service is registered"));
}