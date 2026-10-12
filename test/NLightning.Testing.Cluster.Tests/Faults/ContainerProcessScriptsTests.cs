using System.Diagnostics;

namespace NLightning.Testing.Cluster.Tests.Faults;

using Cluster.Faults;

public class ContainerProcessScriptsTests
{
    [Theory]
    [InlineData("STOP")]
    [InlineData("CONT")]
    [InlineData("KILL")]
    [InlineData("TERM")]
    public void Given_ASignal_When_TheScriptIsBuilt_Then_ItSendsItWithKillDashS(string signal)
    {
        // Act
        var command = ContainerProcessScripts.Signal(signal, false, false);

        // Assert
        Assert.Equal(["sh", "-c"], command.Take(2));
        Assert.Contains($"kill -s {signal} \"$pid\"", command[2]);
        Assert.Contains("[ \"$pid\" = \"$self\" ] && continue", command[2]);
        Assert.Contains("[ \"$c\" = \"$mine\" ] || continue", command[2]);
    }

    [Theory]
    [InlineData("HUP")]
    [InlineData("stop")]
    [InlineData("STOP; rm -rf /")]
    public void Given_AnUnknownSignal_When_TheScriptIsBuilt_Then_ItThrows(string signal)
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => ContainerProcessScripts.Signal(signal, false, false));
    }

    [Fact]
    public void Given_NoPasses_When_TheScriptIsBuilt_Then_ItThrows()
    {
        // Act / Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => ContainerProcessScripts.Signal("STOP", true, true, 0));
    }

    [Fact]
    public void Given_RefuseWhenPid1_When_TheScriptIsBuilt_Then_ItExitsWithTheRefusalCodeBeforeSignalling()
    {
        // Act
        var script = ContainerProcessScripts.Signal("STOP", true, true, 3)[2];

        // Assert
        var refusal = script.IndexOf($"exit {ContainerProcessScripts.MainProcessIsPid1ExitCode}", StringComparison.Ordinal);
        Assert.True(refusal > 0);
        Assert.True(refusal < script.IndexOf("kill -s", StringComparison.Ordinal));
        Assert.Contains("[ \"$pass\" -lt 3 ]", script);
        Assert.Contains("case \"$s\" in T|t) continue ;; esac", script);
    }

    [Fact]
    public void Given_NoRefusalAndNoSkip_When_TheScriptIsBuilt_Then_ItSignalsStoppedProcessesAndPid1Too()
    {
        // Act
        var script = ContainerProcessScripts.Signal("CONT", false, false)[2];

        // Assert
        Assert.DoesNotContain($"exit {ContainerProcessScripts.MainProcessIsPid1ExitCode}", script);
        Assert.DoesNotContain("T|t", script);
        Assert.Contains("[ \"$pass\" -lt 1 ]", script);
    }

    [Fact]
    public void Given_StopAndCont_When_TheScriptsAreBuilt_Then_StopGoesParentsFirstAndContChildrenFirst()
    {
        // Act
        var stop = ContainerProcessScripts.Signal("STOP", true, true, 3)[2];
        var cont = ContainerProcessScripts.Signal("CONT", false, false)[2];

        // Assert: both rank by the depth in the process tree (the ppid walk), then signal by depth
        Assert.All([stop, cont], script =>
        {
            Assert.Contains("read -r qs 2>/dev/null < \"/proc/$q/stat\" || break", script);
            Assert.Contains("entries=\"$entries $d:$pid\"", script);
            Assert.True(script.IndexOf("entries=\"$entries", StringComparison.Ordinal)
                      < script.IndexOf("kill -s", StringComparison.Ordinal));
        });
        Assert.Contains("d=0\n", stop);
        Assert.Contains("d=$((d + 1))", stop);
        Assert.Contains("d=$max\n", cont);
        Assert.Contains("d=$((d - 1))", cont);
    }

    [Fact]
    public void Given_AProcessName_When_TheScriptIsBuilt_Then_ItSignalsOnlyProcessesOfThatName()
    {
        // Act
        var all = ContainerProcessScripts.Signal("STOP", true, true, 3)[2];
        var named = ContainerProcessScripts.Signal("STOP", true, true, 3, "lightningd")[2];

        // Assert: the name test sits before the kill, after the cgroup test
        Assert.DoesNotContain("/comm", all);
        var nameTest = named.IndexOf("[ \"$n\" = \"lightningd\" ] || continue", StringComparison.Ordinal);
        Assert.True(nameTest > named.IndexOf("[ \"$c\" = \"$mine\" ] || continue", StringComparison.Ordinal));
        Assert.True(nameTest < named.IndexOf("kill -s STOP", StringComparison.Ordinal));
        Assert.Contains("read -r n 2>/dev/null < \"$p/comm\" || continue", named);

        // Assert: the named processes and their ancestors in the container (a waiting wrapper shell) are kept
        Assert.Contains("keep=\"$keep $q \"", named);
        Assert.Contains("case \"$keep\" in *\" $pid \"*) ;; *) continue ;; esac", named);
        Assert.True(named.IndexOf("keep=\"$keep $pid \"", StringComparison.Ordinal)
                  < named.IndexOf("case \"$keep\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("x;rm")]
    [InlineData("$(id)")]
    [InlineData("sixteen-chars-xx")]
    public void Given_AnInvalidProcessName_When_TheScriptIsBuilt_Then_ItThrows(string name)
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => ContainerProcessScripts.Signal("STOP", true, true, 1, name));
        Assert.False(ContainerProcessScripts.IsProcessName(name));
    }

    [Fact]
    public void Given_TheScripts_When_ParsedByAPosixShell_Then_TheyHaveNoSyntaxErrors()
    {
        // Arrange
        if (!File.Exists("/bin/sh"))
            Assert.Skip("no /bin/sh on this machine");
        string[] scripts =
        [
            ContainerProcessScripts.Signal("STOP", true, true, 3)[2],
            ContainerProcessScripts.Signal("CONT", false, false)[2],
            ContainerProcessScripts.Signal("KILL", true, false)[2],
            ContainerProcessScripts.Signal("STOP", true, true, 3, "lightningd")[2],
            ContainerProcessScripts.List()[2]
        ];

        foreach (var script in scripts)
        {
            // Act
            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardError = true };
            start.ArgumentList.Add("-n");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(script);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            // Assert
            Assert.True(process.ExitCode == 0, $"{error}\n{script}");
        }
    }

    [Fact]
    public void Given_SignalOutput_When_Parsed_Then_TheSignalledPidsAreReturnedOnce()
    {
        // Act
        var pids = ContainerProcessScripts.ParseSignalled("K 7\nK 12\nnoise\nK x\nK 7\r\nK 31\n");

        // Assert
        Assert.Equal([7, 12, 31], pids);
    }

    [Fact]
    public void Given_ListOutput_When_Parsed_Then_ProcessesAndThePid1MarkerAreRead()
    {
        // Arrange
        const string output = "PID1\nP 1 S sh\nP 7 T bitcoind\nP 9 Z sleep\nP 12 S tor worker\nP bad S x\nP 13 SS y\ngarbage\n";

        // Act
        var list = ContainerProcessScripts.ParseList(output);

        // Assert
        Assert.True(list.MainProcessIsPid1);
        Assert.Equal([1, 7, 9, 12], list.Processes.Select(p => p.Pid));
        Assert.True(list.Find(7)!.IsStopped);
        Assert.True(list.Find(9)!.HasExited);
        Assert.Equal("tor worker", list.Find(12)!.Name);
        Assert.Null(list.Find(13));
    }

    [Fact]
    public void Given_ListOutputWithoutMarker_When_Parsed_Then_Pid1IsNotTheNode()
    {
        // Act
        var list = ContainerProcessScripts.ParseList("P 6 S sh\r\nP 20 R sleep");

        // Assert
        Assert.False(list.MainProcessIsPid1);
        Assert.Equal(2, list.Processes.Count);
        Assert.False(list.Find(6)!.IsStopped);
        Assert.False(list.Find(20)!.HasExited);
    }
}