using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onchain;

using Domain.Bitcoin.Transactions.Interfaces;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Onchain.Interfaces;

public class OnchainServiceCollectionExtensionsTests
{
    [Fact]
    public void Given_Dependencies_When_AddOnchainBitcoinServices_Then_MapperResolvesAsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<ICommitmentTransactionModelFactory>().Object);
        services.AddSingleton(new Mock<ICommitmentTransactionBuilder>().Object);

        // Act
        services.AddOnchainBitcoinServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        // Assert
        var mapper = provider.GetRequiredService<ICommitmentOutputMapper>();
        Assert.IsType<CommitmentOutputMapper>(mapper);
        Assert.Same(mapper, provider.GetRequiredService<ICommitmentOutputMapper>());
    }
}