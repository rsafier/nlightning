// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Payment;

using Domain.Crypto.ValueObjects;

/// <summary>
/// One hop of the trampoline onion of one attempt of our own payment (<c>PaymentTrampolineHopModel</c>, NL-875), with
/// the shared secret that decrypts a trampoline node's failure. Keyed by (payment hash, attempt, hop index); no
/// foreign key to <c>Payments</c> (a retry replaces the payment row, the hops of every attempt stay).
/// </summary>
public class PaymentTrampolineHopEntity
{
    /// <summary>The payment's hash.</summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required Hash PaymentHash { get; set; }

    /// <summary>The attempt the trampoline onion was built for.</summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required int Attempt { get; set; }

    /// <summary>The hop's position in the trampoline route.</summary>
    /// <remarks>This is part of the composite-key identifier</remarks>
    public required int HopIndex { get; set; }

    /// <summary>The trampoline node (or the payee, last).</summary>
    public required CompactPubKey NodeId { get; set; }

    /// <summary>The 32-byte Sphinx shared secret of the hop's layer of the trampoline onion.</summary>
    public required byte[] SharedSecret { get; set; }

    /// <summary>The hop's <c>amt_to_forward</c>, in millisatoshi.</summary>
    public required long AmountMsat { get; set; }

    /// <summary>The hop's <c>outgoing_cltv_value</c>.</summary>
    public required uint CltvExpiry { get; set; }

    // Default constructor for EF Core
    internal PaymentTrampolineHopEntity()
    {
    }
}