namespace NLightning.Application.Tests.Onchain.Resolvers;

using Application.Onchain.Resolvers;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;

/// <summary>NL-966: an unresolved simple taproot output is alerted once per process, after the executor logged it.</summary>
public class UnsupportedTaprootOutputsTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x31, 32).ToArray());
    private static readonly TxId s_txId = new(Enumerable.Repeat((byte)0x32, 32).ToArray());

    [Fact]
    public void Given_AnAlertNotYetLogged_When_ReportedAgain_Then_RepeatedUntilEmittedThenSilent()
    {
        // Arrange
        var outputs = new UnsupportedTaprootOutputs();
        var first = new List<OutputResolverAction>();
        var second = new List<OutputResolverAction>();
        var third = new List<OutputResolverAction>();

        // Act: a round whose save failed (not emitted) reports again; once emitted, never again
        outputs.Report(s_channelId, s_txId, 3, OutputDescriptorKind.LocalOfferedHtlc, 20_000, first);
        outputs.Report(s_channelId, s_txId, 3, OutputDescriptorKind.LocalOfferedHtlc, 20_000, second);
        Assert.Single(second.OfType<AlertAction>()).Emitted!.Invoke();
        outputs.Report(s_channelId, s_txId, 3, OutputDescriptorKind.LocalOfferedHtlc, 20_000, third);
        outputs.Report(s_channelId, s_txId, 4, OutputDescriptorKind.LocalReceivedHtlc, 30_000, third);

        // Assert
        var alert = Assert.IsType<AlertAction>(Assert.Single(first));
        Assert.Equal("NL-966", alert.RequirementId);
        Assert.Contains("LocalOfferedHtlc", alert.Message);
        Assert.Contains(s_channelId.ToString(), alert.Message);
        Assert.Contains("4 (LocalReceivedHtlc", Assert.IsType<AlertAction>(Assert.Single(third)).Message);
    }
}
