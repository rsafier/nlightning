namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;
using Money;
using Payments.Keysend;

/// <summary>
/// Sends a spontaneous (keysend) payment (<c>ClientCommand.Keysend</c>): no invoice, an operator supplies the preimage or the daemon generates one.
/// </summary>
public sealed class KeysendClientRequest
{
    public KeysendClientRequest(CompactPubKey destination, LightningMoney amount)
    {
        Destination = destination;
        Amount = amount;
    }

    /// <summary>The payee's node id.</summary>
    public CompactPubKey Destination { get; }

    /// <summary>What the payee receives.</summary>
    public LightningMoney Amount { get; }

    /// <summary>Operator-selected preimage for externally approved payments; null generates a fresh one.</summary>
    public Secret? Preimage { get; init; }

    /// <summary>
    /// Application records for the payee (types of 65536 or more, never 5482373484, the keysend preimage).
    /// </summary>
    public IReadOnlyList<CustomRecord> CustomRecords { get; init; } = [];

    /// <summary>
    /// How long to wait for the outcome, in seconds, or null for the default (60). The payment keeps going after the
    /// wait ends; the response then reports it in flight.
    /// </summary>
    public uint? TimeoutSeconds { get; init; }

    /// <summary>
    /// The most the payment may pay in routing fees, or null for the node's default (max(0.5 %, 5000 msat)).
    /// </summary>
    public LightningMoney? MaxFee { get; init; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>): stored on the row and copied into the accounting event's
    /// details; null for none. Checked by the daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>, repeatable); empty for none. Checked by the
    /// daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}