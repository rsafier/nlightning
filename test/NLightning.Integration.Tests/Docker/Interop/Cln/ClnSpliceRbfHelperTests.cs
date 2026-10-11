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
/// Tagged <c>Interop.Cln</c> so the CLN suite runs them: their names hold "Docker", which CI's
/// <c>FullyQualifiedName!~Docker</c> leaves out (NL-816).
/// </summary>
[Trait("Category", ClnInteropCollection.Category)]
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

    /// <summary>
    /// NL-522: CLN's first splice paid 3,178 sat (about 2,650 sat/kw of its weight) while its <c>splice_init</c> named a
    /// much lower feerate; a 1,000 sat/kw bump then paid 1,202 sat and was refused. The proof's bump pays more than the
    /// first fee plus BIP 125's incremental relay fee, and never less than the IT-RBF-01 floor or 1,000 sat/kw.
    /// </summary>
    [Theory]
    [InlineData(253u, 3_178ul, 1_196ul)]
    [InlineData(253u, 300ul, 1_196ul)]
    [InlineData(2_500u, 3_178ul, 1_196ul)]
    [InlineData(10_000u, 3_178ul, 1_196ul)]
    public void Given_ClnsFirstAttempt_When_TheProofPicksTheBumpFeerate_Then_ItBeatsTheFeeAndTheFloor(
        uint firstFeerate, ulong firstFee, ulong firstWeight)
    {
        // Arrange
        var vsize = (firstWeight + 3) / 4;

        // Act
        var feerate = ClnSpliceRbfTests.GetClnBumpFeerate(firstFeerate, firstFee, firstWeight);

        // Assert: at the first attempt's weight the bump pays at least its fee plus 1 sat/vB
        Assert.True(feerate >= 1_000);
        Assert.True(feerate >= ClnSpliceRbfTests.GetMinimumNextFeerate(firstFeerate));
        Assert.True(feerate * firstWeight / 1_000 >= firstFee + vsize,
                    $"{feerate} sat/kw pays {feerate * firstWeight / 1_000} sat, not more than {firstFee} + {vsize}");
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