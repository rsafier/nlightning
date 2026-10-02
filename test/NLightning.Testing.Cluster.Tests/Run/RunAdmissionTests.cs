using k8s.Models;

namespace NLightning.Testing.Cluster.Tests.Run;

using Cluster.Run;

public class RunAdmissionTests
{
    private static V1Namespace Namespace(string name, int createdSecond, bool managed = true, string? run = "r")
    {
        var labels = new Dictionary<string, string>();
        if (run is not null)
            labels[RunLabels.Run] = run;
        if (managed)
            labels[RunLabels.ManagedBy] = RunLabels.ManagedByValue;

        return new V1Namespace
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                Labels = labels,
                CreationTimestamp = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + createdSecond).UtcDateTime
            }
        };
    }

    [Theory]
    [InlineData(null, 6)]
    [InlineData("", 6)]
    [InlineData("3", 3)]
    [InlineData(" 12 ", 12)]
    [InlineData("0", null)]
    [InlineData("off", null)]
    [InlineData("OFF", null)]
    public void Given_TheCapVariable_When_Parsed_Then_ItGivesTheCap(string? value, int? expected)
    {
        // Act & Assert
        Assert.Equal(expected, RunAdmission.ParseMaxRuns(value));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("six")]
    public void Given_ABadCapVariable_When_Parsed_Then_ItThrows(string value)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => RunAdmission.ParseMaxRuns(value));
    }

    [Fact]
    public void Given_LiveRuns_When_Ranked_Then_TheOrderIsCreationThenName()
    {
        // Arrange
        var live = new[]
        {
            Namespace("nltg-spike-c", 5),
            Namespace("nltg-spike-b", 1),
            Namespace("nltg-spike-a", 1),
            Namespace("nltg-spike-z", 0)
        };

        // Act & Assert
        Assert.Equal(0, RunAdmission.Rank(live, "nltg-spike-z"));
        Assert.Equal(1, RunAdmission.Rank(live, "nltg-spike-a"));
        Assert.Equal(2, RunAdmission.Rank(live, "nltg-spike-b"));
        Assert.Equal(3, RunAdmission.Rank(live, "nltg-spike-c"));
        Assert.Equal(-1, RunAdmission.Rank(live, "nltg-spike-missing"));
    }

    [Theory]
    [InlineData("nltg-spike-r", true, "r", true)]
    [InlineData("nltg-spike-adm-r", true, "r", true)]
    [InlineData("nltg-spike-r", false, "r", false)]
    [InlineData("nltg-spike-r", true, null, false)]
    [InlineData("nltg-r", true, "r", false)]
    [InlineData("default", true, "r", false)]
    public void Given_ANamespace_When_Checked_Then_OnlyHarnessRunsUnderThePrefixHoldASlot(string name, bool managed,
        string? run, bool expected)
    {
        // Act & Assert
        Assert.Equal(expected, RunAdmission.HoldsSlot(Namespace(name, 0, managed, run), "nltg-spike"));
    }

    [Fact]
    public void Given_ATerminatingRunNamespace_When_Counted_Then_ItStillHoldsASlot()
    {
        // Arrange: deleted in the background by its run (TestRun.DisposeAsync), its pods and PVCs still going
        var terminating = Namespace("nltg-spike-old", 0);
        terminating.Metadata.DeletionTimestamp = DateTime.UtcNow;
        terminating.Status = new V1NamespaceStatus { Phase = "Terminating" };
        var live = new[] { terminating, Namespace("nltg-spike-new", 1) };

        // Act & Assert: it counts, and ranks first (oldest), so a cap of 1 keeps the new run out until it is gone
        Assert.True(RunAdmission.HoldsSlot(terminating, "nltg-spike"));
        Assert.Equal(0, RunAdmission.Rank(live.Where(ns => RunAdmission.HoldsSlot(ns, "nltg-spike")), "nltg-spike-old"));
        Assert.Equal(1, RunAdmission.Rank(live, "nltg-spike-new"));
    }
}