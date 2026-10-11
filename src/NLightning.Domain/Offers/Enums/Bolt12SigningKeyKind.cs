namespace NLightning.Domain.Offers.Enums;

/// <summary>
/// The key a BOLT 12 signature is made with (<see cref="Models.Bolt12SigningKey"/>).
/// </summary>
public enum Bolt12SigningKeyKind : byte
{
    /// <summary>
    /// The node key.
    /// </summary>
    Node = 0,

    /// <summary>
    /// A transient payer key derived from an <c>invreq_metadata</c> and a node secret.
    /// </summary>
    Payer = 1,

    /// <summary>
    /// <c>HMAC256("blinded_node_id", ECDH(path_key, node_key)) * node_key</c>: our blinded node id in one of our paths.
    /// </summary>
    BlindedRecipient = 2
}