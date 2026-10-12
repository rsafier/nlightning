namespace NLightning.Domain.Protocol.OnionMessages;

using Crypto.ValueObjects;

/// <summary>
/// Where <c>IOnionMessageService.SendAsync</c> sends a message: a node id, or a blinded path given to us (a
/// <c>reply_path</c>, a BOLT 12 offer or invoice_request path).
/// </summary>
/// <remarks>
/// Exactly one of <see cref="NodeId"/> and <see cref="BlindedPath"/> is set.
/// </remarks>
public sealed record OnionMessageDestination
{
    /// <summary>
    /// The recipient's node id, or null for a blinded path.
    /// </summary>
    public CompactPubKey? NodeId { get; }

    /// <summary>
    /// The recipient's blinded path, or null for a node id.
    /// </summary>
    public WireBlindedPath? BlindedPath { get; }

    private OnionMessageDestination(CompactPubKey? nodeId, WireBlindedPath? blindedPath)
    {
        NodeId = nodeId;
        BlindedPath = blindedPath;
    }

    /// <summary>
    /// A destination named by its node id.
    /// </summary>
    public static OnionMessageDestination ToNode(CompactPubKey nodeId) => new(nodeId, null);

    /// <summary>
    /// A destination behind a blinded path (BOLT 4: the sender builds unblinded hops up to the introduction node).
    /// </summary>
    public static OnionMessageDestination ToBlindedPath(WireBlindedPath blindedPath)
    {
        ArgumentNullException.ThrowIfNull(blindedPath);
        return new OnionMessageDestination(null, blindedPath);
    }
}