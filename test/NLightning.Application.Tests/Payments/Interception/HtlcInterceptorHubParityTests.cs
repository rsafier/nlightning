using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Interception;

using Application.Payments.Interception;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Payments.Interception;
using Domain.Payments.Keysend;

/// <summary>
/// NL-1182: the hub's remaining LND v0.21.4 <c>InterceptableSwitch</c> rules: <c>requireinterceptor</c> (a new forward
/// without a client is refused, a replay held; a disconnect keeps the holds), on-chain holds (settle only, kept across
/// disconnects, dropped at the incoming expiry, promoted from an off-chain hold), the <c>expiry_too_far</c> check and
/// <c>RESUME_MODIFIED</c> passed through.
/// </summary>
public class HtlcInterceptorHubParityTests
{
    private static readonly Secret s_preimage = new(Enumerable.Repeat((byte)7, 32).ToArray());
    private static readonly Hash s_hash = new(SHA256.HashData((byte[])s_preimage));

    private readonly List<ForwardInterceptResolution> _resolutions = [];

    [Theory]
    [InlineData(121u, ForwardInterceptOutcome.ExpiryTooSoon)]
    [InlineData(122u, ForwardInterceptOutcome.NotIntercepted)]
    [InlineData(2147483667u, ForwardInterceptOutcome.ExpiryTooFar)]
    public void Given_NoInterceptor_When_ForwardExpiryIsChecked_Then_LndSafetyBoundsStillApply(uint expiry,
        ForwardInterceptOutcome expected)
    {
        // Arrange
        using var hub = CreateHub(require: false);
        var forward = Forward(1) with { IncomingExpiry = expiry };

        // Act
        var outcome = hub.Intercept(forward, 100, false, Record);

        // Assert
        Assert.Equal(expected, outcome);
        Assert.Equal(0, hub.HeldCount);
    }

    [Fact]
    public void Given_AnInterceptorRequiredAndNoClient_When_ANewForwardArrives_Then_ItIsRefused()
    {
        // Arrange
        var hub = CreateHub(require: true);

        // Act
        var outcome = hub.Intercept(Forward(1), 100, false, Record);

        // Assert
        Assert.True(hub.IsRequired);
        Assert.Equal(ForwardInterceptOutcome.InterceptorRequired, outcome);
        Assert.Equal(0, hub.HeldCount);
    }

    [Fact]
    public void Given_AnInterceptorRequiredAndNoClient_When_AReplayArrives_Then_ItIsHeldAndOfferedToTheNextClient()
    {
        // Arrange
        var hub = CreateHub(require: true);

        // Act
        var outcome = hub.Intercept(Forward(1), 100, true, Record);
        var client = new Client();
        using var connection = hub.Connect(client);

        // Assert
        Assert.Equal(ForwardInterceptOutcome.Held, outcome);
        Assert.Equal(1, hub.HeldCount);
        var offered = Assert.Single(client.Offered);
        Assert.Equal(500U - 19, offered.AutoFailHeight);
        Assert.False(offered.IsOnChain);
        Assert.Empty(_resolutions);
    }

    [Fact]
    public async Task Given_AnInterceptorRequired_When_TheClientDisconnects_Then_TheHoldsStayForTheNextClient()
    {
        // Arrange
        var hub = CreateHub(require: true);
        var connection = hub.Connect(new Client(), new HtlcInterceptorSettings { RequireInterceptor = true });
        hub.Intercept(Forward(1), 100, false, Record);
        hub.Intercept(Forward(2), 100, false, Record);

        // Act
        connection.Dispose();
        hub.ExpireHeld(200);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var next = new Client();
        using var again = hub.Connect(next, new HtlcInterceptorSettings { RequireInterceptor = true });

        // Assert: nothing resumed, both offered again
        Assert.Empty(_resolutions);
        Assert.Equal(2, hub.HeldCount);
        Assert.Equal(2, next.Offered.Count);
    }

    [Fact]
    public async Task Given_AnInterceptorRequiredAndNoClient_When_AHeldReplayReachesItsAutoFailHeight_Then_ItFailsBack()
    {
        // Arrange
        var hub = CreateHub(require: true);
        hub.Intercept(Forward(1), 100, true, Record);

        // Act
        hub.ExpireHeld(481);
        await WaitUntilAsync(() => hub.HeldCount == 0);

        // Assert
        Assert.Equal(ForwardInterceptAction.Fail, Assert.Single(_resolutions).Action);
    }

    [Fact]
    public void Given_NoInterceptorRequired_When_AReplayArrivesWithoutAClient_Then_ItGoesOn()
    {
        // Arrange
        var hub = CreateHub(require: false);

        // Act / Assert
        Assert.False(hub.IsRequired);
        Assert.Equal(ForwardInterceptOutcome.NotIntercepted, hub.Intercept(Forward(1), 100, true, Record));
    }

    [Fact]
    public void Given_AnAutoFailHeightBeyondLndsInt32_When_Intercepting_Then_ExpiryTooFar()
    {
        // Arrange
        var hub = CreateHub(require: false);
        using var connection = hub.Connect(new Client());

        // Act / Assert: (int.MaxValue + 19) - 19 still fits, one more does not
        Assert.Equal(ForwardInterceptOutcome.Held,
                     hub.Intercept(Forward(1) with { IncomingExpiry = (uint)int.MaxValue + 19 }, 100, false, Record));
        Assert.Equal(ForwardInterceptOutcome.ExpiryTooFar,
                     hub.Intercept(Forward(2) with { IncomingExpiry = (uint)int.MaxValue + 20 }, 100, false, Record));
    }

    [Fact]
    public async Task Given_AForwardHeldOnChain_When_ResumedOrFailed_Then_RefusedAndStillHeld()
    {
        // Arrange
        var hub = CreateHub(require: false);
        var client = new Client();
        using var connection = hub.Connect(client);

        // Act
        var held = hub.InterceptOnChain(Forward(1), Record);
        var resume = await hub.ResolveAsync(Scid, 1, ForwardInterceptResolution.Resume);
        var fail = await hub.ResolveAsync(Scid, 1, new ForwardInterceptResolution(ForwardInterceptAction.Fail));
        var modified = await hub.ResolveAsync(Scid, 1, ForwardInterceptResolution.Modified(null, null, null));

        // Assert: offered with its incoming expiry as the deadline (LND's OnChainSettleDeadline)
        Assert.True(held);
        var offered = Assert.Single(client.Offered);
        Assert.True(offered.IsOnChain);
        Assert.Equal(500U, offered.AutoFailHeight);
        Assert.Equal(InterceptResolveResult.NotAllowedOnChain, resume);
        Assert.Equal(InterceptResolveResult.NotAllowedOnChain, fail);
        Assert.Equal(InterceptResolveResult.NotAllowedOnChain, modified);
        Assert.Empty(_resolutions);
        Assert.Equal(1, hub.HeldCount);
    }

    [Fact]
    public async Task Given_AForwardHeldOnChain_When_SettledWithThePreimage_Then_TheCallbackRunsAndItIsReleased()
    {
        // Arrange
        var hub = CreateHub(require: false);
        using var connection = hub.Connect(new Client());
        hub.InterceptOnChain(Forward(1), Record);

        // Act
        var result = await hub.ResolveAsync(Scid, 1,
                                            new ForwardInterceptResolution(ForwardInterceptAction.Settle, s_preimage));

        // Assert
        Assert.Equal(InterceptResolveResult.Resolved, result);
        Assert.Equal(ForwardInterceptAction.Settle, Assert.Single(_resolutions).Action);
        Assert.Equal(0, hub.HeldCount);
    }

    [Fact]
    public async Task Given_AForwardHeldOnChain_When_TheClientDisconnects_Then_ItIsKeptAndOfferedAgain()
    {
        // Arrange
        var hub = CreateHub(require: false);
        var connection = hub.Connect(new Client());
        hub.InterceptOnChain(Forward(1), Record);

        // Act
        connection.Dispose();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var next = new Client();
        using var again = hub.Connect(next);

        // Assert: never resumed (no link flow to resume on chain)
        Assert.Empty(_resolutions);
        Assert.True(Assert.Single(next.Offered).IsOnChain);
    }

    [Fact]
    public void Given_AForwardHeldOnChain_When_TheChainReachesItsIncomingExpiry_Then_ItIsDroppedWithoutACallback()
    {
        // Arrange
        var hub = CreateHub(require: false);
        using var connection = hub.Connect(new Client());
        hub.InterceptOnChain(Forward(1), Record);

        // Act
        hub.ExpireHeld(499);
        var before = hub.HeldCount;
        hub.ExpireHeld(500);

        // Assert
        Assert.Equal(1, before);
        Assert.Equal(0, hub.HeldCount);
        Assert.Empty(_resolutions);
    }

    [Fact]
    public void Given_AnOffChainHold_When_ItsChannelGoesOnChain_Then_ItIsPromotedAndOfferedWithTheOnChainDeadline()
    {
        // Arrange
        var hub = CreateHub(require: false);
        var client = new Client();
        using var connection = hub.Connect(client);
        hub.Intercept(Forward(1), 100, false, Record);

        // Act: twice, as the resolvers raise it every round
        hub.InterceptOnChain(Forward(1), Record);
        hub.InterceptOnChain(Forward(1), Record);

        // Assert: LND notifies again only on the promotion
        Assert.Equal(2, client.Offered.Count);
        Assert.False(client.Offered[0].IsOnChain);
        Assert.True(client.Offered[1].IsOnChain);
        Assert.Equal(500U, client.Offered[1].AutoFailHeight);
        Assert.Equal(1, hub.HeldCount);
    }

    [Fact]
    public void Given_NoClientAndNoneRequired_When_AForwardGoesOnChain_Then_ItWaitsForALaterClient()
    {
        // Arrange
        var hub = CreateHub(require: false);

        // Act / Assert
        Assert.True(hub.InterceptOnChain(Forward(1), Record));
        Assert.Equal(1, hub.HeldCount);
        var client = new Client();
        using var connection = hub.Connect(client);
        Assert.True(Assert.Single(client.Offered).IsOnChain);
    }

    [Fact]
    public async Task Given_AnOffChainHoldWhoseChannelWentOnChain_When_TheClientResumes_Then_RefusedAndHeldOnChain()
    {
        // Arrange: the switch's callback finds the incoming channel on chain
        var hub = CreateHub(require: false);
        var client = new Client();
        using var connection = hub.Connect(client);
        hub.Intercept(Forward(1), 100, false,
                      _ => throw new ForwardHeldOnChainException("closing on chain"));

        // Act
        var result = await hub.ResolveAsync(Scid, 1, ForwardInterceptResolution.Resume);
        var again = await hub.ResolveAsync(Scid, 1, ForwardInterceptResolution.Resume);

        // Assert
        Assert.Equal(InterceptResolveResult.NotAllowedOnChain, result);
        Assert.Equal(InterceptResolveResult.NotAllowedOnChain, again);
        Assert.Equal(1, hub.HeldCount);
        Assert.True(client.Offered[^1].IsOnChain);
        Assert.Equal(500U, client.Offered[^1].AutoFailHeight);
    }

    [Fact]
    public async Task Given_AHeldForward_When_TheSwitchReleasesIt_Then_ItIsForgotten()
    {
        // Arrange
        var hub = CreateHub(require: false);
        using var connection = hub.Connect(new Client());
        hub.InterceptOnChain(Forward(1), Record);

        // Act
        hub.Release(Forward(1).IncomingChannelId, 1);
        var result = await hub.ResolveAsync(Scid, 1,
                                            new ForwardInterceptResolution(ForwardInterceptAction.Settle, s_preimage));

        // Assert
        Assert.Equal(0, hub.HeldCount);
        Assert.Equal(InterceptResolveResult.NotFound, result);
    }

    [Fact]
    public async Task Given_AResumeModified_When_Resolved_Then_TheCallbackGetsTheAmountsAndRecords()
    {
        // Arrange
        var hub = CreateHub(require: false);
        using var connection = hub.Connect(new Client());
        hub.Intercept(Forward(1), 100, false, Record);
        var resolution = ForwardInterceptResolution.Modified(LightningMoney.MilliSatoshis(20_000), LightningMoney.Zero,
                                                             [new CustomRecord(65_537, [1, 2])]);

        // Act
        var result = await hub.ResolveAsync(Scid, 1, resolution);

        // Assert: a zero amount means unchanged (LND)
        Assert.Equal(InterceptResolveResult.Resolved, result);
        var resolved = Assert.Single(_resolutions);
        Assert.Equal(ForwardInterceptAction.ResumeModified, resolved.Action);
        Assert.Equal(LightningMoney.MilliSatoshis(20_000), resolved.InAmount);
        Assert.Null(resolved.OutAmount);
        Assert.Equal(65_537UL, Assert.Single(resolved.OutWireCustomRecords!).Type);
    }

    [Fact]
    public void Given_ACustomRecordBelowTheMinimum_When_BuildingAResumeModified_Then_ItIsRefusedAsLndDoes()
    {
        // Act
        var exception = Assert.Throws<ArgumentException>(() => ForwardInterceptResolution.Modified(
                                                              null, null, [new CustomRecord(65_535, [1])]));

        // Assert
        Assert.StartsWith("custom records entry with TLV type below min: 65536", exception.Message);
    }

    [Fact]
    public void Given_TheOptions_When_TheHubIsBuilt_Then_ItFollowsThemBeforeAnyClient()
    {
        // Arrange
        var hub = new HtlcInterceptorHub(NullLogger<HtlcInterceptorHub>.Instance, null,
                                         Options.Create(new HtlcInterceptorSettings
                                         {
                                             RequireInterceptor = true,
                                             CltvRejectDelta = 10,
                                             CltvInterceptDelta = 12
                                         }));

        // Act
        var tooSoon = hub.Intercept(Forward(1), 489, true, Record);
        var held = hub.Intercept(Forward(2), 488, true, Record);

        // Assert
        Assert.Equal(ForwardInterceptOutcome.ExpiryTooSoon, tooSoon);
        Assert.Equal(ForwardInterceptOutcome.Held, held);
    }

    private static ShortChannelId Scid => new(150, 1, 0);

    private static HtlcInterceptorHub CreateHub(bool require) =>
        new(NullLogger<HtlcInterceptorHub>.Instance, null,
            Options.Create(new HtlcInterceptorSettings { RequireInterceptor = require }));

    private Task Record(ForwardInterceptResolution resolution)
    {
        lock (_resolutions)
            _resolutions.Add(resolution);
        return Task.CompletedTask;
    }

    private static InterceptedForward Forward(ulong htlcId) =>
        new(new ChannelId(Enumerable.Repeat((byte)1, 32).ToArray()), htlcId, Scid, new ShortChannelId(151, 1, 0),
            null, s_hash, LightningMoney.MilliSatoshis(10_100), LightningMoney.MilliSatoshis(10_000), 500, 460, 0,
            new byte[1366], []);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }

    private sealed class Client : IHtlcInterceptorClient
    {
        private readonly object _lock = new();
        private readonly List<InterceptedForward> _offered = [];

        public IReadOnlyList<InterceptedForward> Offered
        {
            get
            {
                lock (_lock)
                    return _offered.ToList();
            }
        }

        public bool TryOffer(InterceptedForward forward)
        {
            lock (_lock)
                _offered.Add(forward);
            return true;
        }
    }
}