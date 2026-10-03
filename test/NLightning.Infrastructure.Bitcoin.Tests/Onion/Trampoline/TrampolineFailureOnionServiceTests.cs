using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.Trampoline;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Onion.Trampoline;

/// <summary>
/// Trampoline failure layers on random secrets. The topology names follow the PR 836 vectors: Alice pays over Bob to
/// the trampoline Carol, who routes over Dave to the trampoline Eve.
/// </summary>
public class TrampolineFailureOnionServiceTests
{
    private static readonly FailureCode s_temporaryTrampolineFailure = (FailureCode)0x2019;

    private readonly FailureOnionService _failureOnionService;
    private readonly TrampolineFailureOnionService _service;

    // Alice's outer route (Bob, Carol), Alice's trampoline route (Carol, Eve), Carol's route to Eve (Dave, Eve)
    private readonly List<Secret> _aliceOuter = CreateRoute(2);
    private readonly List<Secret> _aliceTrampoline = CreateRoute(2);
    private readonly List<Secret> _carolOuter = CreateRoute(2);

    public TrampolineFailureOnionServiceTests()
    {
        var serializer = new SpecFailureMessageSerializer();
        _failureOnionService = new FailureOnionService(serializer);
        _service = new TrampolineFailureOnionService(_failureOnionService, serializer);
    }

    [Fact]
    public void Given_RecipientTrampolineFails_When_ReturnedThroughCarol_Then_AliceFindsEveInTheTrampolineLayer()
    {
        // Arrange: Eve, Dave, Carol (unwrap + rewrap), Bob
        var message = FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(100_000_000),
                                                                      800_000);
        var packet = _service.CreateTrampolineErrorPacket(_aliceTrampoline[1], _carolOuter[1], message);
        packet = _failureOnionService.WrapErrorPacket(_carolOuter[0], packet);
        var downstream = _service.UnwrapDownstreamErrorPacket(_carolOuter, packet);
        Assert.True(downstream.MustRewrap);
        Assert.Null(downstream.Failure);
        packet = _service.WrapTrampolineErrorPacket(_aliceTrampoline[0], _aliceOuter[1],
                                                    downstream.UnwrappedPacket.Span);
        packet = _failureOnionService.WrapErrorPacket(_aliceOuter[0], packet);

        // Act
        var decrypted = _service.DecryptTrampolineErrorPacket(_aliceOuter, _aliceTrampoline, packet);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Code);
        Assert.Equal(800_000U, decrypted.Message!.Height);
    }

    [Fact]
    public void Given_IntermediateTrampolineFails_When_Decrypting_Then_AliceFindsCarolInTheTrampolineLayer()
    {
        // Arrange
        var packet = _service.CreateTrampolineErrorPacket(_aliceTrampoline[0], _aliceOuter[1],
                                                          new FailureMessage(s_temporaryTrampolineFailure,
                                                                             ReadOnlyMemory<byte>.Empty));
        packet = _failureOnionService.WrapErrorPacket(_aliceOuter[0], packet);

        // Act
        var decrypted = _service.DecryptTrampolineErrorPacket(_aliceOuter, _aliceTrampoline, packet);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(s_temporaryTrampolineFailure, decrypted.Code);
        Assert.Equal("2019", Convert.ToHexStringLower(decrypted.Failure.RawMessage.Span));
    }

    [Fact]
    public void Given_OuterHopFails_When_Decrypting_Then_AliceFindsBobInTheOuterLayer()
    {
        // Arrange
        var packet = _failureOnionService.CreateErrorPacket(_aliceOuter[0], FailureMessage.TemporaryNodeFailure());

        // Act
        var decrypted = _service.DecryptTrampolineErrorPacket(_aliceOuter, _aliceTrampoline, packet);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Outer, decrypted.Layer);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryNodeFailure, decrypted.Code);
    }

    [Fact]
    public void Given_TrampolineAnswersForItsOuterLayerOnly_When_Decrypting_Then_OuterLayerAtItsIndex()
    {
        // Arrange: Carol encrypts for the previous trampoline (Alice here) with her outer key only (e.g. mpp_timeout)
        var packet = _failureOnionService.CreateErrorPacket(_aliceOuter[1], FailureMessage.MppTimeout());
        packet = _failureOnionService.WrapErrorPacket(_aliceOuter[0], packet);

        // Act
        var decrypted = _service.DecryptTrampolineErrorPacket(_aliceOuter, _aliceTrampoline, packet);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Outer, decrypted.Layer);
        Assert.Equal(1, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.MppTimeout, decrypted.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Given_HopOfCarolsRouteFails_When_CarolUnwraps_Then_ThatHopsFailure(int erringHop)
    {
        // Arrange: Dave (0) or Eve answering for her outer layer only (1)
        var packet = _failureOnionService.CreateErrorPacket(_carolOuter[erringHop], FailureMessage.UnknownNextPeer());
        for (var i = erringHop - 1; i >= 0; i--)
            packet = _failureOnionService.WrapErrorPacket(_carolOuter[i], packet);

        // Act
        var downstream = _service.UnwrapDownstreamErrorPacket(_carolOuter, packet);

        // Assert
        Assert.False(downstream.MustRewrap);
        Assert.NotNull(downstream.Failure);
        Assert.Equal(erringHop, downstream.Failure.ErringHopIndex);
        Assert.Equal(FailureCode.UnknownNextPeer, downstream.Failure.Code);
        Assert.True(downstream.UnwrappedPacket.IsEmpty);
    }

    [Fact]
    public void Given_ThreeTrampolines_When_TheRecipientFails_Then_EachTrampolineRewrapsAndAliceFindsIt()
    {
        // Arrange: Alice -> (outer a0, a1) -> T1 -> (outer b0, b1) -> T2 -> (outer c0) -> T3
        var aliceOuter = CreateRoute(2);
        var trampoline = CreateRoute(3);
        var t1Route = CreateRoute(2);
        var t2Route = CreateRoute(1);
        var packet = _service.CreateTrampolineErrorPacket(trampoline[2], t2Route[0],
                                                          FailureMessage.TemporaryNodeFailure());

        // T2 removes its route and re-wraps for T1
        var atT2 = _service.UnwrapDownstreamErrorPacket(t2Route, packet);
        Assert.True(atT2.MustRewrap);
        packet = _service.WrapTrampolineErrorPacket(trampoline[1], t1Route[1], atT2.UnwrappedPacket.Span);
        packet = _failureOnionService.WrapErrorPacket(t1Route[0], packet);

        // T1 removes its route and re-wraps for Alice
        var atT1 = _service.UnwrapDownstreamErrorPacket(t1Route, packet);
        Assert.True(atT1.MustRewrap);
        packet = _service.WrapTrampolineErrorPacket(trampoline[0], aliceOuter[1], atT1.UnwrappedPacket.Span);
        packet = _failureOnionService.WrapErrorPacket(aliceOuter[0], packet);

        // Act
        var decrypted = _service.DecryptTrampolineErrorPacket(aliceOuter, trampoline, packet);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(TrampolineFailureLayer.Trampoline, decrypted.Layer);
        Assert.Equal(2, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryNodeFailure, decrypted.Code);
    }

    [Theory]
    [InlineData(20, 20, true, 19)]
    [InlineData(20, 20, false, 19)]
    [InlineData(1, 30, true, 29)]
    public void Given_RoutesLongerThanTheConstantIterations_When_Decrypting_Then_ErringHopFound(int outerLength,
        int trampolineLength, bool inTrampolineLayer, int erringHop)
    {
        // Arrange
        var outer = CreateRoute(outerLength);
        var trampoline = CreateRoute(trampolineLength);
        byte[] packet;
        if (inTrampolineLayer)
        {
            packet = _failureOnionService.CreateErrorPacket(trampoline[erringHop], FailureMessage.UnknownNextPeer());
            for (var i = erringHop - 1; i >= 0; i--)
                packet = _failureOnionService.WrapErrorPacket(trampoline[i], packet);

            for (var i = outerLength - 1; i >= 0; i--)
                packet = _failureOnionService.WrapErrorPacket(outer[i], packet);
        }
        else
        {
            packet = _failureOnionService.CreateErrorPacket(outer[erringHop], FailureMessage.UnknownNextPeer());
            for (var i = erringHop - 1; i >= 0; i--)
                packet = _failureOnionService.WrapErrorPacket(outer[i], packet);
        }

        // Act
        var decrypted = _service.DecryptTrampolineErrorPacket(outer, trampoline, packet);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(inTrampolineLayer ? TrampolineFailureLayer.Trampoline : TrampolineFailureLayer.Outer,
                     decrypted.Layer);
        Assert.Equal(erringHop, decrypted.ErringHopIndex);
    }

    [Fact]
    public void Given_FlippedBit_When_Decrypting_Then_NoHopMatches()
    {
        // Arrange
        var packet = _service.CreateTrampolineErrorPacket(_aliceTrampoline[0], _aliceOuter[1],
                                                          FailureMessage.TemporaryNodeFailure());
        packet = _failureOnionService.WrapErrorPacket(_aliceOuter[0], packet);
        packet[100] ^= 0x01;

        // Act
        var decrypted = _service.DecryptTrampolineErrorPacket(_aliceOuter, _aliceTrampoline, packet);

        // Assert
        Assert.Null(decrypted);
    }

    [Fact]
    public void Given_PacketShorterThanAnHmac_When_UnwrappingOrDecrypting_Then_RewrapOrNull()
    {
        // Arrange
        var packet = RandomNumberGenerator.GetBytes(20);

        // Act
        var downstream = _service.UnwrapDownstreamErrorPacket(_carolOuter, packet);
        var decrypted = _service.DecryptTrampolineErrorPacket(_aliceOuter, _aliceTrampoline, packet);

        // Assert
        Assert.True(downstream.MustRewrap);
        Assert.Equal(20, downstream.UnwrappedPacket.Length);
        Assert.Null(decrypted);
    }

    [Fact]
    public void Given_EmptyRoutes_When_UnwrappingOrDecrypting_Then_Throws()
    {
        // Arrange
        var packet = new byte[292];

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _service.UnwrapDownstreamErrorPacket([], packet));
        Assert.Throws<ArgumentException>(() => _service.DecryptTrampolineErrorPacket([], _aliceTrampoline, packet));
        Assert.Throws<ArgumentException>(() => _service.DecryptTrampolineErrorPacket(_aliceOuter, [], packet));
    }

    [Fact]
    public void Given_WrapTrampolineErrorPacket_When_Applied_Then_EqualsTrampolineThenOuterAmmag()
    {
        // Arrange
        var packet = RandomNumberGenerator.GetBytes(292);
        var expected = _failureOnionService.WrapErrorPacket(_aliceOuter[1],
                                                            _failureOnionService.WrapErrorPacket(
                                                                _aliceTrampoline[0], packet));

        // Act
        var wrapped = _service.WrapTrampolineErrorPacket(_aliceTrampoline[0], _aliceOuter[1], packet);

        // Assert
        Assert.Equal(expected, wrapped);
    }

    private static List<Secret> CreateRoute(int length)
    {
        return Enumerable.Range(0, length).Select(_ => new Secret(RandomNumberGenerator.GetBytes(32))).ToList();
    }
}