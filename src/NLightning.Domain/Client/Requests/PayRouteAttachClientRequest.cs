namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;

/// <summary>
/// Attaches routes to a <c>payroute</c> payment still in flight (<c>ClientCommand.PayRouteAttach</c>, NL-1276):
/// replacement shards for parts that failed while the payee holds the others. The identity must be the payment's
/// (the same hash, secret and total), the routes are validated as for <see cref="PayRouteClientRequest"/>, and with
/// the parts still in flight they must deliver at least the total within the fee limit. The label and tags stay the
/// first call's.
/// </summary>
public sealed class PayRouteAttachClientRequest
{
    /// <summary>The BOLT 11 invoice being paid; exclusive with <see cref="PaymentHash"/>.</summary>
    public string? Bolt11 { get; init; }

    /// <summary>The raw payment hash; exclusive with <see cref="Bolt11"/>.</summary>
    public Hash? PaymentHash { get; init; }

    /// <summary>The payment secret of the raw form (the all-zero secret when null, as for <c>payroute</c>).</summary>
    public Secret? PaymentSecret { get; init; }

    /// <summary>The payment's <c>total_msat</c>, in msat; null as for <see cref="PayRouteClientRequest"/>.</summary>
    public ulong? TotalMsatMsat { get; init; }

    /// <summary>The routes to attach, our peer first on each; 1 to 128 of them.</summary>
    public required IReadOnlyList<PayRouteRouteClientInfo> Routes { get; init; }

    /// <summary>How long to wait for the payment's outcome, in seconds (default 60, at most 300).</summary>
    public uint TimeoutSeconds { get; init; } = 60;

    /// <summary>
    /// The most the parts in flight and these routes may pay in routing fees together, in msat, or null for the
    /// payment's own limit.
    /// </summary>
    public ulong? MaxFeeMsat { get; init; }

    /// <summary>The same request as a <see cref="PayRouteClientRequest"/> (no label or tags).</summary>
    public PayRouteClientRequest ToPayRouteRequest() => new()
    {
        Bolt11 = Bolt11,
        PaymentHash = PaymentHash,
        PaymentSecret = PaymentSecret,
        TotalMsatMsat = TotalMsatMsat,
        Routes = Routes,
        TimeoutSeconds = TimeoutSeconds,
        MaxFeeMsat = MaxFeeMsat
    };
}