using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Bitcoin.Enums;
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
        var spendRequest = new WalletWithdrawRequest(
            request.Address,
            request.AmountSat is { } amount ? LightningMoney.Satoshis((long)amount) : null,
            request.SatPerVbyte is { } satPerVbyte
                ? LightningMoney.Satoshis(FeeRateConverter.SatPerVByteToSatPerKw(satPerVbyte))
                : null)
        {
            Labels = labels
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
}