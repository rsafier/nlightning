using System.Net;
using System.Text;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Tests.Transport.Tor;

using Domain.Gossip.Addresses;
using Infrastructure.Transport.Tor;

public class TorControlClientTests
{
    [Theory]
    [InlineData("COOKIE,SAFECOOKIE", "AUTHCHALLENGE SAFECOOKIE")]
    [InlineData("COOKIE", "AUTHENTICATE ")]
    [InlineData("NULL", "AUTHENTICATE")]
    public async Task Given_AnAuthMethod_When_Authenticating_Then_TheStrongestUsableOneIsUsed(string methods,
                                                                                              string expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort(methods);
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);

        // Act
        var info = await client.GetProtocolInfoAsync(ct);
        await client.AuthenticateAsync(info, null, null, ct);
        var version = await client.GetInfoAsync("version", ct);

        // Assert
        Assert.Equal(tor.CookieFile, info.CookieFile);
        Assert.Equal("0.4.8.13", info.TorVersion);
        Assert.Equal(new Version(0, 4, 8, 13), info.GetNumericVersion());
        Assert.Equal("0.4.8.13", version);
        Assert.Contains(tor.Commands, c => c.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Given_APassword_When_Authenticating_Then_ItIsSentQuoted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort("HASHEDPASSWORD", "s3cret");
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        var info = await client.GetProtocolInfoAsync(ct);

        // Act
        await client.AuthenticateAsync(info, "s3cret", null, ct);

        // Assert
        Assert.Contains("AUTHENTICATE \"s3cret\"", tor.Commands);
    }

    [Fact]
    public async Task Given_AWrongPassword_When_Authenticating_Then_ItFails()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort("HASHEDPASSWORD", "s3cret");
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        var info = await client.GetProtocolInfoAsync(ct);

        // Act & Assert
        var e = await Assert.ThrowsAsync<TorControlException>(() => client.AuthenticateAsync(info, "nope", null, ct));
        Assert.Equal(515, e.Reply?.Status);
    }

    [Fact]
    public async Task Given_OnlyAPasswordMethod_When_AuthenticatingWithoutOne_Then_TheErrorNamesTheSetting()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort("HASHEDPASSWORD", "s3cret");
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        var info = await client.GetProtocolInfoAsync(ct);

        // Act & Assert
        var e = await Assert.ThrowsAsync<TorControlException>(() => client.AuthenticateAsync(info, null, null, ct));
        Assert.Contains("ControlPassword", e.Message);
    }

    [Fact]
    public async Task Given_AnotherTorsCookie_When_UsingSafeCookie_Then_TorsHashIsRefused()
    {
        // Arrange: the cookie file we read is not the one this Tor wrote
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        var otherCookie = Path.GetTempFileName();
        await File.WriteAllBytesAsync(otherCookie, new byte[32], ct);
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        var info = await client.GetProtocolInfoAsync(ct);

        try
        {
            // Act & Assert
            var e = await Assert.ThrowsAsync<TorControlException>(
                () => client.AuthenticateAsync(info, null, otherCookie, ct));
            Assert.Contains("SAFECOOKIE hash does not match", e.Message);
            Assert.DoesNotContain(tor.Commands, c => c.StartsWith("AUTHENTICATE", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(otherCookie);
        }
    }

    [Fact]
    public async Task Given_AsyncEvents_When_SendingCommands_Then_TheyAreSkipped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort { EmitEvents = true };
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);

        // Act
        var info = await client.GetProtocolInfoAsync(ct);
        await client.AuthenticateAsync(info, null, null, ct);
        var (serviceId, key) = await client.AddOnionAsync(null, 9735, "127.0.0.1:9735", ct);

        // Assert
        Assert.True(OnionV3Address.TryParse(serviceId, out _, out _));
        Assert.StartsWith("ED25519-V3:", key);
        Assert.Contains("ADD_ONION NEW:ED25519-V3 Port=9735,127.0.0.1:9735", tor.Commands);
    }

    [Fact]
    public async Task Given_ASavedKey_When_AddingTheOnion_Then_TheSameServiceComesBackWithoutANewKey()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        await client.AuthenticateAsync(await client.GetProtocolInfoAsync(ct), null, null, ct);
        var (first, key) = await client.AddOnionAsync(null, 9735, "127.0.0.1:9735", ct);
        await client.DeleteOnionAsync(first, ct);

        // Act
        var (second, noKey) = await client.AddOnionAsync(key, 9735, "127.0.0.1:9735", ct);

        // Assert
        Assert.Equal(first, second);
        Assert.Null(noKey);
        Assert.Contains($"DEL_ONION {first}", tor.Commands);
    }

    [Fact]
    public async Task Given_AnUnknownService_When_Deleted_Then_ItIsIgnored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        await client.AuthenticateAsync(await client.GetProtocolInfoAsync(ct), null, null, ct);

        // Act & Assert: 552 is not an error
        await client.DeleteOnionAsync("nosuchservice", ct);
    }

    [Fact]
    public async Task Given_AMultiLineDataReply_When_Read_Then_ItsBlockIsReturned()
    {
        // Arrange: a scripted server
        var ct = TestContext.Current.CancellationToken;
        var server = new MemoryStream(Encoding.ASCII.GetBytes(
            "650 STATUS_GENERAL NOTICE X\r\n250+config-text=\r\nSocksPort 9050\r\n..dot\r\n.\r\n250 OK\r\n"));
        var duplex = new ScriptedStream(server);
        await using var client = new TorControlClient(duplex);

        // Act
        var value = await client.GetInfoAsync("config-text", ct);

        // Assert
        Assert.Equal("SocksPort 9050\n.dot", value);
        Assert.Equal("GETINFO config-text\r\n", Encoding.ASCII.GetString(duplex.Written.ToArray()));
    }

    [Fact]
    public void Given_AProtocolInfoWithAnEscapedPath_When_Parsed_Then_ThePathIsDecoded()
    {
        // Arrange
        var reply = new TorControlReply(250, [
            "PROTOCOLINFO 1",
            "AUTH METHODS=COOKIE,SAFECOOKIE,HASHEDPASSWORD COOKIEFILE=\"/var/lib/tor/a \\\"b\\\"\\\\c\"",
            "VERSION Tor=\"0.4.9.1-alpha (git-abc)\"",
            "OK"
        ]);

        // Act
        var info = TorProtocolInfo.Parse(reply);

        // Assert
        Assert.Equal("/var/lib/tor/a \"b\"\\c", info.CookieFile);
        Assert.Equal(new[] { "COOKIE", "HASHEDPASSWORD", "SAFECOOKIE" }, info.AuthMethods.Order());
        Assert.Equal(new Version(0, 4, 9, 1), info.GetNumericVersion());
    }

    [Fact]
    public void Given_ASpecialPassword_When_Quoted_Then_QuotesAndBackslashesAreEscaped()
    {
        // Act & Assert
        Assert.Equal("\"a\\\"b\\\\c\"", TorControlClient.Quote("a\"b\\c"));
    }

    [Fact]
    public async Task Given_ACommandWithANewLine_When_Sent_Then_ItIsRefused()
    {
        // Arrange
        await using var client = new TorControlClient(new MemoryStream());

        // Act & Assert: no command can be smuggled after another
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync("GETINFO version\r\nSIGNAL HALT",
                                                                           TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A stream that reads a script and records what is written.
    /// </summary>
    private sealed class ScriptedStream(Stream script) : Stream
    {
        public MemoryStream Written { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => script.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }
}