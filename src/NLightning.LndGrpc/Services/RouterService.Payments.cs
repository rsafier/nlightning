using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Lnrpc;
using Routerrpc;
using PaymentStatus = Domain.Payments.Enums.PaymentStatus;

public sealed partial class RouterService
{
    /// <summary>
    /// <c>SendPaymentV2</c>: pays <c>payment_request</c> (BOLT 11) with the node's payment service — retries and MPP
    /// within <c>timeout_seconds</c> (required, as LND), <c>fee_limit_msat</c>/<c>fee_limit_sat</c> (unset: the node's
    /// default limit, where LND would refuse every route that costs a fee), <c>max_parts</c>, <c>amt</c>/<c>amt_msat</c>
    /// for an amountless invoice and one <c>outgoing_chan_ids</c> entry — and streams the payment: <c>IN_FLIGHT</c>
    /// once it is recorded (not with <c>no_inflight_updates</c>), then <c>SUCCEEDED</c> or <c>FAILED</c> with its
    /// route. The payment goes on when the caller leaves. Refused: payments without an invoice (keysend: our keysend
    /// draws its own preimage), AMP, several outgoing channels, <c>last_hop_pubkey</c>, route hints and custom records
    /// beside the invoice, <c>max_shard_size_msat</c>. A paid or in-flight payment hash is <c>ALREADY_EXISTS</c>.
    /// </summary>
    public override async Task SendPaymentV2(SendPaymentRequest request, IServerStreamWriter<Payment> responseStream,
                                             ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PaymentRequest))
            throw Unimplemented("payments without payment_request (keysend, spontaneous) are not supported");
        if (request.Amp || request.DestCustomRecords.Count > 0 || request.RouteHints.Count > 0
         || request.LastHopPubkey.Length > 0 || request.MaxShardSizeMsat != 0 || request.FirstHopCustomRecords.Count > 0)
            throw Unimplemented("amp, dest_custom_records, route_hints, last_hop_pubkey, max_shard_size_msat and "
                              + "first_hop_custom_records are not supported");
        if (request.OutgoingChanIds.Count > 1)
            throw Unimplemented("one outgoing channel at most");
        if (request.TimeoutSeconds <= 0)
            throw InvalidArgument("timeout_seconds must be specified");
        if (request.FeeLimitSat != 0 && request.FeeLimitMsat != 0)
            throw InvalidArgument("fee_limit_sat and fee_limit_msat are mutually exclusive");
        if (request.Amt != 0 && request.AmtMsat != 0)
            throw InvalidArgument("amt and amt_msat are mutually exclusive");
        if (request.FeeLimitSat < 0 || request.FeeLimitMsat < 0 || request.Amt < 0 || request.AmtMsat < 0)
            throw InvalidArgument("amounts cannot be negative");

        Hash paymentHash;
        try
        {
            var decoded = Bolt11.Models.Invoice.Decode(request.PaymentRequest.Trim(), _nodeOptions.BitcoinNetwork);
            paymentHash = new Hash(Convert.FromHexString(decoded.PaymentHash?.ToString()
                                                       ?? throw new FormatException("no payment hash")));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw InvalidArgument($"invalid payment request: {e.Message}");
        }

        if (await _paymentService.GetPaymentAsync(paymentHash, context.CancellationToken) is { } existing)
        {
            if (existing.Status == PaymentStatus.Succeeded)
                throw new RpcException(new Status(StatusCode.AlreadyExists, "invoice is already paid"));
            if (existing.Status == PaymentStatus.InFlight)
                throw new RpcException(new Status(StatusCode.AlreadyExists, "payment is in transition"));
        }

        var options = new PayInvoiceOptions
        {
            Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds),
            MaxFee = request.FeeLimitMsat != 0 ? LightningMoney.MilliSatoshis((ulong)request.FeeLimitMsat)
                     : request.FeeLimitSat != 0 ? LightningMoney.Satoshis(request.FeeLimitSat)
                     : null,
            MaxParts = request.MaxParts > 0 ? (int)Math.Min(request.MaxParts, int.MaxValue) : null,
            OutgoingChannelId = request.OutgoingChanIds.Count == 1
                                    ? OutgoingChannel(request.OutgoingChanIds[0])
                                    : (ChannelId?)null
        };
        var amount = request.AmtMsat != 0 ? LightningMoney.MilliSatoshis((ulong)request.AmtMsat)
                                 : request.Amt != 0 ? LightningMoney.Satoshis(request.Amt)
                                 : null;

        // The payment never takes the caller's cancellation (as LND's): it retries for its window either way
        var payment = Task.Run(() => _paymentService.PayInvoiceAsync(request.PaymentRequest.Trim(), amount, options,
                                                                     CancellationToken.None));
        if (!request.NoInflightUpdates)
        {
            while (!payment.IsCompleted)
            {
                if (await _paymentService.GetPaymentAsync(paymentHash, context.CancellationToken) is
                    { Status: PaymentStatus.InFlight } recorded)
                {
                    await responseStream.WriteAsync(LightningService.ToLndPayment(recorded, false),
                                                    context.CancellationToken);
                    break;
                }

                await Task.WhenAny(payment, Task.Delay(s_poll, _timeProvider, context.CancellationToken));
            }
        }

        PayInvoiceResult result;
        try
        {
            result = await payment.WaitAsync(context.CancellationToken);
        }
        catch (ArgumentException e)
        {
            throw InvalidArgument(e.Message);
        }
        catch (InvalidOperationException e)
        {
            // Refused before a part went out (no route, no balance, ...): LND reports these as a FAILED payment when a
            // row exists, an error otherwise
            if (await _paymentService.GetPaymentAsync(paymentHash, CancellationToken.None) is
                { Status: PaymentStatus.Failed } failed)
            {
                await responseStream.WriteAsync(LightningService.ToLndPayment(failed, false),
                                                context.CancellationToken);
                return;
            }

            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }

        var final = await _paymentService.GetPaymentAsync(paymentHash, CancellationToken.None) ?? result.Payment;
        await responseStream.WriteAsync(LightningService.ToLndPayment(final, false), context.CancellationToken);
    }

    /// <summary>
    /// <c>TrackPaymentV2</c>: the payment's state now (<c>IN_FLIGHT</c> unless <c>no_inflight_updates</c>) and its
    /// outcome when it comes; a payment already final is answered with its outcome alone. Unknown:
    /// <c>NOT_FOUND</c> "payment isn't initiated".
    /// </summary>
    public override async Task TrackPaymentV2(TrackPaymentRequest request, IServerStreamWriter<Payment> responseStream,
                                              ServerCallContext context)
    {
        if (request.PaymentHash.Length != 32)
            throw InvalidArgument("payment_hash must be 32 bytes");

        var paymentHash = new Hash(request.PaymentHash.ToByteArray());
        using var subscription = _events?.Subscribe(64);
        var payment = await _paymentService.GetPaymentAsync(paymentHash, context.CancellationToken)
                   ?? throw new RpcException(new Status(StatusCode.NotFound, "payment isn't initiated"));
        if (payment.Status == PaymentStatus.InFlight && !request.NoInflightUpdates)
            await responseStream.WriteAsync(LightningService.ToLndPayment(payment, false), context.CancellationToken);

        while (payment.Status == PaymentStatus.InFlight)
        {
            await WaitForOutcomeAsync(subscription, paymentHash, context.CancellationToken);
            payment = await _paymentService.GetPaymentAsync(paymentHash, context.CancellationToken) ?? payment;
        }

        await responseStream.WriteAsync(LightningService.ToLndPayment(payment, false), context.CancellationToken);
    }

    /// <summary>
    /// <c>TrackPayments</c>: every payment's outcome as it happens (succeeded or failed, from the payment event bus),
    /// until the caller leaves; in-flight updates are not sent (the bus publishes outcomes only).
    /// </summary>
    public override async Task TrackPayments(TrackPaymentsRequest request, IServerStreamWriter<Payment> responseStream,
                                             ServerCallContext context)
    {
        if (_events is null)
            throw Unimplemented("the payment event bus is not available on this node");

        using var subscription = _events.Subscribe();
        try
        {
            await foreach (var paymentEvent in subscription.ReadAllAsync(context.CancellationToken))
            {
                if (paymentEvent is not (PaymentSucceededEvent or PaymentFailedEvent))
                    continue;

                if (await _paymentService.GetPaymentAsync(paymentEvent.PaymentHash, context.CancellationToken) is
                    { } payment)
                    await responseStream.WriteAsync(LightningService.ToLndPayment(payment, false),
                                                    context.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The caller left
        }
    }

    /// <summary>Returns at the payment's outcome event or after a poll interval, whichever is first.</summary>
    private async Task WaitForOutcomeAsync(IPaymentEventSubscription? subscription, Hash paymentHash,
                                           CancellationToken cancellationToken)
    {
        using var poll = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        poll.CancelAfter(TimeSpan.FromSeconds(1));
        if (subscription is null)
        {
            await Task.Delay(s_poll, _timeProvider, cancellationToken);
            return;
        }

        try
        {
            await foreach (var paymentEvent in subscription.ReadAllAsync(poll.Token))
            {
                if (paymentEvent.PaymentHash == paymentHash
                 && paymentEvent is PaymentSucceededEvent or PaymentFailedEvent)
                    return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The poll interval passed: the caller re-reads the payment
        }
    }

    /// <summary>The channel of an LND <c>chan_id</c> (our SCID or alias), as the payment's outgoing channel.</summary>
    private ChannelId OutgoingChannel(ulong chanId)
    {
        var scid = new ShortChannelId(chanId);
        return _channels.FindChannels(c => c.State == ChannelState.Open
                                        && (c.ShortChannelId == scid || (c.LocalAliases?.Contains(scid) ?? false)))
                        .FirstOrDefault()?.ChannelId
            ?? throw new RpcException(new Status(StatusCode.NotFound, $"outgoing channel {chanId} not found"));
    }

    private static RpcException InvalidArgument(string message) =>
        new(new Status(StatusCode.InvalidArgument, message));

    private static RpcException Unimplemented(string message) => new(new Status(StatusCode.Unimplemented, message));
}