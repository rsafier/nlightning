using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>
/// Request for ListInvoices (ClientCommand 11): a page of our invoices, newest first.
/// </summary>
[MessagePackObject]
public sealed class ListInvoicesIpcRequest
{
    /// <summary>
    /// How many of the newest invoices to skip.
    /// </summary>
    [Key(0)] public int Skip { get; init; }

    /// <summary>
    /// The most invoices to return.
    /// </summary>
    [Key(1)] public int Take { get; set; } = 100;

    public ListInvoicesClientRequest ToClientRequest()
    {
        return new ListInvoicesClientRequest { Skip = Skip, Take = Take };
    }
}