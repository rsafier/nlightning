using System.Diagnostics;

namespace NLightning.RemoteSigning.Tests;

using Infrastructure.RemoteSigning;
using Signer;

public sealed class SignerOwnershipTests
{
    [Fact]
    public async Task Given_AnActiveInjectedSigner_When_AnotherProcessUsesItsState_Then_OnlyTheOwnerCanSign()
    {
        // Arrange: the second process has its own endpoint, but shares the durable state.
        await using var fixture = new SignerDaemonFixture(injected: true);
        await fixture.InitializeAsync();
        using var connection = new RemoteSignerConnection(fixture.Options());
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        var secondSocket = Path.Combine(fixture.DirectoryPath, "second.sock");
        foreach (var argument in new[]
                 {
                     typeof(SignerAssemblyMarker).Assembly.Location, "--socket", secondSocket,
                     "--seed-stdin", "--state-file", Path.Combine(fixture.DirectoryPath, "state"),
                     "--auth-token-file", Path.Combine(fixture.DirectoryPath, "token"), "--network", "regtest"
                 })
            start.ArgumentList.Add(argument);

        // Act: startup must refuse shared ownership before consuming an injected seed.
        using var competitor = Process.Start(start)!;
        var output = competitor.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = competitor.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await competitor.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!competitor.HasExited)
            {
                competitor.Kill(entireProcessTree: true);
                await competitor.WaitForExitAsync(CancellationToken.None);
            }
        }

        // Assert: the owner still serves requests, and its lock is released on process death.
        Assert.NotEqual(0, competitor.ExitCode);
        Assert.DoesNotContain("SIGNER_READY", await output);
        Assert.Contains("exclusive key ownership", await error);
        Assert.False(File.Exists(secondSocket));
        var keys = new RemoteSecureKeyManager(connection);
        Assert.Equal(connection.Identity.NodePublicKey, keys.GetNodePubKey());
        var index = keys.ReserveChannelKeyIndex();
        await fixture.RestartAsync();
        using var restarted = new RemoteSignerConnection(fixture.Options());
        Assert.Equal(connection.Identity.NodePublicKey, restarted.Identity.NodePublicKey);
        Assert.True(new RemoteSecureKeyManager(restarted).ReserveChannelKeyIndex() > index);
    }
}