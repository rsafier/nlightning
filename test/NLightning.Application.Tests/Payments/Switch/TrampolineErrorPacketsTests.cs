namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments.Switch;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;

/// <summary>
/// NL-875: <see cref="TrampolineErrorPackets"/> builds the same reasons as <c>ITrampolineFailureOnionService</c>, with
/// <c>attribution_data</c> on the outer layer when asked (real crypto through <see cref="PaymentsTestNode"/>).
/// </summary>
public class TrampolineErrorPacketsTests : IDisposable
{
    private static readonly Secret s_outer = Enumerable.Repeat((byte)0x31, 32).ToArray();
    private static readonly Secret s_trampoline = Enumerable.Repeat((byte)0x32, 32).ToArray();
    private static readonly Secret s_payerOuterHop = Enumerable.Repeat((byte)0x33, 32).ToArray();

    private readonly PaymentsTestNode _node = new("trampoline", 0x71);

    public void Dispose() => _node.Dispose();

    [Fact]
    public void Given_AFailure_When_CreatedWithOrWithoutAttribution_Then_TheSameReasonReadAtTheTrampolineLayer()
    {
        // Arrange
        var failure = FailureMessage.TemporaryTrampolineFailure();

        // Act
        var plain = TrampolineErrorPackets.Create(_node.TrampolineFailureOnion, s_trampoline, s_outer, failure);
        var attributed = TrampolineErrorPackets.CreateAttributed(_node.FailureOnion, _node.AttributionData,
                                                                 s_trampoline, s_outer, failure, 3);

        // Assert: one reason, the attribution data of the outer layer next to it
        Assert.Equal(_node.TrampolineFailureOnion.CreateTrampolineErrorPacket(s_trampoline, s_outer, failure), plain);
        Assert.Equal(plain, attributed.Reason);
        Assert.Equal(OnionConstants.AttributionDataLength, attributed.AttributionData.Length);

        // The payer (route: one hop before us, then us; trampoline route: us) reads it at the trampoline layer once the
        // hop before us wrapped it
        var upstream = _node.FailureOnion.WrapErrorPacket(s_payerOuterHop, plain);
        var decrypted = _node.TrampolineFailureOnion.DecryptTrampolineErrorPacket([s_payerOuterHop, s_outer],
                                                                                  [s_trampoline], upstream);
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryTrampolineFailure, decrypted.Code);
    }

    [Fact]
    public void Given_ADownstreamTrampolineFailure_When_WrappedWithAttribution_Then_TheSameReasonAsTheService()
    {
        // Arrange: a packet the next trampoline node made for the origin, already unwrapped from our own route
        var downstream = _node.FailureOnion.CreateErrorPacket(Enumerable.Repeat((byte)0x34, 32).ToArray(),
                                                              FailureMessage.UnknownNextTrampoline());

        // Act
        var attributed = TrampolineErrorPackets.WrapAttributed(_node.FailureOnion, _node.AttributionData,
                                                               s_trampoline, s_outer, downstream, 2);

        // Assert
        Assert.Equal(_node.TrampolineFailureOnion.WrapTrampolineErrorPacket(s_trampoline, s_outer, downstream),
                     attributed.Reason);
        Assert.Equal(OnionConstants.AttributionDataLength, attributed.AttributionData.Length);
    }
}