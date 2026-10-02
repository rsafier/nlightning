using System.Globalization;

namespace NLightning.Testing.Cluster.Faults;

/// <summary>
/// The POSIX <c>sh</c> scripts the process faults exec in a node's main container (busybox <c>ash</c>, Debian
/// <c>dash</c>, Alpine and <c>bash</c> alike), and the parser of their output.
/// </summary>
/// <remarks>
/// A container's processes are the ones in the exec shell's own cgroup (<c>/proc/&lt;pid&gt;/cgroup</c>, readable for
/// every user): with a shared process namespace the pod's <c>pause</c> process and the sidecars are in other cgroups
/// and are left alone. The scripts use only shell builtins (<c>read</c>, <c>kill</c>, <c>[</c>, <c>echo</c>) and
/// parameter expansion, so they fork nothing that could be signalled by mistake, and they skip their own shell.
/// Redirections that can fail (a process that exits while it is read) are on regular builtins only, which never end
/// the shell.
/// </remarks>
internal static class ContainerProcessScripts
{
    /// <summary>The exit code of a script that refused because the node's process is PID 1.</summary>
    public const int MainProcessIsPid1ExitCode = 4;

    /// <summary>The exit code when the shell cannot read its own cgroup.</summary>
    public const int NoCgroupExitCode = 5;

    /// <summary>The marker line of <see cref="List"/> when PID 1 is in the container's cgroup.</summary>
    public const string Pid1Marker = "PID1";

    private static readonly HashSet<string> s_signals = new(StringComparer.Ordinal) { "STOP", "CONT", "KILL", "TERM" };

    private const string Prelude =
        """
        self=$$
        mine=
        read -r mine < /proc/self/cgroup || exit 5
        one=
        read -r one 2>/dev/null < /proc/1/cgroup

        """;

    /// <summary>
    /// Sends <paramref name="signal"/> to every process of the container but the script's shell, printing
    /// <c>K &lt;pid&gt;</c> per signal sent. Repeats up to <paramref name="passes"/> times while it still finds
    /// processes to signal (a process forked during a pass is caught by the next).
    /// </summary>
    /// <param name="signal">STOP, CONT, KILL or TERM.</param>
    /// <param name="refuseWhenMainIsPid1">Exit with <see cref="MainProcessIsPid1ExitCode"/> before signalling anything
    /// when PID 1 is in the container (the kernel would ignore the signal).</param>
    /// <param name="skipStopped">Skip processes already stopped (for STOP, so a pass ends once all are).</param>
    /// <param name="passes">At most this many passes.</param>
    public static IReadOnlyList<string> Signal(string signal, bool refuseWhenMainIsPid1, bool skipStopped,
                                               int passes = 1)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!s_signals.Contains(signal))
            throw new ArgumentOutOfRangeException(nameof(signal), signal, "STOP, CONT, KILL or TERM");
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);

        var refuse = refuseWhenMainIsPid1
                         ? $"""
                            if [ "$one" = "$mine" ]; then
                              echo 'the node process is PID 1 of its pod: deploy it WithProcessFaults() (shareProcessNamespace)' >&2
                              exit {MainProcessIsPid1ExitCode}
                            fi

                            """
                         : string.Empty;
        var skip = skipStopped ? "    case \"$s\" in T|t) continue ;; esac\n" : string.Empty;
        var passCount = passes.ToString(CultureInfo.InvariantCulture);

        var script = Prelude + refuse +
                     $$"""
                       pass=0
                       while [ "$pass" -lt {{passCount}} ]; do
                         pass=$((pass + 1))
                         sent=0
                         for p in /proc/[0-9]*; do
                           pid=${p#/proc/}
                           [ "$pid" = "$self" ] && continue
                           c=
                           read -r c 2>/dev/null < "$p/cgroup" || continue
                           [ "$c" = "$mine" ] || continue
                           st=
                           read -r st 2>/dev/null < "$p/stat" || continue
                           rest=${st##*") "}
                           s=${rest%% *}
                           case "$s" in Z|X|x) continue ;; esac
                       {{skip}}    kill -s {{signal}} "$pid" 2>/dev/null || continue
                           sent=$((sent + 1))
                           echo "K $pid"
                         done
                         [ "$sent" -eq 0 ] && break
                       done
                       exit 0
                       """;
        return ["sh", "-c", script];
    }

    /// <summary>
    /// Lists the container's processes but the script's shell as <c>P &lt;pid&gt; &lt;state&gt; &lt;name&gt;</c>, after
    /// a <see cref="Pid1Marker"/> line when PID 1 is one of them.
    /// </summary>
    public static IReadOnlyList<string> List()
    {
        const string body =
            """
            [ "$one" = "$mine" ] && echo PID1
            for p in /proc/[0-9]*; do
              pid=${p#/proc/}
              [ "$pid" = "$self" ] && continue
              c=
              read -r c 2>/dev/null < "$p/cgroup" || continue
              [ "$c" = "$mine" ] || continue
              st=
              read -r st 2>/dev/null < "$p/stat" || continue
              rest=${st##*") "}
              s=${rest%% *}
              n=${st#*"("}
              n=${n%")"*}
              echo "P $pid $s $n"
            done
            exit 0
            """;
        return ["sh", "-c", Prelude + body];
    }

    /// <summary>The pids of the <c>K &lt;pid&gt;</c> lines of a <see cref="Signal"/> run.</summary>
    public static IReadOnlyList<int> ParseSignalled(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var pids = new List<int>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("K ", StringComparison.Ordinal)
             && int.TryParse(line.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                pids.Add(pid);
        }

        return pids.Distinct().ToList();
    }

    /// <summary>The output of <see cref="List"/>.</summary>
    public static ContainerProcessList ParseList(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var pid1 = false;
        var processes = new List<ContainerProcess>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line == Pid1Marker)
            {
                pid1 = true;
                continue;
            }

            if (!line.StartsWith("P ", StringComparison.Ordinal))
                continue;

            var parts = line.Split(' ', 4);
            if (parts.Length < 3
             || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
             || parts[2].Length != 1)
                continue;

            processes.Add(new ContainerProcess(pid, parts[2][0], parts.Length == 4 ? parts[3] : string.Empty));
        }

        return new ContainerProcessList(pid1, processes);
    }
}