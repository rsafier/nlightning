using System.Diagnostics;

namespace NLightning.Application.Gossip.Graph;

using Interfaces;

/// <summary>
/// Reads the process's resident set from <see cref="Process.WorkingSet64"/> and the memory the GC has committed
/// (<see cref="GCMemoryInfo.TotalCommittedBytes"/>, or the allocated bytes when that is larger, e.g. before the first
/// collection) without forcing a collection.
/// </summary>
/// <remarks>
/// <see cref="Environment.WorkingSet"/> is not used: on Unix it returns 0 instead of failing when it cannot read the
/// process, which the budget would take for an empty process and resume at once. A reading of 0 throws here, so
/// <see cref="GossipMemoryBudget"/> keeps its last decision.
/// </remarks>
public sealed class ProcessMemoryReader : IProcessMemoryReader
{
    /// <summary>The shared instance.</summary>
    public static readonly ProcessMemoryReader Instance = new();

    /// <inheritdoc />
    public ProcessMemoryUsage Read()
    {
        long workingSet;
        using (var process = Process.GetCurrentProcess())
            workingSet = process.WorkingSet64;

        if (workingSet <= 0)
            throw new InvalidOperationException("The process's working set could not be read");

        var managed = Math.Max(GC.GetGCMemoryInfo().TotalCommittedBytes, GC.GetTotalMemory(false));
        return new ProcessMemoryUsage(workingSet, managed);
    }
}