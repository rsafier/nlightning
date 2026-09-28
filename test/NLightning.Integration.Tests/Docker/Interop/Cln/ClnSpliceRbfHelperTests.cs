using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Application.InteractiveTx;
using Domain.Channels.ValueObjects;
using Domain.Protocol.InteractiveTx;
using Domain.Serialization.Interfaces;
using Infrastructure.Serialization;

/// <summary>
/// Container-free tests of the helpers Proof SPR (c) asserts through: the IT-RBF-01 minimum it uses as its precondition
/// guard, and the <c>tx_abort</c> reason it expects from our node for a feerate below that minimum.
/// </summary>
public class ClnSpliceRbfHelperTests
{
    [Theory]
    [InlineData(253u, 278u)]
    [InlineData(600u, 625u)]
    [InlineData(1_000u, 1_041u)]
    [InlineData(24_000u, 25_000u)]
    public void Given_APreviousFeerate_When_TheProofComputesTheMinimum_Then_ItIsTheBolt2Maximum(
        uint previous, uint expected)
    {
        // Arrange
        // Act
        var minimum = ClnSpliceRbfTests.GetMinimumNextFeerate(previous);

        // Assert
        Assert.Equal(expected, minimum);
    }

    [Theory]
    [InlineData(264u)]
    [InlineData(277u)]
    public void Given_AFeerateBetween25Over24AndThePlus25Floor_When_Checked_Then_ItIsBelowTheMinimumAndRefused(
        uint feerate)
    {
        // Arrange
        const uint previous = 253;

        // Act
        var violation = InteractiveTxRbfRules.CheckFeerate(feerate, previous);

        // Assert
        Assert.True(feerate < ClnSpliceRbfTests.GetMinimumNextFeerate(previous));
        Assert.NotNull(violation);
        Assert.Equal("IT-RBF-01", violation.RequirementId);
    }

    [Fact]
    public async Task Given_OurFeerateRefusal_When_SerializedAsTxAbort_Then_TheProofReadsItsReason()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSerializationInfrastructureServices();
        var serializer = services.BuildServiceProvider().GetRequiredService<IMessageSerializer>();
        var violation = InteractiveTxRbfRules.CheckFeerate(264, 253);
        Assert.NotNull(violation);
        var abort = InteractiveTxDriver.CreateTxAbort(new ChannelId(new byte[32]), violation.Reason);
        using var stream = new MemoryStream();
        await serializer.SerializeAsync(abort, stream);

        // Act
        var reason = ClnSpliceRbfTests.TxAbortData(stream.ToArray());

        // Assert
        Assert.Contains(ClnSpliceRbfTests.FeerateRefusal(264, 253), reason, StringComparison.Ordinal);
        Assert.DoesNotContain(ClnSpliceRbfTests.FeerateRefusal(300, 253), reason, StringComparison.Ordinal);
    }
}