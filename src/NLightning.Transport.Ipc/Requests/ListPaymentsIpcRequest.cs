using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;

/// <summary>
/// Request for ListPayments (ClientCommand 12): a page of our outgoing payments, newest first.
/// </summary>
[MessagePackObject]
public sealed class ListPaymentsIpcRequest
{
    /// <summary>
    /// How many of the newest payments to skip.
    /// </summary>
    [Key(0)] public int Skip { get; init; }

    /// <summary>
    /// The most payments to return.
    /// </summary>
    [Key(1)] public int Take { get; set; } = 100;

    public ListPaymentsClientRequest ToClientRequest()
    {
        return new ListPaymentsClientRequest { Skip = Skip, Take = Take };
    }
}