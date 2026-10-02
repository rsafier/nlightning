using System.Globalization;
using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Accounting.Financial.Lots;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;

/// <summary>
/// The arguments of <c>accounting lots import</c> (ClientCommand 45 key 13, D-A9; NL-602 A3-T4): the whole lot file in
/// one request (an import replaces every imported lot). Costs travel as invariant decimal text. Keys are append-only.
/// </summary>
[MessagePackObject]
public sealed class AccountingLotsIpcRequest
{
    /// <summary>The lots' currency, or null for the financial book's.</summary>
    [Key(0)] public string? Currency { get; set; }

    /// <summary>The lots, in file order.</summary>
    [Key(1)] public List<AccountingLotRowIpc>? Rows { get; set; }

    /// <exception cref="ClientException">Too many rows, a bad cost or a time out of range.</exception>
    public AccountingLotsClientRequest ToClientRequest()
    {
        if (Rows is { Count: > AccountingLotCsv.MaxLots })
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"At most {AccountingLotCsv.MaxLots} lots per import ({Rows.Count} sent).");

        var lots = new List<AccountingLotPoint>(Rows?.Count ?? 0);
        for (var i = 0; i < (Rows?.Count ?? 0); i++)
        {
            var row = Rows![i];
            if (row is null
             || !decimal.TryParse(row.Cost, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                                  out var cost))
                throw new ClientException(ErrorCodes.InvalidOperation, $"Lot {i + 1} has no valid cost.");
            if (row.TimeUnixSeconds < 0 || row.TimeUnixSeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                throw new ClientException(ErrorCodes.InvalidOperation, $"The time of lot {i + 1} is out of range.");

            lots.Add(new AccountingLotPoint(DateTimeOffset.FromUnixTimeSeconds(row.TimeUnixSeconds), row.Msat, cost));
        }

        return new AccountingLotsClientRequest { Currency = Currency, Lots = lots };
    }
}

/// <summary>One lot of an import: its acquisition time (Unix seconds), its msat and its total cost as invariant
/// decimal text.</summary>
[MessagePackObject]
public sealed class AccountingLotRowIpc
{
    [Key(0)] public long TimeUnixSeconds { get; set; }

    [Key(1)] public long Msat { get; set; }

    [Key(2)] public string Cost { get; set; } = string.Empty;
}