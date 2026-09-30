using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Tests.Transport.Tor;

using Domain.Gossip.Addresses;
using Domain.Node.Options;
using Infrastructure.Transport.Tor;

public sealed class TorOnionServiceTests : IDisposable
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(20);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nltg-onion-{Guid.NewGuid():N}");

    private string KeyFile => Path.Combine(_directory, "tor", "onion.key");

    [Fact]
    public async Task Given_NoKeyYet_When_Started_Then_ANewServiceIsCreatedItsKeySavedAndItsAddressAnnounced()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        var service = CreateService(tor);
        var changed = 0;
        service.AnnouncedAddressesChanged += (_, _) => Interlocked.Increment(ref changed);

        // Act
        await service.StartAsync(ct);
        await WaitUntilAsync(() => service.OnionHost is not null, ct);

        // Assert
        Assert.True(OnionV3Address.TryParse(service.OnionHost, out _, out _));
        var descriptor = Assert.Single(service.GetAnnouncedAddresses());
        Assert.Equal(AddressDescriptorType.TorV3, descriptor.Type);
        Assert.Equal(service.OnionHost, descriptor.Host);
        Assert.Equal((ushort)9735, descriptor.Port);
        Assert.Equal(1, changed);
        Assert.Contains("ADD_ONION NEW:ED25519-V3 Port=9735,127.0.0.1:19735", tor.Commands);
        Assert.StartsWith("ED25519-V3:", await File.ReadAllTextAsync(KeyFile, ct));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
        Assert.Single(tor.ActiveServices);

        // Act: stopping closes the control connection, which takes the service down
        await service.StopAsync();

        // Assert
        await WaitUntilAsync(() => tor.ActiveServices.IsEmpty, ct);
    }

    [Fact]
    public async Task Given_ASavedKey_When_StartedAgain_Then_TheSameAddressComesBackFromTheSavedKey()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        var first = CreateService(tor);
        await first.StartAsync(ct);
        await WaitUntilAsync(() => first.OnionHost is not null, ct);
        await first.StopAsync();
        var key = (await File.ReadAllTextAsync(KeyFile, ct)).Trim();

        // Act
        var second = CreateService(tor);
        await second.StartAsync(ct);
        await WaitUntilAsync(() => second.OnionHost is not null, ct);

        // Assert
        Assert.Equal(first.OnionHost, second.OnionHost);
        Assert.Contains($"ADD_ONION {key} Port=9735,127.0.0.1:19735", tor.Commands);
        await second.StopAsync();
    }

    [Fact]
    public async Task Given_TorRestarts_When_TheControlConnectionDrops_Then_TheServiceIsAddedAgainWithTheSameAddress()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        var service = CreateService(tor);
        var changed = 0;
        service.AnnouncedAddressesChanged += (_, _) => Interlocked.Increment(ref changed);
        await service.StartAsync(ct);
        await WaitUntilAsync(() => service.OnionHost is not null, ct);
        var host = service.OnionHost;

        // Act
        tor.DropConnections();

        // Assert: added again after the retry delay, same address, no second announcement
        await WaitUntilAsync(() => tor.ActiveServices.Count == 1 && tor.ConnectionCount >= 2, ct);
        Assert.Equal(host, service.OnionHost);
        Assert.Equal(1, changed);
        Assert.Equal(2, tor.Commands.Count(c => c.StartsWith("ADD_ONION", StringComparison.Ordinal)));
        await service.StopAsync();
    }

    [Fact]
    public async Task Given_AControlPortThatClosesAfterEveryRegistration_When_Running_Then_ItIsNotRedialedInATightLoop()
    {
        // Arrange - NL-583: every registration succeeds and the connection closes at once
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort { CloseAfterAddOnion = true };
        var service = CreateService(tor);
        service.RetryInitialDelay = TimeSpan.FromMilliseconds(200);
        service.RetryMaxDelay = TimeSpan.FromSeconds(10);

        // Act
        await service.StartAsync(ct);
        await Task.Delay(1000, ct);
        await service.StopAsync();

        // Assert: waits of 200, 400 and 800 ms between the attempts leave room for three in a second
        Assert.InRange(tor.ConnectionCount, 1, 4);
    }

    [Fact]
    public async Task Given_AKeyFileReadableByOthers_When_Started_Then_AWarningIsLogged()
    {
        // Arrange - NL-584
        if (OperatingSystem.IsWindows())
            return;

        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        var first = CreateService(tor);
        await first.StartAsync(ct);
        await WaitUntilAsync(() => first.OnionHost is not null, ct);
        await first.StopAsync();
        File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var logger = new CapturingLogger();
        var second = CreateService(tor, logger: logger);

        // Act
        await second.StartAsync(ct);
        await WaitUntilAsync(() => second.OnionHost is not null, ct);
        await second.StopAsync();

        // Assert: still started with the same key, but the operator is told
        Assert.Equal(first.OnionHost, second.OnionHost);
        Assert.Contains(logger.Warnings, w => w.Contains("readable by other users", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_AStaleTemporaryFileWithAWideMode_When_TheKeyIsWritten_Then_TheKeyFileIsOwnerOnly()
    {
        // Arrange - NL-584: a crash left the temporary file behind with 0644
        if (OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        File.WriteAllText(KeyFile + ".tmp", "old");
        File.SetUnixFileMode(KeyFile + ".tmp", UnixFileMode.UserRead | UnixFileMode.UserWrite
                                             | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        // Act
        TorOnionKeyFile.Write(KeyFile, "ED25519-V3:" + Convert.ToBase64String(new byte[64]));

        // Assert
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
        Assert.False(TorOnionKeyFile.HasGroupOrOtherPermissions(KeyFile));
        Assert.False(File.Exists(KeyFile + ".tmp"));
    }

    [Fact]
    public async Task Given_ACorruptKeyFile_When_Started_Then_TheServiceIsNotStartedAndTheFileIsKept()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        await File.WriteAllTextAsync(KeyFile, "RSA1024:abc", ct);
        var service = CreateService(tor);

        // Act
        await service.StartAsync(ct);
        await service.Loop.WaitAsync(s_timeout, ct);

        // Assert: never a new address in place of an unreadable key
        Assert.Null(service.OnionHost);
        Assert.Empty(service.GetAnnouncedAddresses());
        Assert.DoesNotContain(tor.Commands, c => c.StartsWith("ADD_ONION", StringComparison.Ordinal));
        Assert.Equal("RSA1024:abc", await File.ReadAllTextAsync(KeyFile, ct));
        await service.StopAsync();
    }

    [Fact]
    public async Task Given_AnnouncingOff_When_TheServiceIsUp_Then_NoAddressIsAnnounced()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        var service = CreateService(tor, o => o.AnnounceOnionService = false);

        // Act
        await service.StartAsync(ct);
        await WaitUntilAsync(() => service.OnionHost is not null, ct);

        // Assert
        Assert.Empty(service.GetAnnouncedAddresses());
        await service.StopAsync();
    }

    [Fact]
    public async Task Given_TheOnionServiceOff_When_Started_Then_NothingConnects()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        var service = CreateService(tor, o => o.Mode = TorMode.Hybrid);

        // Act
        await service.StartAsync(ct);
        await service.StopAsync();

        // Assert
        Assert.Equal(0, tor.ConnectionCount);
    }

    [Fact]
    public async Task Given_TorNotRunning_When_Started_Then_TheStartDoesNotFailAndItKeepsRetrying()
    {
        // Arrange: a control port nobody listens on
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        var service = CreateService(tor, o => o.Control = "127.0.0.1:1");

        // Act
        await service.StartAsync(ct);
        await Task.Delay(200, ct);

        // Assert
        Assert.False(service.Loop.IsCompleted);
        Assert.Null(service.OnionHost);
        await service.StopAsync();
        Assert.True(service.Loop.IsCompleted);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private TorOnionService CreateService(FakeTorControlPort tor, Action<TorOptions>? configure = null,
                                          ILogger<TorOnionService>? logger = null)
    {
        var options = new NodeOptions
        {
            ListenAddresses = ["0.0.0.0:19735"],
            Tor = new TorOptions
            {
                Mode = TorMode.TorOnly,
                Control = tor.EndPoint,
                OnionServiceKeyFile = KeyFile
            }
        };
        configure?.Invoke(options.Tor);
        return new TorOnionService(logger ?? NullLogger<TorOnionService>.Instance, Options.Create(options))
        {
            RetryInitialDelay = TimeSpan.FromMilliseconds(100)
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out");

            await Task.Delay(20, ct);
        }
    }

    private sealed class CapturingLogger : ILogger<TorOnionService>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_warnings)
                    return [.. _warnings];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
                return;

            lock (_warnings)
                _warnings.Add(formatter(state, exception));
        }
    }
}