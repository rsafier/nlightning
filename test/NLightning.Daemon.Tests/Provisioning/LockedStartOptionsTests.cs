using Microsoft.Extensions.Configuration;
using NLightning.Client;
using NLightning.Daemon.Configuration;
using NLightning.Daemon.Contracts.Provisioning;
using NLightning.Daemon.Utilities;

namespace NLightning.Daemon.Tests.Provisioning;

/// <summary>The locked start's options, flag and client arguments (NL-1349).</summary>
public class LockedStartOptionsTests
{
    [Fact]
    public void Given_TheLockedFlagOrTheSetting_When_Read_Then_TheStartIsLocked()
    {
        // Arrange
        var empty = new ConfigurationBuilder().Build();
        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:Startup:Locked"] = "true",
            ["Node:Startup:Provisioner"] = "stdin"
        }).Build();

        // Act / Assert
        Assert.False(StartupOptions.Read(empty, false).Locked);
        Assert.True(StartupOptions.Read(empty, DaemonUtils.IsLockedRequested(["--network", "regtest", "--locked"]))
                                  .Locked);
        Assert.True(DaemonUtils.IsLockedRequested(["--locked=true"]));
        Assert.False(DaemonUtils.IsLockedRequested(["--network", "regtest"]));
        var fromFile = StartupOptions.Read(configured, false);
        Assert.True(fromFile.Locked);
        Assert.True(fromFile.IsStdin);
        Assert.Equal("/n/provisioning/key.sock", fromFile.GetSocketPath("/n"));
    }

    [Fact]
    public void Given_TheLockedFlag_When_Normalized_Then_ItIsABareFlag()
    {
        // Act: --locked must not swallow the value after it
        var normalized = DaemonUtils.NormalizeArgs(["--locked", "-n", "regtest"]);

        // Assert
        Assert.Equal(["--locked=true", "--network", "regtest"], normalized);
    }

    [Theory]
    [InlineData("Provisioner", "vsock", "Socket or Stdin")]
    [InlineData("SocketPath", "relative/key.sock", "absolute")]
    [InlineData("FailureDelayMilliseconds", "-1", "between 0 and 60000")]
    public void Given_ABadSetting_When_Validated_Then_ItIsReported(string key, string value, string expected)
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"Node:Startup:{key}"] = value
        }).Build();

        // Act
        var errors = StartupOptions.Read(configuration, true).GetValidationErrors("/n");

        // Assert
        Assert.Contains(errors, e => e.Contains(expected));
    }

    [Fact]
    public void Given_ATooLongSocketPath_When_Validated_Then_ItIsReported()
    {
        // Act
        var errors = new StartupOptions { Locked = true }.GetValidationErrors("/" + new string('a', 120));

        // Assert
        Assert.Contains(errors, e => e.Contains("longer than"));
    }

    [Theory]
    [InlineData(new[] { "--key-file", "k" }, null)]
    [InlineData(new[] { "--key-file", "k", "--password-file", "p", "--socket", "/s" }, null)]
    [InlineData(new[] { "--key-file", "k", "--password-stdin", "--secrets-file", "s" }, null)]
    [InlineData(new[] { "--key-file", "k", "--password-file", "p", "--frame" }, null)]
    [InlineData(new[] { "--status" }, null)]
    [InlineData(new string[0], "--key-file is required")]
    [InlineData(new[] { "--status", "--key-file", "k" }, "--status takes only --socket")]
    [InlineData(new[] { "--key-file", "k", "--password-file", "p", "--password-stdin" }, "either")]
    [InlineData(new[] { "--key-file", "k", "--frame" }, "--frame needs --password-file")]
    [InlineData(new[] { "--key-file" }, "needs a value")]
    [InlineData(new[] { "--key-file", "k", "--bogus" }, "Unknown argument")]
    public void Given_UnlockArguments_When_Validated_Then_TheExpectedResult(string[] args, string? expectedError)
    {
        // Act
        var error = ClientApp.ValidateArguments("unlock", args);

        // Assert
        if (expectedError is null)
            Assert.Null(error);
        else
            Assert.Contains(expectedError, error);
    }

    [Fact]
    public async Task Given_ARequestAndAResponse_When_Framed_Then_TheyRoundTrip()
    {
        // Arrange
        var stream = new MemoryStream();
        var request = new KeyProvisioningRequest
        {
            Kind = KeyProvisioningProtocol.UnlockKind,
            Material = KeyProvisioningProtocol.EncryptedKeyFileMaterial,
            KeyFile = Convert.ToBase64String([1, 2, 3]),
            Password = "pw",
            Secrets = new KeyProvisioningSecrets { BitcoinRpcUser = "u" }
        };

        // Act
        await KeyProvisioningProtocol.WriteRequestAsync(stream, request, TestContext.Current.CancellationToken);
        await KeyProvisioningProtocol.WriteResponseAsync(stream, new KeyProvisioningResponse
        {
            Ok = true,
            State = KeyProvisioningProtocol.UnlockedState,
            NodeId = "02ab"
        }, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var read = await KeyProvisioningProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken);
        var response = await KeyProvisioningProtocol.ReadResponseAsync(stream, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(KeyProvisioningProtocol.UnlockKind, read!.Kind);
        Assert.Equal(request.KeyFile, read.KeyFile);
        Assert.Equal("pw", read.Password);
        Assert.Equal("u", read.Secrets!.BitcoinRpcUser);
        Assert.True(response.Ok);
        Assert.Equal("02ab", response.NodeId);
        Assert.Null(await KeyProvisioningProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AMalformedJsonBody_When_Read_Then_TheErrorDoesNotEchoTheBody()
    {
        // Arrange: a frame whose body is not JSON but holds a secret
        var body = "password=hunter2"u8.ToArray();
        var frame = new MemoryStream();
        frame.Write("NLKP"u8);
        frame.WriteByte(KeyProvisioningProtocol.Version);
        frame.Write([0, 0, 0, (byte)body.Length]);
        frame.Write(body);
        frame.Position = 0;

        // Act
        var e = await Assert.ThrowsAsync<InvalidDataException>(() =>
            KeyProvisioningProtocol.ReadRequestAsync(frame, TestContext.Current.CancellationToken));

        // Assert
        Assert.DoesNotContain("hunter2", e.Message);
    }
}