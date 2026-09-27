using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// Request for Keysend (ClientCommand 31): a spontaneous payment. The response is a <c>PayInvoiceIpcResponse</c>.
/// </summary>
[MessagePackObject]
public sealed class KeysendIpcRequest
{
    /// <summary>
    /// The payee's node id.
    /// </summary>
    [Key(0)] public required CompactPubKey Destination { get; init; }

    /// <summary>
    /// What the payee receives.
    /// </summary>
    [Key(1)] public required LightningMoney Amount { get; init; }

    /// <summary>
    /// Custom records for the payee by type (65536 or more), or null for none.
    /// </summary>
    [Key(2)] public Dictionary<ulong, byte[]>? CustomRecords { get; init; }

    /// <summary>
    /// How long the daemon waits for the outcome, in seconds, or null for its default.
    /// </summary>
    [Key(3)] public uint? TimeoutSeconds { get; init; }

    /// <summary>
    /// The most the payment may pay in routing fees, or null for the daemon's default.
    /// </summary>
    [Key(4)] public LightningMoney? MaxFee { get; init; }

    public KeysendClientRequest ToClientRequest()
    {
        return new KeysendClientRequest(Destination, Amount)
        {
            CustomRecords = CustomRecordsIpc.ToRecords(CustomRecords),
            TimeoutSeconds = TimeoutSeconds,
            MaxFee = MaxFee
        };
    }
}