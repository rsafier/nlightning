using System.ComponentModel;
using System.Diagnostics;

namespace NLightning.Testing.Cluster.Run;

/// <summary>What became of a run's owner process on this host.</summary>
public enum OwnerProcessState
{
    /// <summary>The process runs, and its start time matches (or could not be compared).</summary>
    Alive,

    /// <summary>No process has that pid.</summary>
    Gone,

    /// <summary>A process has that pid but started at another time: the pid was reused, the owner is gone.</summary>
    PidReused
}

/// <summary>Looks up an owner process on the local host (a seam, so the reaper's rules are tested without one).</summary>
public interface IProcessProbe
{
    /// <summary>The state of process <paramref name="pid"/> that started at <paramref name="startUnixMs"/>.</summary>
    OwnerProcessState Probe(int pid, long? startUnixMs);
}

/// <summary>
/// <see cref="IProcessProbe"/> over <see cref="Process"/>. A process it may not inspect counts as alive: the reaper
/// never deletes on a guess.
/// </summary>
public sealed class LocalProcessProbe : IProcessProbe
{
    /// <summary>How far two readings of one process's start time may differ.</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public static LocalProcessProbe Instance { get; } = new();

    public OwnerProcessState Probe(int pid, long? startUnixMs)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return OwnerProcessState.Gone;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return OwnerProcessState.Alive;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                    return OwnerProcessState.Gone;
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return OwnerProcessState.Alive;
            }

            if (startUnixMs is not { } expected || RunOwner.TryGetStartUnixMs(process) is not { } actual)
                return OwnerProcessState.Alive;

            return Math.Abs(actual - expected) <= StartTimeTolerance.TotalMilliseconds
                       ? OwnerProcessState.Alive
                       : OwnerProcessState.PidReused;
        }
    }
}