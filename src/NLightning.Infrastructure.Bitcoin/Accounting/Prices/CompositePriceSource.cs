using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Accounting.Prices;

using Domain.Accounting.Financial;
using Domain.Accounting.Prices;

/// <summary>
/// The configured price sources in order (<c>Accounting:Prices:Source=Both</c>: the price file, then the HTTP source;
/// NL-602 A3-T2): the first answer within <c>MaxAge</c> of the asked time wins; otherwise the most recent answer at or
/// before it (possibly too old to value with) or null.
/// </summary>
public sealed class CompositePriceSource : IPriceSource
{
    private readonly TimeSpan _maxAge;
    private readonly IReadOnlyList<IPriceSource> _sources;

    public CompositePriceSource(IReadOnlyList<IPriceSource> sources, IOptions<AccountingPriceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        _sources = sources;
        _maxAge = options.Value.MaxAge;
    }

    /// <summary>The sources, in the order they are asked.</summary>
    public IReadOnlyList<IPriceSource> Sources => _sources;

    /// <inheritdoc />
    public async Task<AccountingPrice?> GetPriceAsync(string currency, DateTimeOffset time,
                                                      CancellationToken cancellationToken = default)
    {
        AccountingPrice? best = null;
        foreach (var source in _sources)
        {
            var price = await source.GetPriceAsync(currency, time, cancellationToken);
            if (price is null)
                continue;

            if (AccountingValuation.IsUsable(price.Time, time, _maxAge))
                return price;

            if (price.Time <= time && (best is null || price.Time > best.Time))
                best = price;
        }

        return best;
    }
}