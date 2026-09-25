namespace NLightning.Domain.Payments.Interfaces;

using Crypto.ValueObjects;
using Models;
using Money;

/// <summary>
/// Issues and looks up our invoices (BOLT2 plan N8-T2, ONION M4-T3; implemented by <c>InvoiceService</c>).
/// </summary>
/// <remarks>
/// <para><see cref="CreateInvoiceAsync"/> draws the preimage and payment secret from a CSPRNG, encodes a BOLT 11
/// invoice signed with the node key (<c>var_onion_optin</c> and <c>payment_secret</c> compulsory, no
/// <c>basic_mpp</c>; <c>s</c>, <c>c</c> = <c>RoutingOptions.InvoiceMinFinalCltvExpiry</c> and <c>x</c> set), and
/// <b>persists</b> it (<see cref="IInvoiceDbRepository"/> + <c>IUnitOfWork.SaveChangesAsync</c>) before returning
/// it. The returned BOLT 11 string is therefore always payable to us.</para>
/// <para>Settlement is not driven through this service's callers: the final hop accepts the invoice when it fulfills
/// the paying HTLC and settles it when the fulfill is irrevocably committed (<c>InvoiceModel.Accept</c> /
/// <c>Settle</c>, saved in the same unit of work as the channel transition).</para>
/// </remarks>
public interface IInvoiceService
{
    /// <summary>
    /// Creates, signs and persists an invoice.
    /// </summary>
    /// <param name="amount">The requested amount, or null for any amount.</param>
    /// <param name="description">BOLT 11 <c>d</c>; may be empty.</param>
    /// <param name="expirySeconds">BOLT 11 <c>x</c>, or null for <c>RoutingOptions.InvoiceExpirySeconds</c>.</param>
    /// <param name="cancellationToken">Cancels before the invoice is persisted.</param>
    Task<InvoiceModel> CreateInvoiceAsync(LightningMoney? amount, string description, uint? expirySeconds,
                                          CancellationToken cancellationToken = default);

    /// <summary>
    /// The invoice for <paramref name="paymentHash"/>, or null when we never issued one.
    /// </summary>
    Task<InvoiceModel?> GetInvoiceAsync(Hash paymentHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invoices, newest first.
    /// </summary>
    Task<IReadOnlyList<InvoiceModel>> ListInvoicesAsync(int skip, int take,
                                                        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels an open invoice; returns false when it does not exist or is no longer open.
    /// </summary>
    Task<bool> CancelInvoiceAsync(Hash paymentHash, CancellationToken cancellationToken = default);
}