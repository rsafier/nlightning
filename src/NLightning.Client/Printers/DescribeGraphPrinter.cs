using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// Prints <c>describegraph</c> (ClientCommand 20): the summary, each connection's sync state, then the requested pages
/// in the <c>listgraphchannels</c>/<c>listnodes</c> format with the command for the next page.
/// </summary>
public sealed class DescribeGraphPrinter : IPrinter<DescribeGraphIpcResponse>
{
    private readonly TextWriter _output;

    public DescribeGraphPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(DescribeGraphIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        Line($"Graph: {item.Channels} channels, {item.GraphNodes} nodes{(item.IsLoaded ? "" : " (not loaded yet)")}");
        Line($"  Channels:           {item.Channels} ({item.SpentChannels} spent, {item.UnverifiedChannels} unverified, {item.OwnChannels} ours, {item.ChannelsWithoutPolicy} without a policy)");
        Line($"  Policies:           {item.Policies} ({item.DisabledPolicies} disabled)");
        Line($"  Nodes:              {item.GraphNodes} ({item.AnnouncedNodes} announced)");
        Line($"  Capacity (sat):     {item.CapacitySat}");
        Line($"  Memory (estimate):  {ToMegabytes(item.EstimatedStoreBytes)} MiB store + {ToMegabytes(item.EstimatedSnapshotBytes)} MiB per snapshot");
        Line($"  Pending writes:     {item.PendingWrites}");
        Line($"  Ingress:            {Optional(item.IngressQueued)} queued, {Optional(item.IngressDropped)} dropped, {Optional(item.Orphans)} orphans");
        Line($"  Initial sync:       {item.HasCompletedInitialSync switch { true => "complete", false => "not complete", null => "-" }}");
        Line($"  Peers:              {item.Peers.Count}");
        foreach (var peer in item.Peers)
        {
            var queries = peer.SupportsQueriesEx ? "gossip_queries_ex" : peer.SupportsQueries ? "gossip_queries" : "no queries";
            var role = peer.IsSyncPeer ? "sync peer" : "peer";
            var lastSync = peer.LastRangeSyncAt is { } at
                               ? DateTimeOffset.FromUnixTimeSeconds(at).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
                               : "never";
            var theirs = peer.PeerFilterFirstTimestamp is { } first
                             ? $"{first}+{peer.PeerFilterTimestampRange}"
                             : "none";
            var ours = peer.OurFilterFirstTimestamp is { } ourFirst
                           ? $"{ourFirst}+{peer.OurFilterTimestampRange}"
                           : "none";
            Line($"    {peer.PeerId}: {role}, {queries}, last range sync {lastSync}{(peer.IsRangeSyncRunning ? " (running)" : "")}, their filter {theirs}, ours {ours}, {peer.PendingWork} queued{(peer.QueryingStopped ? ", querying stopped" : "")}");
        }

        if (item.ChannelPage.Count > 0 || item.NextChannelOffset is not null)
        {
            _output.WriteLine(PaymentsPrintFormat.Separator);
            new ListGraphChannelsPrinter(_output).Print(new ListGraphChannelsIpcResponse { Channels = item.ChannelPage });
            if (item.NextChannelOffset is { } next)
                Line($"More channels: describegraph --channels --offset {next}");
        }

        if (item.NodePage.Count > 0 || item.NextNodeOffset is not null)
        {
            _output.WriteLine(PaymentsPrintFormat.Separator);
            new ListNodesPrinter(_output).Print(new ListNodesIpcResponse { Nodes = item.NodePage });
            if (item.NextNodeOffset is { } next)
                Line($"More nodes: describegraph --nodes --offset {next}");
        }
    }

    private void Line(FormattableString text) => _output.WriteLine(text.ToString(CultureInfo.InvariantCulture));

    private static string Optional<T>(T? value) where T : struct =>
        value is { } v ? string.Format(CultureInfo.InvariantCulture, "{0}", v) : "-";

    private static string ToMegabytes(long bytes) =>
        (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
}