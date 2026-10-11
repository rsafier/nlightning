using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Daemon.Extensions;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Signing;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeContextCompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KeyManagerFromAnotherEnrollmentIsRejectedBeforeRegistrationOrAllocation(bool sameSeed)
    {
        await using var supervisor = new HostedNativeSignerSupervisor();
        var first = await supervisor.AddAsync("first", new string('a', 64));
        var second = await supervisor.AddAsync("second", new string(sameSeed ? 'a' : 'b', 64));
        var firstOptions = first.Options();
        var secondOptions = second.Options();
        firstOptions.TimeoutSeconds = secondOptions.TimeoutSeconds = 15;
        using var a = new RemoteSignerConnection(firstOptions);
        using var b = new RemoteSignerConnection(secondOptions);
        var aKeys = new RemoteSecureKeyManager(a);
        var bKeys = new RemoteSecureKeyManager(b);
        Assert.Equal(sameSeed, aKeys.GetNodePubKey() == bKeys.GetNodePubKey());
        Assert.NotEqual(aKeys.Context, bKeys.Context);
        var firstIndex = aKeys.ReserveChannelKeyIndex();
        var secondIndex = bKeys.ReserveChannelKeyIndex();
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddNltgNodeServices(Configuration(first), bKeys, a));

        Assert.Empty(services);
        Assert.Equal(firstIndex + 1, aKeys.ReserveChannelKeyIndex());
        Assert.Equal(secondIndex + 1, bKeys.ReserveChannelKeyIndex());
    }

    [Theory]
    [InlineData("Signing:NodeId", "another-node")]
    [InlineData("Signing:OwnerId", "another-owner")]
    [InlineData("Signing:SignerId", "another-signer")]
    [InlineData("Node:Network", "mainnet")]
    public async Task ConfiguredContextMismatchIsRejectedAtComposition(string field, string value)
    {
        await using var supervisor = new HostedNativeSignerSupervisor();
        var process = await supervisor.AddAsync("configured", new string('a', 64));
        using var connection = new RemoteSignerConnection(process.Options());
        var configuration = Configuration(process);
        configuration[field] = value;
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddNltgNodeServices(configuration,
            new RemoteSecureKeyManager(connection), connection));

        Assert.Empty(services);
    }

    [Fact]
    public async Task SeparateConnectionsToSameEnrollmentComposeOneExplicitContext()
    {
        await using var supervisor = new HostedNativeSignerSupervisor();
        var process = await supervisor.AddAsync("same", new string('a', 64));
        using var connection = new RemoteSignerConnection(process.Options());
        using var keyConnection = new RemoteSignerConnection(process.Options());
        var keys = new RemoteSecureKeyManager(keyConnection);
        var services = new ServiceCollection();

        services.AddNltgNodeServices(Configuration(process), keys, connection);

        using var provider = services.BuildServiceProvider();
        Assert.Equal(connection.Context, provider.GetRequiredService<NodeSigningContext>());
        Assert.Same(keys, provider.GetRequiredService<ISecureKeyManager>());
    }

    [Fact]
    public async Task LocalModeCannotComposeARemoteKeyManagerWithoutItsEnrollment()
    {
        await using var supervisor = new HostedNativeSignerSupervisor();
        var process = await supervisor.AddAsync("local", new string('a', 64));
        using var connection = new RemoteSignerConnection(process.Options());
        var configuration = Configuration(process);
        configuration["Signing:Mode"] = "Local";
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddNltgNodeServices(configuration,
            new RemoteSecureKeyManager(connection)));

        Assert.Empty(services);
    }

    private static IConfigurationRoot Configuration(HostedNativeSignerSupervisor.HostedSigner process)
    {
        var context = process.Options();
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:Network"] = context.Network,
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = "Data Source=:memory:",
            ["Signing:Mode"] = "RemoteNative",
            ["Signing:NodeId"] = context.NodeId,
            ["Signing:OwnerId"] = context.OwnerId,
            ["Signing:SignerId"] = context.SignerId,
            ["Signing:SocketPath"] = process.SocketPath,
            ["Signing:AuthTokenFile"] = Path.Combine(process.DirectoryPath, "token")
        }).Build();
    }
}