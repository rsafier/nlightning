namespace NLightning.Daemon.Handlers;

using Domain.Accounting.Prices;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;

/// <summary>
/// The <c>prices</c> actions of <c>ClientCommand.AccountingAdmin</c> (NL-602 A3-T2): <c>import</c>, <c>list</c> and
/// <c>fetch</c> through <see cref="IAccountingPrices"/>. They work with the books off (they only store prices; the
/// valuation that follows an import or a fetch runs with the books on).
/// </summary>
internal static class AccountingPricesAdmin
{
    /// <exception cref="ClientException">No price service, a bad request, or a refusal of the service.</exception>
    public static async Task<AccountingPricesClientResponse> HandleAsync(IAccountingPrices? prices,
                                                                        AccountingAdminClientRequest request,
                                                                        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (prices is null)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "The accounting prices are not available on this node.");

        var arguments = request.Prices ?? new AccountingPricesClientRequest();
        try
        {
            switch (request.Action)
            {
                case AccountingAdminAction.PricesImport:
                    if (arguments.Points.Count == 0)
                        throw new ClientException(ErrorCodes.InvalidOperation, "No prices to import.");

                    var import = await prices.ImportAsync(arguments.Currency, arguments.Points, ct);
                    return new AccountingPricesClientResponse(import.Currency) { Import = import };
                case AccountingAdminAction.PricesList:
                    var listed = await prices.ListAsync(arguments.Currency, arguments.Since, arguments.Until,
                                                        arguments.Limit, ct);
                    return new AccountingPricesClientResponse(arguments.Currency?.Trim().ToUpperInvariant()
                                                           ?? prices.Currency)
                    { Prices = listed };
                case AccountingAdminAction.PricesFetch:
                    if (arguments.Since is not { } since)
                        throw new ClientException(ErrorCodes.InvalidOperation, "prices fetch needs --since.");

                    var fetch = await prices.FetchAsync(since, arguments.Until, ct);
                    return new AccountingPricesClientResponse(fetch.Currency) { Fetch = fetch };
                default:
                    throw new ClientException(ErrorCodes.InvalidOperation,
                                              $"Unknown accounting prices action {request.Action}.");
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
    }
}