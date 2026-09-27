namespace NLightning.Application.Gossip.Graph.Interfaces;

/// <summary>
/// Reads the process's memory for <see cref="GossipMemoryBudget"/> (NL-373). The default is
/// <see cref="ProcessMemoryReader"/>; tests pass a fake.
/// </summary>
public interface IProcessMemoryReader
{
    /// <summary>The process's memory now.</summary>
    ProcessMemoryUsage Read();
}

/// <summary>One reading of the process's memory.</summary>
/// <param name="WorkingSetBytes">The resident set (RSS) of the whole process: what the budget is checked against.</param>
/// <param name="ManagedHeapBytes">The managed heap the GC holds (reported, not budgeted on its own).</param>
public readonly record struct ProcessMemoryUsage(long WorkingSetBytes, long ManagedHeapBytes);