using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NBitcoin;
using NLightning.Domain.Crypto.Interfaces;
using NLightning.Domain.Crypto.KeyRing;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Infrastructure.Bitcoin;
using NLightning.Infrastructure.Bitcoin.KeyRing;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeSwapJournalLifecycleTests
{
    [Fact]
    public async Task DurableExpiryDoesNotChangeCheckpointUntilAnExplicitOperation()
    {
        using var fixture = new Fixture();
        using var signer = fixture.CreateSigner();
        var session = await signer.CreateAsync(fixture.Locator, fixture.Aggregate, [], TestContext.Current.CancellationToken);
        var checkpoint = signer.GetCheckpointDigest();

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(checkpoint, signer.GetCheckpointDigest());
        Assert.Equal(Timeout.InfiniteTimeSpan, fixture.Clock.Timer!.DueTime);

        Assert.Throws<KeyNotFoundException>(() => signer.RegisterNonces(session.Id, []));
        Assert.NotEqual(checkpoint, signer.GetCheckpointDigest());
        var replacement = await signer.CreateAsync(fixture.Locator, fixture.Aggregate, [], TestContext.Current.CancellationToken);
        Assert.NotEqual(session.PublicNonce, replacement.PublicNonce);
    }

    [Fact]
    public async Task DisposalWaitsForAnActiveSessionWriteAndLeavesAReadableJournal()
    {
        using var fixture = new Fixture();
        using var signer = fixture.CreateSigner();
        using var opening = new ManualResetEventSlim();
        using var finishOpening = new ManualResetEventSlim();
        fixture.BeforeOpen = () =>
        {
            opening.Set();
            if (!finishOpening.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Session creation was not released.");
        };
        var cancellation = TestContext.Current.CancellationToken;
        var creation = Task.Run(() => signer.CreateAsync(fixture.Locator, fixture.Aggregate, [], cancellation), cancellation);
        Task? disposal = null;
        try
        {
            Assert.True(opening.Wait(TimeSpan.FromSeconds(10), cancellation));
            disposal = Task.Run(signer.Dispose, cancellation);
            Assert.True(fixture.Clock.Timer!.Disposed.Wait(TimeSpan.FromSeconds(10), cancellation));
            // Creation holds the session gate. Disposal must keep the journal open until its write finishes.
            Assert.Throws<IOException>(() =>
            {
                using var competing = new FileStream(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            });
        }
        finally { finishOpening.Set(); }
        var saved = await creation.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        await disposal!.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        fixture.BeforeOpen = null;

        using var restarted = fixture.CreateSigner();
        Assert.False(restarted.RegisterNonces(saved.Id, []));
    }

    [Fact]
    public async Task AQueuedTimerAfterDisposalCannotTouchTheJournalOrPermitFurtherSessionUse()
    {
        using var fixture = new Fixture();
        using var signer = fixture.CreateSigner();
        var session = await signer.CreateAsync(fixture.Locator, fixture.Aggregate, [], TestContext.Current.CancellationToken);
        var timer = fixture.Clock.Timer!;
        signer.Dispose();
        var committed = await File.ReadAllBytesAsync(fixture.Path, TestContext.Current.CancellationToken);

        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        timer.FireQueuedCallback();
        Assert.Equal(committed, await File.ReadAllBytesAsync(fixture.Path, TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => signer.RegisterNonces(session.Id, []));
        Assert.Throws<ObjectDisposedException>(() => signer.Cleanup(session.Id));
        Assert.Throws<ObjectDisposedException>(() => signer.GetCheckpointDigest());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "native-swap-lifecycle-" + Guid.NewGuid().ToString("N"));
        private readonly ServiceProvider _crypto = new ServiceCollection().AddBitcoinInfrastructure().BuildServiceProvider();
        private readonly ExtKey _master = ExtKey.CreateFromSeed(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        private readonly Mock<ISecureKeyManager> _keys = new();
        private readonly KeyRingOptions _options = new() { MaxSessions = 1, SessionLifetime = TimeSpan.FromMinutes(1) };
        public ControlledClock Clock { get; } = new();
        public KeyRingLocator Locator { get; } = new(99, 0);
        public string Path => System.IO.Path.Combine(_directory, "sessions");
        public Action? BeforeOpen { get; set; }
        public Domain.Crypto.Models.MusigKeyAggregate Aggregate { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            _keys.Setup(k => k.GetKeyRingKeyAtIndex(It.IsAny<int>(), It.IsAny<int>()))
                .Returns((int family, int index) =>
                {
                    if (index == Locator.Index) BeforeOpen?.Invoke();
                    return _master.Derive(new KeyPath($"1017'/0'/{family}'/0/{index}")).ToBytes();
                });
            _keys.Setup(k => k.GetKeyRingPublicKey(It.IsAny<int>(), It.IsAny<int>()))
                .Returns((int family, int index) => new CompactPubKey(_master.Derive(new KeyPath($"1017'/0'/{family}'/0/{index}")).Neuter().PubKey.ToBytes()));
            using var peer = new Key();
            Aggregate = _crypto.GetRequiredService<IMusig2Service>().AggregatePubKeys([
                _keys.Object.GetKeyRingPublicKey(Locator.Family, Locator.Index), peer.PubKey.ToBytes()]);
        }

        public SwapSigner CreateSigner() => new(_keys.Object, _crypto.GetRequiredService<IMusig2Service>(),
            _crypto.GetRequiredService<ISecp256K1Math>(), Options.Create(_options), Clock, Path);

        public void Dispose()
        {
            _crypto.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class ControlledClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public ControlledTimer? Timer { get; private set; }
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Timer = new ControlledTimer(callback, state, dueTime);
        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            if (Timer is { DueTime: var due } && due != Timeout.InfiniteTimeSpan)
                Timer.FireQueuedCallback();
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public ManualResetEventSlim Disposed { get; } = new();
        public bool Change(TimeSpan due, TimeSpan period) { DueTime = due; return true; }
        public void FireQueuedCallback() => callback(state);
        public void Dispose() => Disposed.Set();
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}