namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;
using Money;

/// <summary>
/// The route a payment to a node would take now (<c>ClientCommand.GetRoute</c>, BOLT 7 plan G4-T4). Nothing is sent.
/// </summary>
public sealed class GetRouteClientRequest
{
    /// <summary>The destination.</summary>
    public CompactPubKey NodeId { get; }

    /// <summary>What the destination must receive.</summary>
    public LightningMoney Amount { get; }

    /// <summary>The most the route may cost in fees, or null for the node's payment default.</summary>
    public LightningMoney? MaxFee { get; init; }

    /// <summary>The destination's <c>min_final_cltv_expiry_delta</c>, or null for BOLT 11's default of 18.</summary>
    public ushort? FinalCltvDelta { get; init; }

    public GetRouteClientRequest(CompactPubKey nodeId, LightningMoney amount)
    {
        NodeId = nodeId;
        Amount = amount;
    }
}