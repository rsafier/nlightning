namespace NLightning.Domain.Client.Requests;

/// <summary>
/// Describes the gossip graph (<c>ClientCommand.DescribeGraph</c>, BOLT 7 plan G5-T4): the summary always, and on
/// request one page of its channels (by short channel id) and of its node announcements (by node id).
/// </summary>
public sealed class DescribeGraphClientRequest
{
    /// <summary>The page size when none is given.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The largest page (a 200,000-channel graph is listed in pages, never at once).</summary>
    public const int MaxLimit = 1_000;

    /// <summary>Include a page of channels.</summary>
    public bool IncludeChannels { get; init; }

    /// <summary>Include a page of node announcements.</summary>
    public bool IncludeNodes { get; init; }

    /// <summary>
    /// How many entries to skip. A non-zero offset pages one listing and is refused when both are requested.
    /// </summary>
    public int Offset { get; init; }

    /// <summary>The page size, 1 to <see cref="MaxLimit"/>.</summary>
    public int Limit { get; init; } = DefaultLimit;
}