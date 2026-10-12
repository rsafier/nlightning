namespace NLightning.Domain.Protocol.Onion.Models;

/// <summary>
/// The result of decrypting a failure at the origin node with its <c>attribution_data</c>.
/// </summary>
public sealed class AttributedFailure
{
    /// <summary>
    /// The legacy decryption result: the erring hop and its message, or <c>null</c> when no hop's return-packet HMAC
    /// matched.
    /// </summary>
    public DecryptedFailure? Failure { get; }

    /// <summary>
    /// What the <c>attribution_data</c> said. When <see cref="Failure"/> is known, only the hops up to the erring hop
    /// are checked.
    /// </summary>
    public AttributionVerification Attribution { get; }

    /// <summary>
    /// The hop to hold responsible: the erring hop when the return packet identified one, otherwise (BOLT 4: "when the
    /// failure source cannot be identified from the return packet AND attribution_data is present") the first hop
    /// whose attribution HMAC failed. That hop shares the blame with its upstream neighbour. <c>null</c> when neither
    /// identifies anyone.
    /// </summary>
    public int? BlamedHopIndex => Failure?.ErringHopIndex ?? Attribution.InvalidHopIndex;

    public AttributedFailure(DecryptedFailure? failure, AttributionVerification attribution)
    {
        ArgumentNullException.ThrowIfNull(attribution);

        Failure = failure;
        Attribution = attribution;
    }
}