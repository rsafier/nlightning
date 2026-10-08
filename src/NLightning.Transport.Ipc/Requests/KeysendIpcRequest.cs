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

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>), or null; an older client sends none.
    /// </summary>
    [Key(5)] public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>), or null for none.
    /// </summary>
    [Key(6)] public List<string>? Tags { get; init; }

    /// <summary>Operator-selected 32-byte preimage; null generates a fresh preimage in the node.</summary>
    [Key(7)] public Secret? Preimage { get; init; }

    public KeysendClientRequest ToClientRequest()
    {
        return new KeysendClientRequest(Destination, Amount)
        {
            CustomRecords = CustomRecordsIpc.ToRecords(CustomRecords),
            TimeoutSeconds = TimeoutSeconds,
            MaxFee = MaxFee,
            Preimage = Preimage,
            Label = Label,
            Tags = Tags ?? []
        };
    }
}