using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Tests.Transport.Tor;

using Domain.Gossip.Addresses;
using Domain.Node.Options;
using Infrastructure.Transport.Tor;

/// <summary>
/// Against a real Tor (C Tor 0.4.8+). Explicit: set <c>NLTG_TEST_TOR_CONTROL</c> (e.g. <c>127.0.0.1:9051</c> or
/// <c>unix:/run/tor/control</c>, cookie or no authentication; <c>NLTG_TEST_TOR_PASSWORD</c> for a password) and
/// <c>NLTG_TEST_TOR_SOCKS</c> (e.g. <c>127.0.0.1:9050</c>), then run with <c>-- xUnit.Explicit=only</c>. With
/// <c>NLTG_TEST_TOR_NETWORK=1</c> (a bootstrapped Tor with network access) the dial to our own onion service must
/// connect end to end; without it a Tor that cannot build circuits may refuse the dial, which must then come back as a
/// <see cref="Socks5Exception"/>.
/// </summary>
public sealed class TorLiveTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nltg-torlive-{Guid.NewGuid():N}");

    private static string Control => Environment.GetEnvironmentVariable("NLTG_TEST_TOR_CONTROL") ?? "127.0.0.1:9051";
    private static string Socks => Environment.GetEnvironmentVariable("NLTG_TEST_TOR_SOCKS") ?? "127.0.0.1:9050";
    private static bool HasNetwork => Environment.GetEnvironmentVariable("NLTG_TEST_TOR_NETWORK") == "1";

    [Fact(Explicit = true)]
    public async Task Given_ARealTor_When_OurOnionServiceIsRegisteredTwice_Then_ItKeepsItsAddressAndTorListsIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var options = CreateOptions((IPEndPoint)target.LocalEndpoint);

        // Act
        var first = new TorOnionService(NullLogger<TorOnionService>.Instance, Options.Create(options));
        await first.StartAsync(ct);
        await WaitForAsync(() => first.OnionHost is not null, ct);
        var host = first.OnionHost!;
        var collision = await AddSavedKeyElsewhereAsync(options, ct);
        await first.StopAsync();

        var second = new TorOnionService(NullLogger<TorOnionService>.Instance, Options.Create(options));
        await second.StartAsync(ct);
        await WaitForAsync(() => second.OnionHost is not null, ct);

        // Assert
        Assert.True(OnionV3Address.TryParse(host, out _, out _));
        Assert.Equal(host, second.OnionHost);
        Assert.Equal(550, collision.Status);
        await second.StopAsync();
    }

    [Fact(Explicit = true)]
    public async Task Given_ARealTor_When_DialingOurOwnOnionService_Then_ItConnectsOrTorsReasonComesBack()
    {
        // Arrange: our onion service in front of an echo listener
        var ct = TestContext.Current.CancellationToken;
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var echo = Task.Run(async () =>
        {
            using var client = await target.AcceptTcpClientAsync(ct);
            await client.GetStream().CopyToAsync(client.GetStream(), ct);
        }, ct);
        var options = CreateOptions((IPEndPoint)target.LocalEndpoint);
        options.Tor.ConnectTimeout = TimeSpan.FromMinutes(2);
        var service = new TorOnionService(NullLogger<TorOnionService>.Instance, Options.Create(options));
        await service.StartAsync(ct);
        await WaitForAsync(() => service.OnionHost is not null, ct);
        var dialer = new TorSocksDialer(NullLogger<TorSocksDialer>.Instance, Options.Create(options));

        try
        {
            // Act
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Tor.ConnectTimeout);
            using var client = await dialer.ConnectAsync(service.OnionHost!, options.Tor.OnionServicePort,
                                                         timeout.Token);
            await client.GetStream().WriteAsync("ping"u8.ToArray(), ct);
            var echoed = new byte[4];
            await client.GetStream().ReadExactlyAsync(echoed, ct);

            // Assert
            Assert.Equal("ping"u8.ToArray(), echoed);
        }
        catch (Exception e) when (!HasNetwork && e is Socks5Exception or OperationCanceledException)
        {
            // A Tor without circuits refuses (or never answers) through the real SOCKS5 port
            TestContext.Current.SendDiagnosticMessage($"Tor could not reach the service: {e.Message}");
        }
        finally
        {
            await service.StopAsync();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private NodeOptions CreateOptions(IPEndPoint target) => new()
    {
        ListenAddresses = [target.ToString()],
        Tor = new TorOptions
        {
            Mode = TorMode.TorOnly,
            SocksProxy = Socks,
            Control = Control,
            ControlPassword = Environment.GetEnvironmentVariable("NLTG_TEST_TOR_PASSWORD"),
            OnionServicePort = 9735,
            OnionServiceKeyFile = Path.Combine(_directory, "onion.key")
        }
    };

    /// <summary>
    /// Adds the saved key from another control connection: Tor answers 550 (address collision) while the service
    /// already runs, which proves it is registered (another connection's services are not listed by GETINFO).
    /// </summary>
    private static async Task<TorControlReply> AddSavedKeyElsewhereAsync(NodeOptions options, CancellationToken ct)
    {
        Assert.True(TorOptions.TryParseEndPoint(Control, out var endPoint));
        await using var client = await TorControlClient.ConnectAsync(endPoint, ct);
        await client.AuthenticateAsync(await client.GetProtocolInfoAsync(ct),
                                       Environment.GetEnvironmentVariable("NLTG_TEST_TOR_PASSWORD"), null, false, ct);
        var key = (await File.ReadAllTextAsync(options.Tor.OnionServiceKeyFile, ct)).Trim();
        return await client.SendAsync($"ADD_ONION {key} Port=9735,127.0.0.1:1", ct);
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for Tor");
            await Task.Delay(50, ct);
        }
    }
}