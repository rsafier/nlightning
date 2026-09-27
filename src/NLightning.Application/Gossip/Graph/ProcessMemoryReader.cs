namespace NLightning.Application.Gossip.Graph;

using Interfaces;

/// <summary>
/// Reads <see cref="Environment.WorkingSet"/> (the process's RSS) and <see cref="GC.GetTotalMemory(bool)"/> without
/// forcing a collection.
/// </summary>
public sealed class ProcessMemoryReader : IProcessMemoryReader
{
    /// <summary>The shared instance.</summary>
    public static readonly ProcessMemoryReader Instance = new();

    /// <inheritdoc />
    public ProcessMemoryUsage Read() => new(Environment.WorkingSet, GC.GetTotalMemory(false));
}