namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;
using Money;
using Payments.Keysend;

/// <summary>
/// Sends a spontaneous (keysend) payment (<c>ClientCommand.Keysend</c>): no invoice, the daemon picks the preimage.
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
}