using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Extensions;

using Application.Accounting;
using Daemon.Extensions;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Lots;
using Domain.Accounting.Prices;
using Fixtures;

/// <summary>
/// The <c>Accounting</c> section of the default <c>appsettings.json</c> (NL-602 A3-T7): the books' switch, the sealer
/// and snapshot timers, <c>Profile</c> and <c>CostBasis</c> bind to <see cref="AccountingOptions"/> at their code
/// defaults, and <c>Accounting:Prices</c> carries every <see cref="AccountingPriceOptions"/> default, valid.
/// </summary>
public class AccountingConfigTemplateTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("mutinynet")]
    [InlineData("regtest")]
    public void Given_DefaultConfigJson_When_AccountingIsBound_Then_TheBooksAreOperationalFifoAtTheCodeDefaults(
        string network)
    {
        // Arrange
        var configuration = BuildTemplateConfiguration(network);
        var section = configuration.GetSection(AccountingOptions.SectionName);
        var defaults = new AccountingOptions();

        // Act
        var bound = section.Get<AccountingOptions>();

        // Assert: the scalar keys written are exactly these (the name-override dictionaries stay unset), plus Prices
        Assert.NotNull(bound);
        var keys = section.GetChildren().Select(c => c.Key).Order().ToList();
        Assert.Equal(["CostBasis", "Enabled", "Prices", "Profile", "SealBatchSize", "SealInterval", "SnapshotInterval"],
                     keys);
        Assert.True(bound.Enabled);
        Assert.True(bound.AreBooksEnabled);
        Assert.Equal(AccountingProfile.Operational, bound.Profile);
        Assert.Equal(defaults.Profile, bound.Profile);
        Assert.Equal(AccountingCostBasisMethod.Fifo, bound.CostBasis);
        Assert.Equal(defaults.CostBasis, bound.CostBasis);
        Assert.False(bound.IsFinancialBookEnabled);
        Assert.Equal(defaults.SealInterval, bound.SealInterval);
        Assert.Equal(defaults.SealBatchSize, bound.SealBatchSize);
        Assert.Equal(defaults.SnapshotInterval, bound.SnapshotInterval);
        Assert.Empty(bound.AccountNames);
        Assert.Empty(bound.FinancialAccountNames);
    }

    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("mutinynet")]
    [InlineData("regtest")]
    public void Given_DefaultConfigJson_When_PricesIsBound_Then_EveryKeyIsAnOptionAndEqualsItsDefault(string network)
    {
        // Arrange
        var configuration = BuildTemplateConfiguration(network);
        var section = configuration.GetSection(AccountingPriceOptions.SectionName);
        var defaults = new AccountingPriceOptions();
        var properties = typeof(AccountingPriceOptions).GetProperties().Where(p => p.CanWrite).ToList();

        // Act
        var bound = section.Get<AccountingPriceOptions>();

        // Assert: a typo would bind nothing, a missing key would hide a knob from the operator
        Assert.NotNull(bound);
        var keys = section.GetChildren().Select(c => c.Key).Order().ToList();
        Assert.Equal(properties.Select(p => p.Name).Order().ToList(), keys);
        foreach (var property in properties)
            Assert.Equal(property.GetValue(defaults), property.GetValue(bound));
        Assert.Empty(bound.GetValidationErrors());
        Assert.Equal("USD", bound.NormalizedCurrency);
        Assert.Equal(AccountingPriceSourceMode.Both, bound.Source);
        Assert.Equal(AccountingPriceOptions.DefaultUrl, bound.Url);
        Assert.Equal(AccountingPriceOptions.DefaultCsvFile, bound.CsvFile);
        Assert.Equal(TimeSpan.FromHours(26), bound.MaxAge);
    }

    [Fact]
    public void Given_DefaultConfigJson_When_TheNodeServicesBindIt_Then_TheOptionsInEffectAreTheTemplatesValues()
    {
        // Arrange
        var configuration = BuildTemplateConfiguration("mainnet");
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, NodeSigningIdentityFixture.CreateSecureKeyManager());
        using var provider = services.BuildServiceProvider();

        // Act
        var accounting = provider.GetRequiredService<IOptions<AccountingOptions>>().Value;
        var prices = provider.GetRequiredService<IOptions<AccountingPriceOptions>>().Value;

        // Assert
        Assert.Equal(AccountingProfile.Operational, accounting.Profile);
        Assert.Equal(AccountingCostBasisMethod.Fifo, accounting.CostBasis);
        Assert.Equal("USD", prices.NormalizedCurrency);
        Assert.Equal(AccountingPriceSourceMode.Both, prices.Source);
        Assert.Empty(prices.GetValidationErrors());
    }

    private static IConfigurationRoot BuildTemplateConfiguration(string network)
    {
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        return new ConfigurationBuilder()
              .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
              .Build();
    }
}