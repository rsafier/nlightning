using System.Globalization;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Exceptions;
using Domain.Money;
using Infrastructure.Bitcoin.Services;
using Interfaces;

/// <summary>
/// Pays an external address from the on-chain wallet (ClientCommand 25, <c>withdraw</c>) through
/// <see cref="IWalletSpendService"/>.
/// </summary>
/// <remarks>
/// Errors: an invalid or wrong-network address is <see cref="ErrorCodes.InvalidAddress"/>; too little confirmed
/// money or a spend that would break the anchors reserve is <see cref="ErrorCodes.NotEnoughBalance"/>; a dust amount, a
/// fee rate out of bounds or halted chain processing is <see cref="ErrorCodes.InvalidOperation"/>. A fee rate in sat/vB
/// is converted with <see cref="FeeRateConverter"/> (x 250, floored at 253 sat/kw), so 1 sat/vB is 253 sat/kw.
/// </remarks>
public sealed class WithdrawClientHandler : IClientCommandHandler<WithdrawClientRequest, WithdrawClientResponse>
{
    /// <summary>The most satoshis that exist (21 million BTC).</summary>
    internal const ulong MaxAmountSat = 2_100_000_000_000_000;

    /// <summary>A sanity cap on the requested fee rate, in sat/vB (the service caps it again, per kiloweight).</summary>
    internal const ulong MaxSatPerVbyte = 1_000;

    private readonly ILogger<WithdrawClientHandler> _logger;
    private readonly IWalletSpendService _walletSpendService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.Withdraw;

    public WithdrawClientHandler(IWalletSpendService walletSpendService, ILogger<WithdrawClientHandler> logger)
    {
        _walletSpendService = walletSpendService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<WithdrawClientResponse> HandleAsync(WithdrawClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Address))
            throw new ClientException(ErrorCodes.InvalidAddress, "No destination address given.");
        if (request.AmountSat is 0)
            throw new ClientException(ErrorCodes.InvalidOperation, "The amount must be positive (or 'all').");
        if (request.AmountSat > MaxAmountSat)
            throw new ClientException(ErrorCodes.InvalidOperation, $"{request.AmountSat} sat is more than exists.");
        if (request.SatPerVbyte is 0)
            throw new ClientException(ErrorCodes.InvalidOperation, "The fee rate must be at least 1 sat/vB.");
        if (request.SatPerVbyte > MaxSatPerVbyte)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The fee rate {request.SatPerVbyte} sat/vB is above the cap of "
                                    + $"{MaxSatPerVbyte} sat/vB.");

        var labels = SourceLabelsGuard.Check(request.Label, request.Tags);
        var inputs = ParseUtxos(request.Utxos);
        var spendRequest = new WalletWithdrawRequest(
            request.Address,
            request.AmountSat is { } amount ? LightningMoney.Satoshis((long)amount) : null,
            request.SatPerVbyte is { } satPerVbyte
                ? LightningMoney.Satoshis(FeeRateConverter.SatPerVByteToSatPerKw(satPerVbyte))
                : null)
        {
            Labels = labels,
            Inputs = inputs
        };

        WalletWithdrawResult result;
        try
        {
            result = await _walletSpendService.WithdrawAsync(spendRequest, ct);
        }
        catch (WalletSpendException e)
        {
            throw new ClientException(e.Error is WalletSpendError.InvalidAddress or WalletSpendError.WrongNetwork
                                          ? ErrorCodes.InvalidAddress
                                          : ErrorCodes.InvalidOperation, e.Message);
        }
        catch (AnchorReserveException e)
        {
            throw new ClientException(ErrorCodes.NotEnoughBalance, e.Message);
        }
        catch (InsufficientFundsException e)
        {
            throw new ClientException(ErrorCodes.NotEnoughBalance,
                                      $"Not enough confirmed on-chain funds: {e.Required.Satoshi} sat needed with the "
                                    + $"fee, {e.Available.Satoshi} sat spendable.");
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("withdraw: {Amount} sat to {Address} (fee {Fee} sat, published {Published})",
                                   result.Amount.Satoshi, request.Address, result.Fee.Satoshi, result.Published);

        return new WithdrawClientResponse(result.TxId, result.Amount.Satoshi, result.Fee.Satoshi,
                                          result.Change.Satoshi, result.FeeRatePerKw.Satoshi, result.Weight,
                                          result.InputCount, result.AnchorReserve.Satoshi, result.Published);
    }

    /// <summary>The most outputs one withdraw may name (<c>--utxo</c>).</summary>
    internal const int MaxUtxos = 500;

    /// <summary>
    /// The chosen outputs (NL-1296): <c>txid:vout</c>, the txid in display order (as bitcoind and explorers print it);
    /// null when none is given.
    /// </summary>
    internal static IReadOnlyList<(TxId TxId, uint Index)>? ParseUtxos(IReadOnlyList<string>? utxos)
    {
        if (utxos is null || utxos.Count == 0)
            return null;
        if (utxos.Count > MaxUtxos)
            throw new ClientException(ErrorCodes.InvalidOperation, $"At most {MaxUtxos} --utxo outputs may be given.");

        var outpoints = new List<(TxId, uint)>(utxos.Count);
        foreach (var text in utxos)
        {
            if (!TryParseOutpoint(text, out var outpoint))
                throw new ClientException(ErrorCodes.InvalidOperation,
                                          $"Invalid output '{text}': expected <txid>:<vout>.");
            if (outpoints.Contains(outpoint))
                throw new ClientException(ErrorCodes.InvalidOperation, $"Output '{text}' is given twice.");
            outpoints.Add(outpoint);
        }

        return outpoints;
    }

    internal static bool TryParseOutpoint(string? text, out (TxId TxId, uint Index) outpoint)
    {
        outpoint = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var separator = text.LastIndexOf(':');
        if (separator != 64
         || !uint.TryParse(text.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture,
                           out var index))
            return false;

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(text.AsSpan(0, separator));
        }
        catch (FormatException)
        {
            return false;
        }

        // Display order is the reverse of the internal byte order TxId holds
        Array.Reverse(bytes);
        outpoint = (new TxId(bytes), index);
        return true;
    }
}