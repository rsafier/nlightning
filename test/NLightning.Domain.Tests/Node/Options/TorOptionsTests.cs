using System.Net;
using System.Net.Sockets;

namespace NLightning.Domain.Tests.Node.Options;

using Domain.Gossip.Addresses;
using Domain.Node.Options;

public class TorOptionsTests
{
    [Fact]
    public void Given_TheDefaults_When_Read_Then_TorIsOffAndOnionsCannotBeDialed()
    {
        // Arrange
        var options = new TorOptions();

        // Assert
        Assert.Equal(TorMode.Off, options.Mode);
        Assert.False(options.IsEnabled);
        Assert.False(options.IsOnionServiceEnabled);
        Assert.False(options.CanDial(AddressDescriptorType.TorV3));
        Assert.True(options.CanDial(AddressDescriptorType.IPv4));
        Assert.False(options.UsesProxy(AddressDescriptorType.IPv4));
        Assert.True(options.StreamIsolation);
        Assert.Empty(options.GetValidationErrors());
    }

    [Theory]
    [InlineData(TorMode.Hybrid, AddressDescriptorType.TorV3, true)]
    [InlineData(TorMode.Hybrid, AddressDescriptorType.IPv4, false)]
    [InlineData(TorMode.Hybrid, AddressDescriptorType.IPv6, false)]
    [InlineData(TorMode.Hybrid, AddressDescriptorType.Dns, false)]
    [InlineData(TorMode.TorOnly, AddressDescriptorType.TorV3, true)]
    [InlineData(TorMode.TorOnly, AddressDescriptorType.IPv4, true)]
    [InlineData(TorMode.TorOnly, AddressDescriptorType.IPv6, true)]
    [InlineData(TorMode.TorOnly, AddressDescriptorType.Dns, true)]
    [InlineData(TorMode.Off, AddressDescriptorType.Dns, false)]
    public void Given_AMode_When_Routing_Then_TheProxyIsUsedAsDocumented(TorMode mode, AddressDescriptorType type,
                                                                          bool expected)
    {
        // Arrange
        var options = new TorOptions { Mode = mode };

        // Act & Assert
        Assert.Equal(expected, options.UsesProxy(type));
        Assert.True(options.CanDial(type));
        Assert.False(options.CanDial(AddressDescriptorType.TorV2));
    }

    [Theory]
    [InlineData(TorMode.Off, null, false)]
    [InlineData(TorMode.Off, true, false)]
    [InlineData(TorMode.Hybrid, null, false)]
    [InlineData(TorMode.Hybrid, true, true)]
    [InlineData(TorMode.TorOnly, null, true)]
    [InlineData(TorMode.TorOnly, false, false)]
    public void Given_AModeAndTheSwitch_When_Read_Then_TheOnionServiceIsOnAsDocumented(TorMode mode, bool? enabled,
                                                                                       bool expected)
    {
        // Arrange
        var options = new TorOptions { Mode = mode, OnionServiceEnabled = enabled };

        // Act & Assert
        Assert.Equal(expected, options.IsOnionServiceEnabled);
    }

    [Fact]
    public void Given_EndpointForms_When_Parsed_Then_EachBecomesItsEndPointType()
    {
        // Act & Assert
        Assert.True(TorOptions.TryParseEndPoint("127.0.0.1:9050", out var ip));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 9050), ip);
        Assert.True(TorOptions.TryParseEndPoint("[::1]:9051", out var ipv6));
        Assert.Equal(new IPEndPoint(IPAddress.IPv6Loopback, 9051), ipv6);
        Assert.True(TorOptions.TryParseEndPoint("unix:/run/tor/control", out var unix));
        Assert.IsType<UnixDomainSocketEndPoint>(unix);
        Assert.True(TorOptions.TryParseEndPoint("tor:9050", out var dns));
        Assert.Equal(new DnsEndPoint("tor", 9050), dns);
    }

    [Theory]
    [InlineData("")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:0")]
    [InlineData("unix:")]
    [InlineData("::1:9050")]
    [InlineData("bad host:9050")]
    public void Given_ABadEndpoint_When_Parsed_Then_ItIsRefused(string value)
    {
        // Act & Assert
        Assert.False(TorOptions.TryParseEndPoint(value, out _));
    }

    [Theory]
    [InlineData("0.0.0.0:9735", "127.0.0.1:9735")]
    [InlineData("[::]:9736", "[::1]:9736")]
    [InlineData("127.0.0.1:9737", "127.0.0.1:9737")]
    [InlineData("192.168.1.5:9738", "192.168.1.5:9738")]
    public void Given_AListenAddress_When_TheTargetIsDerived_Then_AnAnyAddressBecomesLoopback(string listen,
                                                                                            string expected)
    {
        // Arrange
        var options = new TorOptions();

        // Act & Assert
        Assert.Equal(expected, options.GetOnionServiceTarget([listen]));
    }

    [Fact]
    public void Given_AnExplicitTarget_When_TheTargetIsDerived_Then_ItWins()
    {
        // Arrange
        var options = new TorOptions { OnionServiceTarget = "unix:/var/run/nltg.sock" };

        // Act & Assert
        Assert.Equal("unix:/var/run/nltg.sock", options.GetOnionServiceTarget(["0.0.0.0:9735"]));
    }

    [Fact]
    public void Given_TorOnlyWithBadSettings_When_Validated_Then_EveryProblemIsListed()
    {
        // Arrange
        var options = new TorOptions
        {
            Mode = TorMode.TorOnly,
            SocksProxy = "nowhere",
            Control = "",
            ConnectTimeout = TimeSpan.Zero,
            OnionServicePort = 0,
            OnionServiceKeyFile = " "
        };

        // Act
        var errors = options.GetValidationErrors([]);

        // Assert
        Assert.Contains(errors, e => e.Contains("SocksProxy"));
        Assert.Contains(errors, e => e.Contains("Control"));
        Assert.Contains(errors, e => e.Contains("ConnectTimeout"));
        Assert.Contains(errors, e => e.Contains("OnionServicePort"));
        Assert.Contains(errors, e => e.Contains("OnionServiceKeyFile"));
        Assert.Contains(errors, e => e.Contains("OnionServiceTarget"));
    }

    [Fact]
    public void Given_TorOnlyWithTheDefaults_When_ValidatedWithAListenAddress_Then_ItIsValid()
    {
        // Arrange
        var options = new TorOptions { Mode = TorMode.TorOnly };

        // Act & Assert
        Assert.Empty(options.GetValidationErrors(["127.0.0.1:9735"]));
    }

    [Theory]
    [InlineData("0.0.0.0:9735")]
    [InlineData("[::]:9735")]
    [InlineData("192.168.1.5:9735")]
    public void Given_TorOnlyListeningOffLoopback_When_Validated_Then_ItIsRefused(string listen)
    {
        // Arrange - NL-577: such a listener is reachable without Tor
        var options = new TorOptions { Mode = TorMode.TorOnly, OnionServiceTarget = "127.0.0.1:9735" };

        // Act
        var errors = options.GetValidationErrors(["127.0.0.1:9735", listen]);

        // Assert
        var error = Assert.Single(errors);
        Assert.Contains(listen, error);
        Assert.Contains("AllowClearnetListen", error);
    }

    [Theory]
    [InlineData(TorMode.TorOnly, true)]
    [InlineData(TorMode.Hybrid, false)]
    public void Given_AClearnetListenerThatIsAllowed_When_Validated_Then_ItIsValid(TorMode mode, bool allow)
    {
        // Arrange: explicitly allowed in Tor-only mode, and never a question in Hybrid mode
        var options = new TorOptions { Mode = mode, AllowClearnetListen = allow, OnionServiceEnabled = true };

        // Act & Assert
        Assert.Empty(options.GetValidationErrors(["0.0.0.0:9735"]));
    }

    [Fact]
    public void Given_AUnixSocketTarget_When_Validated_Then_ItIsRefused()
    {
        // Arrange - NL-585: the node cannot listen on a Unix socket yet, so Tor would send every connection nowhere
        var options = new TorOptions { Mode = TorMode.TorOnly, OnionServiceTarget = "unix:/var/run/nltg.sock" };

        // Act
        var errors = options.GetValidationErrors(["127.0.0.1:9735"]);

        // Assert
        var error = Assert.Single(errors);
        Assert.Contains("OnionServiceTarget", error);
        Assert.Contains("Unix socket", error);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:192.168.1.10", true)]
    [InlineData("100.64.0.1", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("203.0.113.7", false)]
    [InlineData("2001:db8::1", false)]
    public void Given_AnAddress_When_TorOnlyRoutesIt_Then_OnlyLocalNetworkAddressesAreDirect(string host, bool local)
    {
        // Arrange - NL-588: Tor refuses loopback and private targets; the provider's CGNAT space is not local
        var options = new TorOptions { Mode = TorMode.TorOnly };
        var address = IPAddress.Parse(host);
        var type = address.AddressFamily == AddressFamily.InterNetworkV6
                       ? AddressDescriptorType.IPv6
                       : AddressDescriptorType.IPv4;

        // Act & Assert
        Assert.Equal(local, TorOptions.IsLocalNetworkAddress(address));
        Assert.Equal(!local, options.UsesProxy(type, address));
        Assert.True(options.UsesProxy(type));
        Assert.False(new TorOptions { Mode = TorMode.Hybrid }.UsesProxy(type, address));
    }

    [Theory]
    [InlineData(15, 60, 30)]
    [InlineData(45, 60, 45)]
    public void Given_TheTimeouts_When_TheTorNetworkTimeoutIsDerived_Then_ItIsTheLongerOfTheTwo(
        int networkSeconds, int connectSeconds, int expectedSeconds)
    {
        // Arrange - NL-590
        var options = new TorOptions { ConnectTimeout = TimeSpan.FromSeconds(connectSeconds) };

        // Act & Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
                     options.GetNetworkTimeout(TimeSpan.FromSeconds(networkSeconds)));
    }

    [Fact]
    public void Given_NodeOptionsWithBadTorSettings_When_Validated_Then_TheTorErrorsAreIncluded()
    {
        // Arrange
        var options = new NodeOptions { Tor = new TorOptions { Mode = TorMode.Hybrid, SocksProxy = "x" } };

        // Act & Assert
        Assert.Contains(options.GetValidationErrors(), e => e.StartsWith("Tor:SocksProxy"));
    }
}