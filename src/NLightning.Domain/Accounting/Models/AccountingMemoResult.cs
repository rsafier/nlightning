namespace NLightning.Domain.Accounting.Models;

/// <summary>
/// What one run of the memo backfill wrote (NL-602 A1-T6).
/// </summary>
/// <param name="Completed">Every source was read to its end and the completion marker is written (also when it was
/// written by an earlier run).</param>
/// <param name="Invoices">Memo <c>InvoiceSettled</c> events written.</param>
/// <param name="Payments">Memo <c>PaymentSucceeded</c>/<c>PaymentFailed</c> events written.</param>
/// <param name="Forwards">Memo <c>ForwardSettled</c> and <c>TrampolineRelaySettled</c> (NL-875) events written.</param>
/// <param name="Channels">Memo channel events written (<c>ChannelFunded</c> with its push, <c>ChannelClosedMutual</c>).
/// </param>
/// <param name="Skipped">Facts whose key was already in the feed (a live event or an earlier run).</param>
public sealed record AccountingMemoResult(bool Completed, int Invoices, int Payments, int Forwards, int Channels,
                                          int Skipped)
{
    /// <summary>Every memo event written by the run.</summary>
    public int Written => Invoices + Payments + Forwards + Channels;
}