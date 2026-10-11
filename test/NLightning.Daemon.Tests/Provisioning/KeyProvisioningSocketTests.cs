using System.Buffers.Binary;
using System.Net.Sockets;
using NLightning.Client.Handlers;
using NLightning.Daemon.Contracts.Provisioning;
using NLightning.Daemon.Provisioning;

namespace NLightning.Daemon.Tests.Provisioning;

/// <summary>
/// The provisioning socket, the development provisioner and the client's <c>unlock</c> command end to end (NL-1349).
/// </summary>
public sealed class KeyProvisioningSocketTests : IDisposable
{
    private const string Password = "pässword with spaces";
    private readonly LockedNodeFiles _files = new();

    public void Dispose() => _files.Dispose();

    private string SocketPath => Path.Combine(_files.ConfigPath, "provisioning", "key.sock");

    [Fact]
    public async Task Given_ALockedNode_When_TheClientUnlocksOverTheSocket_Then_TheNodeIdIsPrintedAndTheSocketGoes()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Arrange
        var keyFile = _files.CreateKeyFile(Password, bip32: true, out var nodeId);
        var keyPath = Path.Combine(_files.OperatorPath, "node.key.json");
        await File.WriteAllBytesAsync(keyPath, keyFile, TestContext.Current.CancellationToken);
        var passwordPath = Path.Combine(_files.OperatorPath, "password");
        await File.WriteAllTextAsync(passwordPath, Password + "\n", TestContext.Current.CancellationToken);
        var logger = new ListLogger();
        var (startup, provisioner) = CreateSocketStartup(logger);
        var run = startup.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync();

        // The socket is owner-only, in an owner-only directory
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SocketPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                     File.GetUnixFileMode(Path.GetDirectoryName(SocketPath)!));

        // Before the key: the status says locked
        var statusOutput = new StringWriter();
        Assert.Equal(0, await UnlockCommands.RunAsync(["--status"], _files.ConfigPath, TextReader.Null, statusOutput,
                                                      new StringWriter(), TestContext.Current.CancellationToken));
        Assert.Contains("State: locked", statusOutput.ToString());

        // Act
        var output = new StringWriter();
        var errors = new StringWriter();
        var exitCode = await UnlockCommands.RunAsync(["--key-file", keyPath, "--password-file", passwordPath],
                                                     _files.ConfigPath, TextReader.Null, output, errors,
                                                     TestContext.Current.CancellationToken);
        var unlocked = await run;
        await provisioner.DisposeAsync();

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("State: unlocked", output.ToString());
        Assert.Contains($"Node id: {nodeId}", output.ToString());
        using var keyManager = unlocked!.KeyManager;
        Assert.False(File.Exists(SocketPath));
        Assert.DoesNotContain(Password, output + errors.ToString());
        Assert.DoesNotContain(logger.Lines, line => line.Contains(Password));
        _files.AssertNoSecretInConfigDirectory(Password);
    }

    [Fact]
    public async Task Given_ALockedNode_When_TheClientSendsAWrongPassword_Then_ItExitsOneAndTheNodeStaysLocked()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Arrange
        var keyFile = _files.CreateKeyFile(Password, bip32: false, out _);
        var keyPath = Path.Combine(_files.OperatorPath, "node.key.json");
        await File.WriteAllBytesAsync(keyPath, keyFile, TestContext.Current.CancellationToken);
        var (startup, provisioner) = CreateSocketStartup(new ListLogger());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = startup.RunAsync(stop.Token);
        await WaitForSocketAsync();

        // Act
        var errors = new StringWriter();
        var exitCode = await UnlockCommands.RunAsync(["--key-file", keyPath, "--password-stdin"], _files.ConfigPath,
                                                     new StringReader("wrong\n"), new StringWriter(), errors,
                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, exitCode);
        Assert.Contains("Refused: The key could not be opened", errors.ToString());
        Assert.Equal(KeyProvisioningProtocol.LockedState, startup.Status().State);
        Assert.True(File.Exists(SocketPath));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await provisioner.DisposeAsync();
        Assert.False(File.Exists(SocketPath));
    }

    [Fact]
    public async Task Given_AMalformedFrame_When_Sent_Then_TheEndpointAnswersAnErrorAndKeepsListening()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Arrange
        var (startup, provisioner) = CreateSocketStartup(new ListLogger());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = startup.RunAsync(stop.Token);
        await WaitForSocketAsync();

        // Act: a bad magic, then a frame that claims more than the limit
        var badMagic = await ExchangeRawAsync([0x00, 0x01, 0x02, 0x03, 0x01, 0, 0, 0, 0]);
        var oversized = new byte[9];
        "NLKP"u8.CopyTo(oversized);
        oversized[4] = KeyProvisioningProtocol.Version;
        BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(5), KeyProvisioningProtocol.MaxBodyLength + 1);
        var tooLarge = await ExchangeRawAsync(oversized);

        // Assert
        Assert.False(badMagic.Ok);
        Assert.Contains("Not a key provisioning frame", badMagic.Error);
        Assert.False(tooLarge.Ok);
        Assert.Contains("too large", tooLarge.Error);
        Assert.Equal(0, await UnlockCommands.RunAsync(["--status"], _files.ConfigPath, TextReader.Null,
                                                      new StringWriter(), new StringWriter(),
                                                      TestContext.Current.CancellationToken));
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await provisioner.DisposeAsync();
    }

    [Fact]
    public async Task Given_AGroupReadableSocketDirectory_When_Started_Then_TheEndpointRefuses()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Arrange
        var directory = Path.GetDirectoryName(SocketPath)!;
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                                              | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        var endpoint = new UnixSocketProvisioningEndpoint(SocketPath);

        // Act / Assert
        var e = await Assert.ThrowsAsync<IOException>(() => endpoint.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("owner-only", e.Message);
        Assert.False(File.Exists(SocketPath));
    }

    [Fact]
    public async Task Given_AStaleSocketFile_When_Started_Then_ItIsReplacedButAnyOtherFileIsKept()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Arrange: an empty entry nobody answers on, as a daemon that died leaves its socket (.NET unlinks a socket it
        // disposes, so the test stands in with an empty file), and a regular file with content next to it
        var directory = Path.GetDirectoryName(SocketPath)!;
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllBytesAsync(SocketPath, [], TestContext.Current.CancellationToken);
        var otherPath = Path.Combine(directory, "other.sock");
        await File.WriteAllTextAsync(otherPath, "data", TestContext.Current.CancellationToken);

        // Act
        await using var endpoint = new UnixSocketProvisioningEndpoint(SocketPath);
        await endpoint.StartAsync(TestContext.Current.CancellationToken);
        var refused = await Assert.ThrowsAsync<IOException>(() => new UnixSocketProvisioningEndpoint(otherPath)
                                                                .StartAsync(TestContext.Current.CancellationToken));

        // Assert: the new endpoint answers; the file with content is untouched
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), TestContext.Current.CancellationToken);
        Assert.Contains("is not a socket", refused.Message);
        Assert.Equal("data", await File.ReadAllTextAsync(otherPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AListeningSocket_When_ASecondEndpointStarts_Then_ItRefuses()
    {
        if (OperatingSystem.IsWindows())
            return;

        // Arrange
        await using var first = new UnixSocketProvisioningEndpoint(SocketPath);
        await first.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var e = await Assert.ThrowsAsync<IOException>(() => new UnixSocketProvisioningEndpoint(SocketPath)
                                                          .StartAsync(TestContext.Current.CancellationToken));

        // Assert: refused, and the first one's socket is still there
        Assert.Contains("Another process listens", e.Message);
        Assert.True(File.Exists(SocketPath));
    }

    [Fact]
    public async Task Given_AStdioEndpoint_When_FramesArrive_Then_StatusAndUnlockAreAnsweredOnTheOutput()
    {
        // Arrange: the request frames, as `unlock --frame` writes them
        var keyFile = _files.CreateKeyFile(Password, bip32: true, out var nodeId);
        var input = new MemoryStream();
        await KeyProvisioningProtocol.WriteRequestAsync(input, new KeyProvisioningRequest(),
                                                        TestContext.Current.CancellationToken);
        await KeyProvisioningProtocol.WriteRequestAsync(input, new KeyProvisioningRequest
        {
            Kind = KeyProvisioningProtocol.UnlockKind,
            Material = KeyProvisioningProtocol.EncryptedKeyFileMaterial,
            KeyFile = Convert.ToBase64String(keyFile),
            Password = Password
        }, TestContext.Current.CancellationToken);
        input.Position = 0;
        var output = new MemoryStream();
        LockedStartup? startup = null;
        var provisioner = new EndpointKeyProvisioner(new StdioProvisioningEndpoint(input, output),
                                                     () => startup!.Status(), new ListLogger(), null);
        startup = new LockedStartup("regtest", _files.ConfigPath, _files.Configuration(), provisioner,
                                    new ListLogger(), TimeSpan.Zero);

        // Act
        var unlocked = await startup.RunAsync(TestContext.Current.CancellationToken);
        await provisioner.DisposeAsync();

        // Assert
        using var keyManager = unlocked!.KeyManager;
        output.Position = 0;
        var status = await KeyProvisioningProtocol.ReadResponseAsync(output, TestContext.Current.CancellationToken);
        var answer = await KeyProvisioningProtocol.ReadResponseAsync(output, TestContext.Current.CancellationToken);
        Assert.Equal(KeyProvisioningProtocol.LockedState, status.State);
        Assert.True(answer.Ok);
        Assert.Equal(nodeId, answer.NodeId);
    }

    private (LockedStartup, EndpointKeyProvisioner) CreateSocketStartup(ListLogger logger)
    {
        LockedStartup? startup = null;
        var provisioner = new EndpointKeyProvisioner(new UnixSocketProvisioningEndpoint(SocketPath),
                                                     () => startup!.Status(), logger, TimeSpan.FromSeconds(30));
        startup = _files.CreateStartup(provisioner, logger);
        return (startup, provisioner);
    }

    private async Task WaitForSocketAsync()
    {
        for (var i = 0; i < 200 && !File.Exists(SocketPath); i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(SocketPath));
    }

    private async Task<KeyProvisioningResponse> ExchangeRawAsync(byte[] frame)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), TestContext.Current.CancellationToken);
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(frame, TestContext.Current.CancellationToken);
        return await KeyProvisioningProtocol.ReadResponseAsync(stream, TestContext.Current.CancellationToken);
    }
}