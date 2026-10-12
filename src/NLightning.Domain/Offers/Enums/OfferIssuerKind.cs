namespace NLightning.Domain.Offers.Enums;

/// <summary>
/// Which key signs the invoices of one of our offers (BOLT 12 "Invoices": <c>invoice_node_id</c> is
/// <c>offer_issuer_id</c> when set, else the final <c>blinded_node_id</c> of the path the request came through). Values
/// are persisted; never renumber them.
/// </summary>
public enum OfferIssuerKind : byte
{
    /// <summary>
    /// <c>offer_issuer_id</c> is our node id; invoices are signed with the node key (BOLT 12 plan D2, the only kind
    /// we create).
    /// </summary>
    NodeId = 0,

    /// <summary>
    /// No <c>offer_issuer_id</c>, only <c>offer_paths</c>; invoices are signed as the blinded recipient of the path
    /// (built, not used while D2 holds).
    /// </summary>
    BlindedPaths = 1
}