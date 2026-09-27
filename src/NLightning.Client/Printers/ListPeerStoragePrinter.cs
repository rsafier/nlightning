using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// Prints <c>listpeerstorage</c> (ClientCommand 32, NL-432).
/// </summary>
public sealed class ListPeerStoragePrinter : IPrinter<ListPeerStorageIpcResponse>
{
    private readonly TextWriter _output;

    public ListPeerStoragePrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ListPeerStorageIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var inv = CultureInfo.InvariantCulture;

        _output.WriteLine(string.Format(inv, "Backups handed back by peers ({0})", item.Retrievals.Count));
        if (item.BackupsHeldForDataLoss)
            _output.WriteLine("  Our backups go to no peer until the restart: a peer holds one naming channels we "
                            + "have no record of.");

        var toRestore = 0;
        foreach (var retrieval in item.Retrievals)
        {
            _output.WriteLine(string.Format(inv, "  Peer:              {0}", retrieval.PeerNodeId));
            _output.WriteLine(string.Format(inv, "    Received:        {0:u}{1}", retrieval.ReceivedAt,
                                            retrieval.Persisted ? string.Empty : " (not stored yet)"));
            _output.WriteLine(string.Format(inv, "    Blob:            {0} bytes, {1}", retrieval.BlobLength,
                                            retrieval.IsOurs ? "our backup" : "not one of ours"));
            if (retrieval.BackupCreatedAt is { } createdAt)
                _output.WriteLine(string.Format(inv, "    Backup built:    {0:u}", createdAt));
            _output.WriteLine(string.Format(inv, "    Last sent to it: {0}",
                                            retrieval.MatchesLastSent switch
                                            {
                                                true => "yes",
                                                false => "no",
                                                null => "none sent by that process"
                                            }));
            foreach (var channel in retrieval.Channels)
            {
                if (!channel.KnownNow)
                    toRestore++;

                _output.WriteLine(string.Format(inv, "    Channel {0} with {1}: {2}", channel.ChannelId,
                                                channel.PeerNodeId,
                                                channel.KnownNow
                                                    ? channel.UnknownWhenReceived ? "restored" : "known"
                                                    : "UNKNOWN (restore it)"));
            }

            if (retrieval.Blob is { } blob)
                _output.WriteLine(string.Format(inv, "    Blob hex:        {0}", Convert.ToHexStringLower(blob)));
        }

        if (toRestore > 0)
            _output.WriteLine(string.Format(inv,
                                            "  {0} channel(s) named by a peer's copy are unknown to this node: restore "
                                          + "them with restorechanbackup and your static channel backup.",
                                            toRestore));

        _output.WriteLine(string.Format(inv, "Blobs we keep for peers ({0})", item.StoredBlobs.Count));
        foreach (var stored in item.StoredBlobs)
            _output.WriteLine(string.Format(inv, "  {0}: {1} bytes, updated {2:u}", stored.PeerNodeId,
                                            stored.BlobLength, stored.UpdatedAt));
    }
}