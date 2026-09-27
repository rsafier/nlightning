using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Channels.Splicing;
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
/// refused rule (<see cref="InvalidOperationException"/>) or the service's own <see cref="TimeoutException"/> is
/// <see cref="ErrorCodes.InvalidOperation"/> with the service's reason; a wallet too small for a splice-in or the
/// anchors reserve (<see cref="InsufficientFundsException"/>) is <see cref="ErrorCodes.NotEnoughBalance"/>; an address
/// the service refuses (a plain <see cref="ArgumentException"/> whose <c>ParamName</c> is
/// <c>nameof(SpliceRequest.SpliceOutAddress)</c>) is <see cref="ErrorCodes.InvalidAddress"/>, any other
/// <see cref="ArgumentException"/> is <see cref="ErrorCodes.InvalidOperation"/>.</para>
/// <para>Wait: the call returns when the negotiation is signed or ended, or after the handler's wait (default
/// <see cref="DefaultMaxWait"/>). The splice is not cancelled then: the response carries the state of the negotiation
/// this call started (<c>AwaitingQuiescence</c> with no txid while the service has not registered it; a previous
/// splice's negotiation is never reported), the splice goes on and its outcome is logged when it ends (it holds one IPC pipe instance, so the wait is bounded as
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
        var startedAt = DateTimeOffset.UtcNow;
        Task<SpliceResult>? startTask = null;
        SpliceResult result;
        try
        {
            startTask = _spliceService.StartAsync(request, ct);
            result = await startTask.WaitAsync(_maxWait, ct);
        }
        catch (TimeoutException) when (startTask is { IsCompleted: false })
        {
            // Our wait expired, the splice goes on: report how far it got, and log how it ends
            ObserveOutcome(startTask, channelId, request);
            var negotiation = _spliceService.GetNegotiation(channelId);
            if (negotiation is not null && !IsOurs(negotiation, request, startedAt))
                negotiation = null;

            var state = negotiation?.State ?? SpliceNegotiationState.AwaitingQuiescence;
            _logger.LogInformation("splice on {ChannelId}: still {State} after {Wait}", channelId, state, _maxWait);
            return new SpliceClientResponse(channelId, state) { SpliceTxId = negotiation?.SpliceTxId };
        }
        catch (TimeoutException e)
        {
            // The service's own timeout (a quiescence that never came, say) ended the splice
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }
        catch (KeyNotFoundException e)
        {
            throw new ClientException(ErrorCodes.InvalidChannel, $"Unknown channel {channelId}.", e);
        }
        catch (InsufficientFundsException e)
        {
            throw new ClientException(ErrorCodes.NotEnoughBalance, e.Message, e);
        }
        catch (ArgumentException e) when (address is not null && IsAddressRefusal(e))
        {
            throw new ClientException(ErrorCodes.InvalidAddress, e.Message, e);
        }
        catch (ArgumentException e)
        {
            // Not about the address: a bound the service refused (the amount, the feerate)
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
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

    /// <summary>
    /// The service refused the splice-out address: an <see cref="ArgumentException"/> itself (not a subclass such as
    /// <see cref="ArgumentNullException"/>) naming <see cref="SpliceRequest.SpliceOutAddress"/>.
    /// </summary>
    private static bool IsAddressRefusal(ArgumentException e) =>
        e.GetType() == typeof(ArgumentException) && e.ParamName == nameof(SpliceRequest.SpliceOutAddress);

    /// <summary>
    /// The stored negotiation belongs to this call: one we initiated, created after the call started, with a
    /// contribution in the request's direction. A previous splice's (signed) negotiation is not reported as this one.
    /// </summary>
    private static bool IsOurs(SpliceNegotiationModel negotiation, SpliceRequest request, DateTimeOffset startedAt) =>
        negotiation.IsInitiator
     && negotiation.CreatedAt >= startedAt
     && Math.Sign(negotiation.LocalContributionSatoshis) == Math.Sign(request.ContributionSatoshis);

    /// <summary>Logs how a splice the caller stopped waiting for ends, so a late failure is not lost.</summary>
    private void ObserveOutcome(Task<SpliceResult> startTask, ChannelId channelId, SpliceRequest request)
    {
        _ = startTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                _logger.LogWarning(t.Exception?.GetBaseException(),
                                   "splice on {ChannelId} (contribution {Contribution} sat) failed after the IPC wait",
                                   channelId, request.ContributionSatoshis);
            else if (t.IsCanceled)
                _logger.LogWarning("splice on {ChannelId} (contribution {Contribution} sat) was cancelled after the "
                                 + "IPC wait", channelId, request.ContributionSatoshis);
            else
                _logger.LogInformation("splice on {ChannelId} (contribution {Contribution} sat) ended after the IPC "
                                     + "wait: {State}, txid {TxId}, new capacity {Capacity} sat{Reason}", channelId,
                                       request.ContributionSatoshis, t.Result.State, t.Result.SpliceTxId,
                                       t.Result.NewCapacitySatoshis,
                                       t.Result.FailureReason is null ? string.Empty : $" ({t.Result.FailureReason})");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}