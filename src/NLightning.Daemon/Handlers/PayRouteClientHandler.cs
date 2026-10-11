using System.Globalization;

namespace NLightning.Daemon.Handlers;

using Domain.Bitcoin.Constants;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// Pays over exactly the routes the caller supplied through <see cref="IPaymentService.PayRouteAsync"/>
/// (ClientCommand 48, NL-1082) and reports every route's outcome.
/// </summary>
/// <remarks>
/// The request's identity is either a BOLT 11 invoice or a raw payment hash with an optional secret and an explicit
/// total (the LND <c>SendToRoute</c> form); exactly one of the two. The wait is bounded by
/// <see cref="PayRouteClientRequest.TimeoutSeconds"/> (default 60, at most <see cref="MaxTimeoutSeconds"/>). When it
/// ends first, the routes still resolving are reported <c>InFlight</c>: their HTLCs stay offered and resolve later
/// (see <c>ListPayments</c>). The cap is kept short because the call holds one IPC pipe instance for the whole wait
/// (see <c>NamedPipeIpcService.MaxServerInstances</c>); for a longer wait, poll <c>ListPayments</c>.
/// <para>Only the exceptions <see cref="IPaymentService.PayRouteAsync"/> documents as "nothing persisted" become
/// <see cref="ErrorCodes.InvalidOperation"/>: <see cref="ArgumentException"/> (an invalid identity or route set) and a
/// plain <see cref="InvalidOperationException"/> (duplicate hash). Its subclasses (for example
/// <see cref="ObjectDisposedException"/> at shutdown) and every other exception are not guaranteed to happen before
/// the HTLCs were offered, so they become <see cref="ErrorCodes.ServerError"/> with a hint to check
/// <c>ListPayments</c>.</para>
/// <para>While the chain monitor's processing is halted (NL-216) the call is refused with
/// <see cref="ErrorCodes.InvalidOperation"/> before anything is sent (the channel operations refuse every HTLC offer
/// then too; see <see cref="ChainProcessingHalt"/>).</para>
/// <para>NL-1082: each route's <see cref="PayRouteRouteClientInfo.FirstHopChannel"/> names the channel of ours the
/// first HTLC leaves through (a channel id, or a short channel id or alias of one of our channels, like
/// <c>payinvoice --out</c>); a value that names none of our channels is
/// <see cref="ErrorCodes.InvalidOperation"/>.</para>
/// </remarks>
public sealed class PayRouteClientHandler
    : IClientCommandHandler<PayRouteClientRequest, PayRouteClientResponse>
{
    /// <summary>
    /// The longest wait a request may ask for.
    /// </summary>
    public const uint MaxTimeoutSeconds = 300;

    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly IPaymentService _paymentService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.PayRoute;

    public PayRouteClientHandler(IPaymentService paymentService, IBlockchainMonitor? blockchainMonitor = null,
                                 IChannelMemoryRepository? channelMemoryRepository = null)
    {
        _blockchainMonitor = blockchainMonitor;
        _channelMemoryRepository = channelMemoryRepository;
        _paymentService = paymentService;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">The request is invalid (no identity or both, no route, the timeout out of
    /// range, an invalid label or tag, a first-hop channel that names none of our channels), the invoice was rejected
    /// (malformed, expired, other network, amount inconsistent) or a payment for the hash is already in flight or
    /// succeeded (<see cref="ErrorCodes.InvalidOperation"/>). Nothing was sent in those cases. Any other failure of
    /// the payment service is <see cref="ErrorCodes.ServerError"/>: the payment may be stored <c>InFlight</c> with its
    /// HTLCs offered, so the message asks the user to check <c>ListPayments</c>.</exception>
    public Task<PayRouteClientResponse> HandleAsync(PayRouteClientRequest request, CancellationToken ct) =>
        HandleAsync(request, PayRouteAttachMode.Never, ct);

    /// <summary>
    /// <see cref="HandleAsync(PayRouteClientRequest, CancellationToken)"/> with the call's relation to a payment of the
    /// hash in flight: <see cref="PayRouteAttachMode.Required"/> for <c>payroute --attach</c> (NL-1276, ClientCommand
    /// 56), whose refusals (nothing in flight to attach to, a mismatched identity, too late) are
    /// <see cref="ErrorCodes.InvalidOperation"/> with nothing sent.
    /// </summary>
    internal async Task<PayRouteClientResponse> HandleAsync(PayRouteClientRequest request, PayRouteAttachMode attach,
                                                            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hasInvoice = !string.IsNullOrWhiteSpace(request.Bolt11);
        var hasHash = request.PaymentHash is not null;
        if (hasInvoice == hasHash)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      "Exactly one of the invoice (bolt11) or the payment hash must be given.");
        if (request.Routes.Count == 0)
            throw new ClientException(ErrorCodes.InvalidOperation, "At least one route is required.");
        if (request.Routes.Any(r => string.IsNullOrWhiteSpace(r.FirstHopChannel)))
            throw new ClientException(ErrorCodes.InvalidOperation, "Each route needs a first hop channel.");
        if (request.TimeoutSeconds is 0 or > MaxTimeoutSeconds)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The timeout must be between 1 and {MaxTimeoutSeconds} seconds.");
        var labels = SourceLabelsGuard.Check(request.Label, request.Tags);
        if (_blockchainMonitor is { IsChainProcessingHalted: true })
            throw new ClientException(ErrorCodes.InvalidOperation, ChainProcessingHalt.Refusal("payroute"));

        var serviceRequest = new PayRouteRequest
        {
            Bolt11 = hasInvoice ? request.Bolt11!.Trim() : null,
            PaymentHash = request.PaymentHash,
            PaymentSecret = request.PaymentSecret,
            TotalAmount = request.TotalMsatMsat is { } totalMsat ? LightningMoney.MilliSatoshis(totalMsat) : null,
            Routes = request.Routes.Select(ToServiceRoute).ToList(),
            Attach = attach
        };
        var options = new PayInvoiceOptions
        {
            Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds),
            MaxFee = request.MaxFeeMsat is { } maxFeeMsat ? LightningMoney.MilliSatoshis(maxFeeMsat) : null,
            Labels = labels
        };

        try
        {
            var result = await _paymentService.PayRouteAsync(serviceRequest, options, ct);
            return new PayRouteClientResponse
            {
                Payment = PaymentInfoClientResponse.FromModel(result.Payment),
                RouteOutcomes = result.Outcomes.Select(o => new RouteOutcomeClientInfo(
                                                        o.Index, o.Status, o.HtlcId, o.FailureCode,
                                                        o.FailureSourceIndex, o.FailureReason))
                                                   .ToList()
            };
        }
        catch (ArgumentException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, $"Invalid request: {e.Message}", e);
        }
        catch (InvalidOperationException e) when (e.GetType() == typeof(InvalidOperationException))
        {
            // The contract's "already in flight or succeeded" refusal; nothing was persisted or sent
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message, e);
        }
        catch (Exception e) when (e is not OperationCanceledException and not ClientException)
        {
            throw new ClientException(ErrorCodes.ServerError,
                                      $"The payment failed with an unexpected error: {e.Message}. It may still be "
                                    + "in flight: check listpayments before paying again.", e);
        }
    }

    private PayRouteRoute ToServiceRoute(PayRouteRouteClientInfo route)
    {
        return new PayRouteRoute(
            ResolveChannel(route.FirstHopChannel, "first hop")
           ?? throw new ClientException(ErrorCodes.InvalidOperation, "Each route needs a first hop channel."),
            LightningMoney.MilliSatoshis(route.FirstHopAmountMsat),
            route.FirstHopCltv,
            route.Hops.Select(h => new PayRouteHop(
                               h.NodeId,
                               h.OutgoingShortChannelId is { } scid ? new ShortChannelId(scid) : (ShortChannelId?)null,
                               LightningMoney.MilliSatoshis(h.AmountToForwardMsat),
                               h.OutgoingCltvValue))
                        .ToList());
    }

    /// <summary>
    /// The channel a route's first hop names (NL-609, as <c>payinvoice</c> resolves its pins): a channel id
    /// (64 hex characters) as is, or the channel of ours with that short channel id or alias.
    /// </summary>
    /// <exception cref="ClientException">The value names none of our channels.</exception>
    internal ChannelId? ResolveChannel(string? value, string side)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();
        if (value.Length == 64)
        {
            try
            {
                return new ChannelId(Convert.FromHexString(value));
            }
            catch (FormatException)
            {
                // Not hex: tried as a short channel id below, which fails with the message
            }
        }

        var parts = value.Split('x');
        if (parts.Length == 3
         && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var block) && block <= 0xFFFFFF
         && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tx) && tx <= 0xFFFFFF
         && ushort.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var output))
        {
            var scid = new ShortChannelId(block, tx, output);
            var channel = _channelMemoryRepository?
                         .FindChannels(c => c.ShortChannelId == scid || c.RemoteAlias == scid
                                         || c.LocalAliases?.Contains(scid) == true)
                         .FirstOrDefault();
            if (channel is not null)
                return channel.ChannelId;

            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The {side} channel {value} is not one of our channels.");
        }

        throw new ClientException(ErrorCodes.InvalidOperation,
                                  $"Invalid {side} channel '{value}': expected a channel id (64 hex characters) or a "
                                + "short channel id (BLOCKxTXxOUTPUT).");
    }
}