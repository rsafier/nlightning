using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Exceptions;
using Interfaces;

/// <summary>
/// Splices wallet funds into a channel (ClientCommand 33, <c>splicein</c>) through <see cref="ISpliceService"/>.
/// </summary>
/// <remarks>See <see cref="SpliceCommand"/> for the checks, the wait and the error codes.</remarks>
public sealed class SpliceInClientHandler : IClientCommandHandler<SpliceInClientRequest, SpliceClientResponse>
{
    private readonly SpliceCommand _command;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.SpliceIn;

    public SpliceInClientHandler(ISpliceService spliceService, ILogger<SpliceInClientHandler> logger,
                                 TimeSpan? maxWait = null)
    {
        _command = new SpliceCommand(spliceService, logger, maxWait ?? SpliceCommand.DefaultMaxWait);
    }

    /// <inheritdoc/>
    public Task<SpliceClientResponse> HandleAsync(SpliceInClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _command.RunAsync(request.ChannelId, request.AmountSat, request.FeeRatePerKw, null,
                                 request.ToSpliceRequest, ct);
    }
}

/// <summary>
/// Splices funds out of a channel to an address or our wallet (ClientCommand 34, <c>spliceout</c>) through
/// <see cref="ISpliceService"/>.
/// </summary>
/// <remarks>See <see cref="SpliceCommand"/> for the checks, the wait and the error codes.</remarks>
public sealed class SpliceOutClientHandler : IClientCommandHandler<SpliceOutClientRequest, SpliceClientResponse>
{
    private readonly SpliceCommand _command;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.SpliceOut;

    public SpliceOutClientHandler(ISpliceService spliceService, ILogger<SpliceOutClientHandler> logger,
                                  TimeSpan? maxWait = null)
    {
        _command = new SpliceCommand(spliceService, logger, maxWait ?? SpliceCommand.DefaultMaxWait);
    }

    /// <inheritdoc/>
    public Task<SpliceClientResponse> HandleAsync(SpliceOutClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Address is not null && string.IsNullOrWhiteSpace(request.Address))
            throw new ClientException(ErrorCodes.InvalidAddress,
                                      "The splice-out address is empty (leave it out for our wallet).");

        return _command.RunAsync(request.ChannelId, request.AmountSat, request.FeeRatePerKw, request.Address,
                                 request.ToSpliceRequest, ct);
    }
}

/// <summary>
/// What <c>splicein</c> and <c>spliceout</c> share: the bounds checked before <see cref="ISpliceService"/> is called,
/// the bounded wait and the mapping of the service's refusals to IPC error codes.
/// </summary>
/// <remarks>
/// <para>Checks: the amount is 1 sat up to the 21M BTC supply; a feerate, when given, is
/// <see cref="MinFeeRatePerKw"/> (BOLT 3's floor) to <see cref="MaxFeeRatePerKw"/> (1,000 sat/vB, the <c>withdraw</c>
/// cap); everything else (35 and 63 negotiated, the channel <c>Open</c>, no <c>shutdown</c>, no unlocked splice, the
/// balance or the wallet) is the service's rule set, SP-S-01/02.</para>
/// <para>Errors: an unknown channel (<see cref="KeyNotFoundException"/>) is <see cref="ErrorCodes.InvalidChannel"/>; a
/// refused rule (<see cref="InvalidOperationException"/>) is <see cref="ErrorCodes.InvalidOperation"/> with the
/// service's reason; a wallet too small for a splice-in or the anchors reserve
/// (<see cref="InsufficientFundsException"/>) is <see cref="ErrorCodes.NotEnoughBalance"/>; an address the service
/// refuses (<see cref="ArgumentException"/>) is <see cref="ErrorCodes.InvalidAddress"/>.</para>
/// <para>Wait: the call returns when the negotiation is signed or ended, or after the handler's wait (default
/// <see cref="DefaultMaxWait"/>). The splice is not cancelled then: the response carries the negotiation's state at
/// that moment, and the splice goes on (it holds one IPC pipe instance, so the wait is bounded as
/// <c>closechannel</c>'s is).</para>
/// </remarks>
internal sealed class SpliceCommand
{
    /// <summary>The longest a <c>splicein</c>/<c>spliceout</c> call waits for the negotiation.</summary>
    internal static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(120);

    /// <summary>The most satoshis that exist (21 million BTC).</summary>
    internal const ulong MaxAmountSat = 2_100_000_000_000_000;

    /// <summary>BOLT 3's feerate floor, in sat/kw.</summary>
    internal const uint MinFeeRatePerKw = 253;

    /// <summary>A sanity cap on the requested feerate: 1,000 sat/vB, in sat/kw.</summary>
    internal const uint MaxFeeRatePerKw = 250_000;

    private readonly ISpliceService _spliceService;
    private readonly ILogger _logger;
    private readonly TimeSpan _maxWait;

    internal SpliceCommand(ISpliceService spliceService, ILogger logger, TimeSpan maxWait)
    {
        _spliceService = spliceService;
        _logger = logger;
        _maxWait = maxWait;
    }

    internal async Task<SpliceClientResponse> RunAsync(ChannelId channelId, ulong amountSat, uint? feeRatePerKw,
                                                       string? address, Func<SpliceRequest> toSpliceRequest,
                                                       CancellationToken ct)
    {
        if (amountSat is 0)
            throw new ClientException(ErrorCodes.InvalidOperation, "The amount must be at least 1 sat.");
        if (amountSat > MaxAmountSat)
            throw new ClientException(ErrorCodes.InvalidOperation, $"{amountSat} sat is more than exists.");
        if (feeRatePerKw is < MinFeeRatePerKw or > MaxFeeRatePerKw)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The feerate {feeRatePerKw} sat/kw is outside {MinFeeRatePerKw} to "
                                    + $"{MaxFeeRatePerKw} sat/kw.");

        var request = toSpliceRequest();
        SpliceResult result;
        try
        {
            result = await _spliceService.StartAsync(request, ct).WaitAsync(_maxWait, ct);
        }
        catch (TimeoutException)
        {
            // The splice goes on; report how far it got
            var negotiation = _spliceService.GetNegotiation(channelId);
            _logger.LogInformation("splice on {ChannelId}: still {State} after {Wait}", channelId,
                                   negotiation?.State ?? SpliceNegotiationState.AwaitingQuiescence, _maxWait);
            return new SpliceClientResponse(channelId,
                                            negotiation?.State ?? SpliceNegotiationState.AwaitingQuiescence)
            {
                SpliceTxId = negotiation?.SpliceTxId
            };
        }
        catch (KeyNotFoundException e)
        {
            throw new ClientException(ErrorCodes.InvalidChannel, $"Unknown channel {channelId}.", e);
        }
        catch (InsufficientFundsException e)
        {
            throw new ClientException(ErrorCodes.NotEnoughBalance, e.Message, e);
        }
        catch (ArgumentException e) when (address is not null)
        {
            throw new ClientException(ErrorCodes.InvalidAddress, e.Message, e);
        }
        catch (InvalidOperationException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("splice on {ChannelId}: contribution {Contribution} sat, {State}, txid {TxId}, "
                                 + "new capacity {Capacity} sat{Reason}", channelId, request.ContributionSatoshis,
                                   result.State, result.SpliceTxId, result.NewCapacitySatoshis,
                                   result.FailureReason is null ? string.Empty : $" ({result.FailureReason})");

        return new SpliceClientResponse(result.ChannelId, result.State)
        {
            SpliceTxId = result.SpliceTxId,
            NewCapacitySat = result.NewCapacitySatoshis,
            FailureReason = result.FailureReason
        };
    }
}