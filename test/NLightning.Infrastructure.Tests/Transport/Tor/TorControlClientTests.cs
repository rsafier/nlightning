using System.Net;
using System.Text;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Tests.Transport.Tor;

using Domain.Gossip.Addresses;
using Infrastructure.Transport.Tor;

public class TorControlClientTests
{
    /// <summary>A canonical base32 x25519 client authorization key (32 zero bytes).</summary>
    private static readonly string s_clientAuthKeyA = new('a', 52);

    /// <summary>Another one (32 bytes, the first 2).</summary>
    private static readonly string s_clientAuthKeyB = "ai" + new string('a', 50);

    [Theory]
    [InlineData("COOKIE,SAFECOOKIE", false, "AUTHCHALLENGE SAFECOOKIE")]
    [InlineData("NULL,COOKIE,SAFECOOKIE", false, "AUTHCHALLENGE SAFECOOKIE")]
    [InlineData("NULL", true, "AUTHENTICATE")]
    public async Task Given_AnAuthMethod_When_Authenticating_Then_TheStrongestUsableOneIsUsed(
        string methods, bool allowUnauthenticated, string expected)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort(methods);
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);

        // Act
        var info = await client.GetProtocolInfoAsync(ct);
        await client.AuthenticateAsync(info, null, null, allowUnauthenticated, ct);
        var version = await client.GetInfoAsync("version", ct);

        // Assert
        Assert.Equal(tor.CookieFile, info.CookieFile);
        Assert.Equal("0.4.8.13", info.TorVersion);
        Assert.Equal(new Version(0, 4, 8, 13), info.GetNumericVersion());
        Assert.Equal("0.4.8.13", version);
        Assert.Contains(tor.Commands, c => c.StartsWith(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("COOKIE", "SAFECOOKIE")]
    [InlineData("NULL", "AllowUnauthenticatedControlPort")]
    public async Task Given_AControlPortThatCannotProveItIsTor_When_Authenticating_Then_NothingIsSent(
        string methods, string expectedInMessage)
    {
        // Arrange - NL-575: plain COOKIE hands the file's bytes to whatever listens (a fake port could name any
        // 32-byte file), and NULL proves nothing at all
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort(methods);
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        var info = await client.GetProtocolInfoAsync(ct);

        // Act
        var e = await Assert.ThrowsAsync<TorControlException>(
            () => client.AuthenticateAsync(info, null, null, false, ct));

        // Assert
        Assert.Contains(expectedInMessage, e.Message);
        Assert.DoesNotContain(tor.Commands, c => c.StartsWith("AUTHENTICATE", StringComparison.Ordinal));
        Assert.DoesNotContain(tor.Commands, c => c.StartsWith("AUTHCHALLENGE", StringComparison.Ordinal));
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
        await client.AuthenticateAsync(info, "s3cret", null, false, ct);

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
        var e = await Assert.ThrowsAsync<TorControlException>(
            () => client.AuthenticateAsync(info, "nope", null, false, ct));
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
        var e = await Assert.ThrowsAsync<TorControlException>(
            () => client.AuthenticateAsync(info, null, null, false, ct));
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
                () => client.AuthenticateAsync(info, null, otherCookie, false, ct));
            Assert.Contains("SAFECOOKIE hash does not match", e.Message);
            Assert.DoesNotContain(tor.Commands, c => c.StartsWith("AUTHENTICATE", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(otherCookie);
        }
    }

    [Fact]
    public async Task Given_ASyncEvents_When_SendingCommands_Then_TheyAreSkipped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort { EmitEvents = true };
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);

        // Act
        var info = await client.GetProtocolInfoAsync(ct);
        await client.AuthenticateAsync(info, null, null, false, ct);
        var (serviceId, key) = await client.AddOnionAsync(null, 9735, "127.0.0.1:9735", ct);

        // Assert
        Assert.True(OnionV3Address.TryParse(serviceId, out _, out _));
        Assert.StartsWith("ED25519-V3:", key);
        Assert.Contains("ADD_ONION NEW:ED25519-V3 Port=9735,127.0.0.1:9735", tor.Commands);
    }

    [Fact]
    public async Task Given_ClientAuthKeys_When_AddingTheOnion_Then_V3AuthAndEveryKeyGoOnTheCommand()
    {
        // Arrange - NL-573: a private service
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        await client.AuthenticateAsync(await client.GetProtocolInfoAsync(ct), null, null, false, ct);

        // Act
        await client.AddOnionAsync(null, 9735, "127.0.0.1:9735", ct, [s_clientAuthKeyA, s_clientAuthKeyB]);

        // Assert
        Assert.Contains("ADD_ONION NEW:ED25519-V3 Port=9735,127.0.0.1:9735 Flags=V3Auth "
                      + $"ClientAuthV3={s_clientAuthKeyA} ClientAuthV3={s_clientAuthKeyB}", tor.Commands);
    }

    [Fact]
    public async Task Given_PowOptions_When_AddingTheOnion_Then_TheyGoOnTheCommand()
    {
        // Arrange - NL-573
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        await client.AuthenticateAsync(await client.GetProtocolInfoAsync(ct), null, null, false, ct);

        // Act
        await client.AddOnionAsync(null, 9735, "127.0.0.1:9735", ct,
                                   pow: new TorOnionPoWOptions(true, QueueRate: 250, QueueBurst: 3000));

        // Assert
        Assert.Contains("ADD_ONION NEW:ED25519-V3 Port=9735,127.0.0.1:9735 PoWDefensesEnabled=1 PoWQueueRate=250 "
                      + "PoWQueueBurst=3000", tor.Commands);
    }

    [Fact]
    public async Task Given_PowWithoutAuthOrTuning_When_AddingTheOnion_Then_OnlyTheSwitchIsSent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        await client.AuthenticateAsync(await client.GetProtocolInfoAsync(ct), null, null, false, ct);

        // Act
        await client.AddOnionAsync(null, 9735, "127.0.0.1:9735", ct, pow: new TorOnionPoWOptions(false));

        // Assert
        Assert.Contains("ADD_ONION NEW:ED25519-V3 Port=9735,127.0.0.1:9735 PoWDefensesEnabled=0", tor.Commands);
    }

    [Fact]
    public async Task Given_ASavedKey_When_AddingTheOnion_Then_TheSameServiceComesBackWithoutANewKey()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var tor = new FakeTorControlPort();
        await using var client = await TorControlClient.ConnectAsync(IPEndPoint.Parse(tor.EndPoint), ct);
        await client.AuthenticateAsync(await client.GetProtocolInfoAsync(ct), null, null, false, ct);
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
        await client.AuthenticateAsync(await client.GetProtocolInfoAsync(ct), null, null, false, ct);

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

    [Fact]
    public void Given_AnAddOnionReplyWithAPrivateKey_When_ItIsShown_Then_TheKeyIsRedacted()
    {
        // Arrange - NL-581: a failed exchange puts the reply in the exception message, which is logged
        var reply = new TorControlReply(250, ["ServiceID=abc", "PrivateKey=ED25519-V3:c2VjcmV0a2V5", "OK"]);
        var failed = new TorControlException("ADD_ONION returned no ServiceID", reply);

        // Act
        var text = reply.ToString();

        // Assert
        Assert.DoesNotContain("c2VjcmV0a2V5", text);
        Assert.DoesNotContain("c2VjcmV0a2V5", failed.Message);
        Assert.Contains("PrivateKey=[redacted]", text);
        Assert.Contains("ServiceID=abc", text);
        Assert.Equal("ADD_ONION ED25519-V3:[redacted] Port=9735",
                     TorControlReply.Redact("ADD_ONION ED25519-V3:c2VjcmV0a2V5 Port=9735"));
    }

    [Fact]
    public async Task Given_AMalformedLineWithAKey_When_Read_Then_TheErrorHidesTheKey()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var client = new TorControlClient(new ScriptedStream(new MemoryStream(
            Encoding.ASCII.GetBytes("2x0-PrivateKey=ED25519-V3:c2VjcmV0a2V5\r\n"))));

        // Act
        var e = await Assert.ThrowsAsync<TorControlException>(() => client.SendAsync("GETINFO version", ct));

        // Assert
        Assert.DoesNotContain("c2VjcmV0a2V5", e.Message);
    }

    [Fact]
    public async Task Given_ACommandCancelledMidReply_When_TheNextCommandIsSent_Then_ItFailsInsteadOfReadingTheStaleReply()
    {
        // Arrange - NL-582: the first reply arrives only after the first command was given up
        var ct = TestContext.Current.CancellationToken;
        var server = new GatedStream();
        await using var client = new TorControlClient(server);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = client.SendAsync("GETINFO version", cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        server.Release("250-version=stale\r\n250 OK\r\n250-version=fresh\r\n250 OK\r\n");

        // Act
        var e = await Assert.ThrowsAsync<TorControlException>(() => client.GetInfoAsync("version", ct));

        // Assert
        Assert.True(client.IsFaulted);
        Assert.Contains("interrupted", e.Message);
    }

    [Fact]
    public async Task Given_AnEndlessLine_When_Read_Then_ItIsRefusedOnceItPassesTheLimit()
    {
        // Arrange - NL-589: a line without a newline is refused as it grows, not after it was all buffered
        var ct = TestContext.Current.CancellationToken;
        var endless = new EndlessStream();
        await using var client = new TorControlClient(endless);

        // Act
        var e = await Assert.ThrowsAsync<TorControlException>(() => client.SendAsync("GETINFO version", ct));

        // Assert
        Assert.Contains("too long", e.Message);
        Assert.InRange(endless.BytesRead, 64 * 1024, 64 * 1024 + 8192);
    }

    [Fact]
    public async Task Given_RepliesSplitAcrossReads_When_Read_Then_TheLinesAreJoined()
    {
        // Arrange: one byte per read, CRLF and bare LF endings
        var ct = TestContext.Current.CancellationToken;
        await using var client = new TorControlClient(new ScriptedStream(new OneByteStream(
            Encoding.ASCII.GetBytes("250-version=0.4.8.13\n250 OK\r\n"))));

        // Act
        var version = await client.GetInfoAsync("version", ct);

        // Assert
        Assert.Equal("0.4.8.13", version);
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

    /// <summary>
    /// A stream whose reads wait until <see cref="Release"/> gives them bytes; a cancelled read gives up.
    /// </summary>
    private sealed class GatedStream : Stream
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly MemoryStream _data = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Release(string text)
        {
            _data.Write(Encoding.ASCII.GetBytes(text));
            _data.Position = 0;
            _released.TrySetResult();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _released.Task.WaitAsync(cancellationToken);
            return _data.Read(buffer.Span);
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    /// <summary>
    /// A stream that reads 'x' forever and counts it.
    /// </summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)'x');
            BytesRead += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    /// <summary>
    /// A stream over <paramref name="data"/> that returns one byte per read.
    /// </summary>
    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    }
}