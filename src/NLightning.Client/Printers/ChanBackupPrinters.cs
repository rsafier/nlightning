using System.Globalization;

namespace NLightning.Client.Printers;

using Domain.Channels.ValueObjects;
using Transport.Ipc.Responses;

/// <summary>
/// Prints an exported backup: where it went (or its hex), its channels and the node's own backup file.
/// </summary>
public sealed class ExportChanBackupPrinter : IPrinter<ExportChanBackupIpcResponse>
{
    private readonly TextWriter _output;
    private readonly string? _writtenTo;

    /// <param name="writtenTo">The file the client wrote the backup to; null prints the backup as hex.</param>
    /// <param name="output">Where to print (default the console).</param>
    public ExportChanBackupPrinter(string? writtenTo, TextWriter? output = null)
    {
        _writtenTo = writtenTo;
        _output = output ?? Console.Out;
    }

    public void Print(ExportChanBackupIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine($"Channel backup: {item.ChannelIds.Count} channel(s), {item.Backup.Length} bytes");
        foreach (var channelId in item.ChannelIds)
            _output.WriteLine($"  {channelId}");

        if (_writtenTo is not null)
            _output.WriteLine($"Written to: {_writtenTo}");
        else
            _output.WriteLine($"Backup (hex): {Convert.ToHexStringLower(item.Backup)}");

        _output.WriteLine($"Node backup file: {item.FilePath ?? "none (Node:Backup:FilePath not set)"}");
    }
}

/// <summary>
/// Prints what the node found in a backup.
/// </summary>
public sealed class VerifyChanBackupPrinter : IPrinter<VerifyChanBackupIpcResponse>
{
    private readonly TextWriter _output;

    public VerifyChanBackupPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(VerifyChanBackupIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine($"Channel backup: {(item.IsValid ? "valid" : "INVALID")}");
        if (item.Error is not null)
            _output.WriteLine($"  Error: {item.Error}");
        if (item.CreatedAt is { } createdAt)
            _output.WriteLine("  Created: "
                            + DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime
                                            .ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        _output.WriteLine($"  Channels: {item.Channels.Count}");
        foreach (var channel in item.Channels)
        {
            _output.WriteLine($"  {channel.ChannelId}");
            _output.WriteLine($"    Peer: {channel.RemoteNodeId}"
                            + (channel.Addresses.Count > 0 ? $"@{string.Join(",", channel.Addresses)}" : string.Empty));
            _output.WriteLine($"    Funding: {channel.FundingTxId}:{channel.FundingOutputIndex}, "
                            + $"{channel.CapacitySat} sat, "
                            + (channel.ShortChannelId is { } scid ? new ShortChannelId(scid).ToString() : "no scid"));
            _output.WriteLine($"    Type: {(channel.OptionAnchors ? "anchors" : "static_remotekey")}, "
                            + $"{(channel.IsInitiator ? "we funded it" : "the peer funded it")}");
            _output.WriteLine($"    Keys: {(channel.KeysMatch ? "derive from this key file" : "DO NOT MATCH")}");
            _output.WriteLine($"    Local state: {channel.LocalState?.ToString() ?? "not in the database"}");
        }
    }
}