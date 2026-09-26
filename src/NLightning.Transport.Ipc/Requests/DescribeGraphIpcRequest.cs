using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>
/// Request for DescribeGraph (ClientCommand 20, BOLT 7 plan G5-T4). Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class DescribeGraphIpcRequest
{
    /// <summary>Include a page of channels (by short channel id).</summary>
    [Key(0)] public bool IncludeChannels { get; set; }

    /// <summary>Include a page of node announcements (by node id).</summary>
    [Key(1)] public bool IncludeNodes { get; set; }

    /// <summary>
    /// How many entries to skip. A non-zero offset pages one listing and is refused when both are requested.
    /// </summary>
    [Key(2)] public int Offset { get; set; }

    /// <summary>The page size (1 to 1,000).</summary>
    [Key(3)] public int Limit { get; set; } = DescribeGraphClientRequest.DefaultLimit;

    public DescribeGraphClientRequest ToClientRequest() =>
        new()
        {
            IncludeChannels = IncludeChannels,
            IncludeNodes = IncludeNodes,
            Offset = Offset,
            Limit = Limit
        };
}