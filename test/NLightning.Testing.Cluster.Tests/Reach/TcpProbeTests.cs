using System.Net;
using System.Net.Sockets;

namespace NLightning.Testing.Cluster.Tests.Reach;

using Cluster.Reach;

public class TcpProbeTests
{
    [Fact]
    public async Task Given_AHostListener_When_ProbedWithItsBanner_Then_ItSucceedsAndRecordsTheConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var listener = HostListener.Start();

        // Act
        var result = await TcpProbe.ProbeAsync("127.0.0.1", listener.Port, TimeSpan.FromSeconds(5), listener.Banner,
                                               ct);

        // Assert
        Assert.True(result.Succeeded, result.ToString());
        Assert.Equal(ProbeDirection.HostToPod, result.Direction);
        Assert.Equal(listener.Banner, result.Detail);
        Assert.Equal("127.0.0.1", result.ResolvedAddress);
        await WaitForAsync(() => listener.Accepted.Count == 1, ct);
        Assert.True(IPAddress.IsLoopback(listener.Accepted.Single().Address));
    }

    [Fact]
    public async Task Given_AnotherBanner_When_Probed_Then_ItFailsWithTheBannerSeen()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var listener = HostListener.Start(banner: "something-else");

        // Act
        var result = await TcpProbe.ProbeAsync("127.0.0.1", listener.Port, TimeSpan.FromSeconds(5), "nltg-echo", ct);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("something-else", result.Error);
    }

    [Fact]
    public async Task Given_AClosedPort_When_Probed_Then_ItFailsWithoutThrowing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        int port;
        using (var free = new TcpListener(IPAddress.Loopback, 0))
        {
            free.Start();
            port = ((IPEndPoint)free.LocalEndpoint).Port;
        }

        // Act
        var result = await TcpProbe.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(5), null, ct);

        // Assert
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Contains("FAIL", result.ToString());
    }

    [Fact]
    public async Task Given_AnUnresolvableName_When_Probed_Then_ItFailsWithoutThrowing()
    {
        // Act
        var result = await TcpProbe.ProbeAsync("nltg-no-such-host.invalid", 80, TimeSpan.FromSeconds(5), null,
                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public async Task Given_AnInvalidPort_When_Probed_Then_ItThrows(int port)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => TcpProbe.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(1), null,
                                      TestContext.Current.CancellationToken));
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(20, ct);
    }
}