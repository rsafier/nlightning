using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class ListNodesPrinter : IPrinter<ListNodesIpcResponse>
{
    private readonly TextWriter _output;

    public ListNodesPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ListNodesIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine("Graph nodes: {0}", item.Nodes.Count.ToString(CultureInfo.InvariantCulture));
        if (item.Nodes.Count == 0)
            return;

        _output.WriteLine(PaymentsPrintFormat.Separator);
        foreach (var node in item.Nodes)
        {
            _output.WriteLine("  Node Id:            {0}", node.NodeId);
            _output.WriteLine("  Alias:              {0}", node.Alias.Length == 0 ? "-" : node.Alias);
            _output.WriteLine("  Color:              {0}", node.Color);
            _output.WriteLine("  Addresses:          {0}",
                              node.Addresses.Count == 0 ? "-" : string.Join(", ", node.Addresses));
            _output.WriteLine("  Features:           {0}", node.Features.Length == 0 ? "-" : node.Features);
            _output.WriteLine("  Timestamp:          {0}", FormatTimestamp(node.Timestamp));
            _output.WriteLine("  Channels:           {0}", node.ChannelCount.ToString(CultureInfo.InvariantCulture));
            _output.WriteLine(PaymentsPrintFormat.Separator);
        }
    }

    internal static string FormatTimestamp(uint timestamp) =>
        string.Create(CultureInfo.InvariantCulture,
                      $"{timestamp} ({DateTimeOffset.FromUnixTimeSeconds(timestamp):yyyy-MM-dd HH:mm:ss} UTC)");
}