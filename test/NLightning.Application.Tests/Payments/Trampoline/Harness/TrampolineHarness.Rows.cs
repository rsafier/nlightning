namespace NLightning.Application.Tests.Payments.Trampoline.Harness;

using Channels.Harness;
using Domain.Channels.Commitments;
using Domain.Crypto.ValueObjects;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;

/// <summary>
/// Readers of <see cref="TrampolineHarness"/>: a node's persisted invoice, payment, trampoline relay and trampoline hop
/// rows (each through a fresh scope's unit of work, so what was saved, not what is in memory) and its live HTLCs.
/// </summary>
internal sealed partial class TrampolineHarness
{
    public static Task<InvoiceModel?> GetInvoiceAsync(SwitchNode node, Hash paymentHash) =>
        node.InScopeAsync(u => u.InvoiceDbRepository.GetByPaymentHashAsync(paymentHash));

    public static Task<PaymentModel?> GetPaymentAsync(SwitchNode node, Hash paymentHash) =>
        node.InScopeAsync(u => u.PaymentDbRepository.GetByPaymentHashAsync(paymentHash));

    /// <summary>The trampoline relay <paramref name="node"/> stored for <paramref name="paymentHash"/>, with its parts
    /// (TR3-P tables; written by the relay engine).</summary>
    public static Task<(TrampolineRelayModel Relay, IReadOnlyList<TrampolineRelayPartModel> Parts)?> GetRelayAsync(
        SwitchNode node, Hash paymentHash) =>
        node.InScopeAsync(u => u.TrampolineRelayDbRepository.GetAsync(paymentHash));

    /// <summary>The inner (trampoline) hops a payer stored for its payment (TR3-P table; written by the TR4 client).
    /// </summary>
    public static Task<IReadOnlyList<PaymentTrampolineHopModel>> GetTrampolineHopsAsync(SwitchNode node,
                                                                                       Hash paymentHash) =>
        node.InScopeAsync(u => u.PaymentTrampolineHopDbRepository.GetByPaymentAsync(paymentHash));

    /// <summary>Every HTLC still in a commitment of a running node's loaded channels, with where it is.</summary>
    public IReadOnlyList<(SwitchNode Node, HtlcRecord Htlc)> LiveHtlcs() =>
        Nodes.Where(n => n.IsRunning)
             .SelectMany(n => n.Channels.SelectMany(c => (c.Commitments?.Htlcs.Values ?? []).Select(h => (n, h))))
             .ToList();

    /// <summary>Fails when any channel still holds an HTLC or a node took two channel locks in one flow.</summary>
    public void AssertQuiescent()
    {
        Assert.Empty(LiveHtlcs());
        foreach (var node in Nodes)
            Assert.Empty(node.LockAudit.Violations);
    }
}