namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

public class ClusterCliTests
{
    [Fact]
    public void Given_AReapCommandLine_When_Parsed_Then_EveryOptionApplies()
    {
        // Act
        var parsed = ClusterCli.Parse([
            "reap", "--prefix", "nltg", "--ttl", "30m", "--run", "Batch_1", "--force", "--all", "--dry-run", "--wait",
            "--context", "orbstack"
        ]);

        // Assert
        Assert.Equal("reap", parsed.Command);
        Assert.Equal("nltg", parsed.Options.Prefix);
        Assert.Equal(TimeSpan.FromMinutes(30), parsed.Options.Ttl);
        Assert.Equal("batch-1", parsed.Options.RunFilter);
        Assert.True(parsed.Options.Force);
        Assert.False(parsed.Options.RequireSpikeLabel);
        Assert.True(parsed.Options.DryRun);
        Assert.True(parsed.Options.WaitForDeletion);
        Assert.Equal("orbstack", parsed.Context);
    }

    [Fact]
    public void Given_AListCommandLine_When_Parsed_Then_TheDefaultsApply()
    {
        // Act
        var parsed = ClusterCli.Parse(["list"]);

        // Assert
        Assert.Equal(TestRunOptions.SpikeNamespacePrefix, parsed.Options.Prefix);
        Assert.Equal(ReaperOptions.DefaultTtl, parsed.Options.Ttl);
        Assert.True(parsed.Options.RequireSpikeLabel);
        Assert.Null(parsed.Options.RunFilter);
        Assert.Null(parsed.Context);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("list", "--dry-run")]
    [InlineData("list", "--force", "--run", "x")]
    [InlineData("reap", "--ttl")]
    [InlineData("reap", "--ttl", "--all")]
    [InlineData("reap", "--bogus")]
    public void Given_ABadCommandLine_When_Parsed_Then_ItIsRefused(params string[] args)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => ClusterCli.Parse(args));
    }

    [Theory]
    [InlineData("reap", "--prefix", "kube")]
    [InlineData("reap", "--force")]
    public async Task Given_UnsafeOptions_When_Run_Then_ItExitsWithUsageBeforeTouchingTheCluster(params string[] args)
    {
        // Arrange
        var output = new StringWriter();
        var error = new StringWriter();
        var clientCreated = false;

        // Act
        var code = await ClusterCli.RunAsync(args, output, error, _ =>
        {
            clientCreated = true;
            throw new InvalidOperationException("no cluster in unit tests");
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ClusterCli.Usage, code);
        Assert.False(clientCreated);
        Assert.Contains("nltg-cluster:", error.ToString());
    }

    [Fact]
    public async Task Given_NoArguments_When_Run_Then_ItPrintsUsage()
    {
        // Arrange
        var output = new StringWriter();

        // Act
        var code = await ClusterCli.RunAsync([], output, TextWriter.Null, null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ClusterCli.Usage, code);
        Assert.Contains("nltg-cluster reap", output.ToString());
    }

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("30m", 1800)]
    [InlineData("6H", 21600)]
    [InlineData("2d", 172800)]
    [InlineData("1.5h", 5400)]
    [InlineData("120", 120)]
    public void Given_ADuration_When_Parsed_Then_ItIsInSeconds(string text, int seconds)
    {
        // Act & Assert
        Assert.Equal(TimeSpan.FromSeconds(seconds), ClusterCli.ParseDuration(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("h")]
    [InlineData("-5m")]
    [InlineData("0")]
    [InlineData("5w")]
    public void Given_ABadDuration_When_Parsed_Then_ItIsRefused(string text)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => ClusterCli.ParseDuration(text));
    }

    [Fact]
    public void Given_Candidates_When_Tabled_Then_EachIsOneAlignedLine()
    {
        // Arrange
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var candidates = new[]
        {
            new ReapCandidate("nltg-spike-a", "a", "smoke", now.AddMinutes(-3), TimeSpan.FromMinutes(3),
                              TimeSpan.FromHours(6), new RunOwner("mac", 7, null), false, ReapVerdict.OwnerGone,
                              "owner process 7 is gone"),
            new ReapCandidate("nltg-spike-long-b", "long-b", null, null, null, TimeSpan.FromHours(6), null, false,
                              ReapVerdict.Keep, "no owner recorded; TTL only")
        };

        // Act
        var table = ClusterCli.FormatTable(candidates, now,
                                           new Dictionary<string, string> { ["nltg-spike-a"] = "deleted" });

        // Assert
        var lines = table.TrimEnd('\n').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("NAMESPACE", lines[0]);
        Assert.Contains("mac:7", lines[1]);
        Assert.Contains("3m", lines[1]);
        Assert.EndsWith("owner process 7 is gone -> deleted", lines[1]);
        Assert.Equal(lines[0].IndexOf("SUITE", StringComparison.Ordinal),
                     lines[2].IndexOf("-", "nltg-spike-long-b".Length, StringComparison.Ordinal));
        Assert.Equal("no run namespaces\n", ClusterCli.FormatTable([], now, null));
    }
}