using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Channels.DualFunding.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Interfaces;

/// <summary>
/// <c>bumpopen</c> (ClientCommand 38, lane dfrbf): RBF of an unconfirmed dual-funded open, as its opener or its
/// accepter (NL-530), through
/// <see cref="IDualFundedOpenService.BumpAsync(Domain.Channels.ValueObjects.ChannelId, uint, LightningMoney?, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// Checks here: a feerate of <see cref="MinFeeRatePerKw"/> (BOLT 3's floor) to <see cref="MaxFeeRatePerKw"/> (1,000
/// sat/vB, the <c>withdraw</c> cap) and a contribution, when given, of 0 up to the 21M BTC supply (0 is an accepter
/// that stops contributing; the service refuses it for the opener); everything else (the channel is a dual-funded open
/// waiting for its funding, RBF allowed by <c>Node:DualFund:AllowRbf</c>, the IT-RBF-01 feerate floor, the new
/// contribution, our inputs) is the service's. A refusal or a failed attempt (the
/// peer's <c>tx_abort</c>, a timeout) is <see cref="ErrorCodes.InvalidOperation"/> with the reason; a node without
/// dual funding answers "not available".
/// </remarks>
public sealed class BumpOpenClientHandler : IClientCommandHandler<BumpOpenClientRequest, BumpOpenClientResponse>
{
    /// <summary>BOLT 3's feerate floor, in sat/kw.</summary>
    internal const uint MinFeeRatePerKw = 253;

    /// <summary>1,000 sat/vB in sat/kw, the largest feerate a command takes.</summary>
    internal const uint MaxFeeRatePerKw = 250_000;

    /// <summary>The most satoshis that exist (21 million BTC).</summary>
    internal const ulong MaxAmountSat = 2_100_000_000_000_000;

    private readonly IDualFundedOpenService? _dualFundedOpenService;
    private readonly ILogger<BumpOpenClientHandler> _logger;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.BumpOpen;

    public BumpOpenClientHandler(ILogger<BumpOpenClientHandler> logger,
                                 IDualFundedOpenService? dualFundedOpenService = null)
    {
        _logger = logger;
        _dualFundedOpenService = dualFundedOpenService;
    }

    /// <inheritdoc/>
    public async Task<BumpOpenClientResponse> HandleAsync(BumpOpenClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_dualFundedOpenService is null)
            throw new ClientException(ErrorCodes.InvalidOperation, "Dual-funded opens are not available on this node");
        if (request.FeeRatePerKw is < MinFeeRatePerKw or > MaxFeeRatePerKw)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The feerate must be {MinFeeRatePerKw} to {MaxFeeRatePerKw} sat/kw");
        if (request.ContributionSat is > MaxAmountSat)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The contribution must be 0 to {MaxAmountSat} sat");

        var contribution = request.ContributionSat is { } sat ? LightningMoney.Satoshis(sat) : null;
        Domain.Channels.DualFunding.Models.DualFundedOpenResult result;
        try
        {
            result = await _dualFundedOpenService.BumpAsync(request.ChannelId, request.FeeRatePerKw, contribution, ct);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }

        if (result.FailureReason is not null || result.FundingTxId is not { } fundingTxId)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The RBF of the open failed: {result.FailureReason ?? "no funding transaction"}");

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Dual-funded open {ChannelId} bumped to funding {TxId} at {Feerate} sat/kw",
                                   request.ChannelId, fundingTxId, request.FeeRatePerKw);
        return new BumpOpenClientResponse(result.ChannelId, fundingTxId);
    }
}