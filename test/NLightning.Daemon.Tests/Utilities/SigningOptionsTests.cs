using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Utilities;

using Daemon.Configuration;
using Daemon.Extensions;
using Daemon.Utilities;
using Domain.Protocol.Interfaces;

public class SigningOptionsTests
{
    [Fact]
    public void Given_NoSigningSection_When_Read_Then_LocalModeIsSelected()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().Build();

        // Act
        var options = SigningOptions.Read(configuration);

        // Assert
        Assert.False(options.IsRemote);
    }

    [Theory]
    [InlineData("Signing:Mode", "UnknownSigner")]
    [InlineData("Signing:SocketPath", "signer.sock")]
    [InlineData("Signing:AuthTokenFile", "token")]
    [InlineData("Signing:TimeoutSeconds", "0")]
    [InlineData("Signing:ExpectedNodePublicKey", "bad")]
    public void Given_InvalidRemoteConfiguration_When_Read_Then_ItFails(string key, string value)
    {
        // Arrange
        var values = new Dictionary<string, string?>
        {
            ["Signing:Mode"] = "RemoteNative",
            ["Signing:SocketPath"] = "/tmp/signer.sock",
            ["Signing:AuthTokenFile"] = "/tmp/signer.token"
        };
        values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => SigningOptions.Read(configuration));
    }

    [Fact]
    public void Given_RemoteModeWithoutAConnection_When_Composed_Then_ItCannotFallBackToLocalSigning()
    {
        // Arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Signing:Mode"] = "RemoteNative",
            ["Signing:SocketPath"] = "/tmp/signer.sock",
            ["Signing:AuthTokenFile"] = "/tmp/signer.token"
        }).Build();
        var services = new ServiceCollection();

        // Act / Assert
        Assert.Throws<ArgumentException>(() =>
            services.AddNltgNodeServices(configuration, Mock.Of<ISecureKeyManager>()));
    }

    [Fact]
    public void Given_RemoteConfigurationWithMissingSocketAndToken_When_Checked_Then_NoConnectionOrKeyFileIsNeeded()
    {
        // Arrange
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            NodeConfigurationExtensions.CreateDefaultConfigJson("regtest")));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream)
           .AddInMemoryCollection(new Dictionary<string, string?>
           {
               ["Signing:Mode"] = "RemoteNative",
               ["Signing:SocketPath"] = "/nonexistent/nltg-signer.sock",
               ["Signing:AuthTokenFile"] = "/nonexistent/nltg-signer.token"
           }).Build();

        // Act
        var failures = ConfigurationCheck.Run(configuration, "regtest");

        // Assert
        Assert.Empty(failures);
    }
}