namespace NLightning.Domain.Accounting.Financial.Classification;

using Client.Requests;
using Client.Responses;

/// <summary>
/// The <c>accounting classify</c> administration (IPC 45, NL-602 A3-T3, D-A10): the rules, the overrides, a test of
/// one event and the unclassified listing.
/// </summary>
public interface IAccountingClassificationAdmin
{
    /// <exception cref="Client.Exceptions.ClientException">An invalid request (<c>invalid_operation</c>).</exception>
    Task<AccountingClassifyClientResponse> HandleAsync(AccountingClassifyClientRequest request,
                                                       CancellationToken cancellationToken = default);
}