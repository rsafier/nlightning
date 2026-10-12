using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Nodes.BitcoinCore;

using Cluster.Images;
using Cluster.Kube;
using Cluster.Nodes.BitcoinCore;
using Cluster.Topology;

/// <summary>
/// The chain's startup wait (<see cref="BitcoinCoreWorkload.StartupWaitContainer"/>): its container, and its script run
/// by the local <c>sh</c> against a stub <c>bitcoin-cli</c>.
/// </summary>
public class StartupWaitTests
{
    [Fact]
    public void Given_TheChainOptions_When_TheWaitIsBuilt_Then_ItRunsBitcoindsImageAgainstTheChainsAlias()
    {
        // Arrange
        var options = new BitcoinCoreOptions { Name = "miner", Image = ImageVersions.BitcoinCore31 };

        // Act
        var container = BitcoinCoreWorkload.StartupWaitContainer(options);

        // Assert
        Assert.Equal(BitcoinCoreWorkload.StartupWaitContainerName, container.Name);
        Assert.Equal(ImageVersions.BitcoinCore31.Reference, container.Image);
        Assert.Equal(ImageVersions.BitcoinCore31.PullPolicyValue, container.ImagePullPolicy);
        Assert.Equal("sh", container.Command[0]);
        var script = container.Command[2];
        Assert.Contains("'-rpcconnect=miner'", script);
        Assert.Contains("'-rpcuser=nltg'", script);
        Assert.Contains("getblockchaininfo", script);
        Assert.Equal(WorkloadResources.Tiny.CpuRequest, container.Resources.Requests["cpu"].ToString());
    }

    [Fact]
    public void Given_TheChainEndpoint_When_AskedForTheWait_Then_ItIsTheChainsOwn()
    {
        // Arrange
        var endpoint = BitcoinCoreTopologyChain.EndpointFor(new TopologyNodeSpec("chain", Cluster.Nodes.NodeKind.BitcoinCore));

        // Act
        var container = endpoint.CreateStartupWait();

        // Assert
        Assert.NotNull(container);
        Assert.Equal("chain", endpoint.RpcHost);
        Assert.Contains("'-rpcconnect=chain'", container.Command[2]);
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("a b")]
    public void Given_ARpcPasswordWithShellCharacters_When_Quoted_Then_TheShellGetsItVerbatim(string password)
    {
        // Act
        var quoted = BitcoinCoreWorkload.ShellQuote(password);

        // Assert
        Assert.Equal(password, RunSh($"printf %s {quoted}", null).Output);
    }

    [Fact]
    public void Given_BitcoindAnswersOnTheThirdCall_When_TheWaitRuns_Then_ItExitsZeroOnceItAnswers()
    {
        // Arrange: a bitcoin-cli that fails twice, then answers
        var script = BitcoinCoreWorkload.StartupWaitContainer(new BitcoinCoreOptions(), timeoutSeconds: 30).Command[2];
        using var stub = new StubCli("n=$(cat \"$0.count\" 2>/dev/null || echo 0); n=$((n + 1)); echo $n > \"$0.count\"; "
                                   + "[ $n -ge 3 ] || { echo 'error: Could not connect to the server' >&2; exit 1; }");

        // Act
        var (code, output) = RunSh(script, stub.Directory);

        // Assert
        Assert.Equal(0, code);
        Assert.Contains("miner:18443 answers after", output);
        Assert.Contains("2 failed call(s), last: error: Could not connect to the server", output);
        Assert.Equal("3", File.ReadAllText(stub.Path + ".count").Trim());
    }

    [Fact]
    public void Given_BitcoindNeverAnswers_When_TheWaitTimesOut_Then_ItLetsTheNodeStartAnyway()
    {
        // Arrange
        var script = BitcoinCoreWorkload.StartupWaitContainer(new BitcoinCoreOptions(), timeoutSeconds: 1).Command[2];
        using var stub = new StubCli("exit 1");

        // Act
        var (code, output) = RunSh(script, stub.Directory);

        // Assert
        Assert.Equal(0, code);
        Assert.Contains("starting anyway", output);
    }

    private static (int Code, string Output) RunSh(string script, string? pathPrefix)
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("needs a POSIX sh");

        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        if (pathPrefix is not null)
            start.Environment["PATH"] = pathPrefix + ":" + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    /// <summary>A <c>bitcoin-cli</c> on a temporary PATH entry that runs <paramref name="body"/>.</summary>
    private sealed class StubCli : IDisposable
    {
        public StubCli(string body)
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nltg-wait-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, "bitcoin-cli");
            File.WriteAllText(Path, "#!/bin/sh\n" + body + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Directory { get; }

        public string Path { get; }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}