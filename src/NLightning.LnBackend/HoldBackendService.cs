using System.Security.Cryptography;
using System.Threading.Channels;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.LnBackend;

using Domain.Accounting.Labels;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Bolt11Invoice = NLightning.Bolt11.Models.Invoice;

/// <summary>
/// The <c>hold.Hold</c> service of the vendored Boltz hold proto, served over this node's hold invoices (NL-995):
/// what an unmodified ASP (Second's captaind, the Bark server) drives a Lightning node through. Receive: create or
/// inject a hold invoice by payment hash, stream its <c>UNPAID → ACCEPTED → PAID | CANCELLED</c> states, settle with
/// the preimage or cancel. The gRPC field numbers are the wire contract with their Rust client.
/// </summary>
/// <remarks>
/// State mapping: our <see cref="InvoiceStatus.Open"/> is <c>UNPAID</c>, <see cref="InvoiceStatus.Held"/> is
/// <c>ACCEPTED</c> (their word for "HTLC held"), <see cref="InvoiceStatus.Settled"/> is <c>PAID</c>,
/// <see cref="InvoiceStatus.Canceled"/> is <c>CANCELLED</c>; a legacy <see cref="InvoiceStatus.Accepted"/> (its
/// fulfill is already persisted) is reported <c>PAID</c>. <c>OnionMessages</c> (the BOLT 12 fetch path) is not
/// implemented yet and answers <c>UNIMPLEMENTED</c>; hold, settle, cancel, pay tracking and BOLT 11 receive work
/// without it.
/// </remarks>
public sealed class HoldBackendService : Hold.Hold.HoldBase
{
    private const int ListPage = 64;
    private static readonly TimeSpan s_cancelRecheck = TimeSpan.FromSeconds(5);

    private readonly ILogger<HoldBackendService> _logger;
    private readonly IInvoiceService _invoiceService;
    private readonly IHoldInvoiceService _holdInvoiceService;
    private readonly IPaymentEventSource? _events;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IChannelMemoryRepository _channelMemory;
    private readonly Domain.Node.Options.NodeOptions _nodeOptions;

    public HoldBackendService(ILogger<HoldBackendService> logger, IInvoiceService invoiceService,
                              IHoldInvoiceService holdInvoiceService, IServiceScopeFactory scopeFactory,
                              IChannelMemoryRepository channelMemory,
                              Microsoft.Extensions.Options.IOptions<Domain.Node.Options.NodeOptions> nodeOptions,
                              IPaymentEventSource? events = null)
    {
        _logger = logger;
        _invoiceService = invoiceService;
        _holdInvoiceService = holdInvoiceService;
        _scopeFactory = scopeFactory;
        _channelMemory = channelMemory;
        _nodeOptions = nodeOptions.Value;
        _events = events;
    }

    public override Task<Hold.GetInfoResponse> GetInfo(Hold.GetInfoRequest request, ServerCallContext context)
    {
        return Task.FromResult(new Hold.GetInfoResponse
        {
            Version = typeof(HoldBackendService).Assembly.GetName().Version?.ToString(3) ?? "0.0.1"
        });
    }

    /// <summary>The label every invoice this backend creates carries (so List can find exactly its own).</summary>
    private const string Label = "ln-backend";

    /// <summary><see cref="Label"/> as stored on the row (an empty tag list).</summary>
    private static readonly SourceLabels s_labelled = SourceLabels.Create(Label, []);

    public override async Task<Hold.InvoiceResponse> Invoice(Hold.InvoiceRequest request, ServerCallContext context)
    {
        var amount = LightningMoney.MilliSatoshis(request.AmountMsat);
        if (amount.IsZero)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "amount_msat must be positive"));
        var description = request.DescriptionCase switch
        {
            Hold.InvoiceRequest.DescriptionOneofCase.Memo => request.Memo,
            Hold.InvoiceRequest.DescriptionOneofCase.Hash => Convert.ToHexString(request.Hash.ToByteArray()).ToLowerInvariant(),
            _ => string.Empty
        };
        // captaind sizes c for the time it needs before it settles (its HTLC delta plus the user's): the HTLC must
        // carry it, so the invoice says it and our final hop enforces it (NL-1149)
        var minFinalCltv = request.HasMinFinalCltvExpiry && request.MinFinalCltvExpiry > 0
                               ? CheckedCltvDelta(request.MinFinalCltvExpiry, "min_final_cltv_expiry")
                               : (ushort?)null;
        InvoiceModel invoice;
        try
        {
            invoice = await _invoiceService.CreateHoldInvoiceAsync(
                          new Hash(request.PaymentHash.ToByteArray()), amount, description,
                          request.HasExpiry && request.Expiry > 0 ? (uint)request.Expiry : null, minFinalCltv,
                          s_labelled, context.CancellationToken);
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Backend hold invoice {PaymentHash} for {AmountMsat} msat",
                                   invoice.PaymentHash, request.AmountMsat);
        return new Hold.InvoiceResponse { Bolt11 = invoice.Bolt11 };
    }

    public override async Task<Hold.InjectResponse> Inject(Hold.InjectRequest request, ServerCallContext context)
    {
        Bolt11Invoice invoice;
        try
        {
            invoice = Bolt11Invoice.Decode(request.Invoice, _nodeOptions.BitcoinNetwork);
        }
        catch (Exception e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"not a decodable invoice: {e.Message}"));
        }

        var amount = invoice.Amount is { } invoiceAmount && !invoiceAmount.IsZero
                          ? invoiceAmount
                          : throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                              "an injected invoice must carry an amount"));
        var paymentHashHex = invoice.PaymentHash?.ToString()
                          ?? throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                              "the invoice has no payment hash"));
        // The injected invoice's own c, raised to the request's min_cltv_expiry when that asks for more
        var minFinalCltv = request.HasMinCltvExpiry && request.MinCltvExpiry > invoice.MinFinalCltvExpiry
                               ? CheckedCltvDelta(request.MinCltvExpiry, "min_cltv_expiry")
                               : invoice.MinFinalCltvExpiry;
        await _invoiceService.CreateHoldInvoiceAsync(
            new Hash(NBitcoin.DataEncoders.Encoders.Hex.DecodeData(paymentHashHex)), amount,
                                                     invoice.Description ?? string.Empty,
                                                     (uint)Math.Max(1, (invoice.ExpiryDate -
                                                                        DateTimeOffset.FromUnixTimeSeconds(invoice.Timestamp))
                                                                       .TotalSeconds),
                                                     minFinalCltv, s_labelled, context.CancellationToken);
        return new Hold.InjectResponse();
    }

    public override async Task<Hold.ListResponse> List(Hold.ListRequest request, ServerCallContext context)
    {
        var invoices = new List<InvoiceModel>();
        if (request.ConstraintCase == Hold.ListRequest.ConstraintOneofCase.PaymentHashes)
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>();
            foreach (var hash in request.PaymentHashes.Hashes.Select(h => new Hash(h.ToByteArray())))
            {
                var one = await repository.GetByPaymentHashAsync(hash);
                if (one is not null)
                    invoices.Add(one);
            }
        }
        else if (request.ConstraintCase == Hold.ListRequest.ConstraintOneofCase.PaymentHash)
        {
            using var scope = _scopeFactory.CreateScope();
            var one = await scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>()
                                  .GetByPaymentHashAsync(new Hash(request.PaymentHash.ToByteArray()));
            if (one is not null)
                invoices.Add(one);
        }
        else
        {
            var start = request.ConstraintCase == Hold.ListRequest.ConstraintOneofCase.Pagination
                            ? (int)request.Pagination.IndexStart
                            : 0;
            var take = request.ConstraintCase == Hold.ListRequest.ConstraintOneofCase.Pagination
                           ? (int)Math.Min(request.Pagination.Limit, ListPage)
                           : ListPage;
            using var scope = _scopeFactory.CreateScope();
            invoices.AddRange((await scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>()
                                           .ListAsync(start, take))
                              .Where(i => i.Label == Label));
        }

        var response = new Hold.ListResponse();
        foreach (var invoice in invoices)
            response.Invoices.Add(ToHoldInvoice(invoice));
        return response;
    }

    public override async Task<Hold.SettleResponse> Settle(Hold.SettleRequest request, ServerCallContext context)
    {
        var preimage = new Secret(request.PaymentPreimage.ToByteArray());
        var paymentHash = new Hash(SHA256.HashData((byte[])preimage));
        try
        {
            await _holdInvoiceService.SettleHoldInvoiceAsync(paymentHash, preimage, context.CancellationToken);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.NotFound, e.Message));
        }
        catch (InvalidOperationException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }

        return new Hold.SettleResponse();
    }

    public override async Task<Hold.CancelResponse> Cancel(Hold.CancelRequest request, ServerCallContext context)
    {
        try
        {
            await _holdInvoiceService.CancelHoldInvoiceAsync(new Hash(request.PaymentHash.ToByteArray()),
                                                             context.CancellationToken);
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.NotFound, e.Message));
        }
        catch (InvalidOperationException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }

        return new Hold.CancelResponse();
    }

    public override Task<Hold.CleanResponse> Clean(Hold.CleanRequest request, ServerCallContext context) =>
        // The node prunes its own invoice lifecycle; the hold plugin's Clean is janitorial for its own DB
        Task.FromResult(new Hold.CleanResponse { Cleaned = 0 });

    public override Task Track(Hold.TrackRequest request, IServerStreamWriter<Hold.TrackResponse> responseStream,
                               ServerCallContext context) =>
        throw new RpcException(new Status(StatusCode.Unimplemented, "use TrackAll"));

    public override async Task TrackAll(Hold.TrackAllRequest request,
                                         IServerStreamWriter<Hold.TrackAllResponse> responseStream,
                                         ServerCallContext context)
    {
        var watched = request.PaymentHashes.Select(h => new Hash(h.ToByteArray())).ToHashSet();
        using var subscription = _events?.Subscribe(256);

        // The snapshot first, so a reconnecting client learns where every watched invoice already stands
        foreach (var snapshot in await SnapshotAsync(watched))
            await responseStream.WriteAsync(snapshot);

        var changes = System.Threading.Channels.Channel.CreateBounded<(Hash Hash, InvoiceStatus Status)>(
            new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
        if (subscription is not null)
        {
            var pump = Task.Run(async () =>
            {
                try
                {
                    await foreach (var item in subscription.ReadAllAsync(context.CancellationToken))
                        if (item is InvoiceHeldEvent or InvoiceSettledEvent)
                            changes.Writer.TryWrite((item.PaymentHash, item is InvoiceSettledEvent
                                                                        ? InvoiceStatus.Settled
                                                                        : InvoiceStatus.Held));
                }
                catch (OperationCanceledException)
                {
                    // the stream ended
                }
            });
            _ = pump.ContinueWith(_ => changes.Writer.TryComplete(), TaskScheduler.Default);
        }

        // Cancels raise no event (the operator path writes the row directly): re-read on a slow tick
        while (!context.CancellationToken.IsCancellationRequested)
        {
            var changeTask = changes.Reader.ReadAsync(context.CancellationToken).AsTask();
            var delayTask = Task.Delay(s_cancelRecheck, context.CancellationToken);
            var done = await Task.WhenAny(changeTask, delayTask);
            if (done == changeTask && changeTask.IsCompletedSuccessfully)
            {
                var (hash, status) = await changeTask;
                if (watched.Count == 0 || watched.Contains(hash))
                    await responseStream.WriteAsync(new Hold.TrackAllResponse
                    {
                        PaymentHash = Google.Protobuf.ByteString.CopyFrom((byte[])hash),
                        State = ToHoldState(status)
                    });
            }
            else if (done == delayTask)
            {
                foreach (var change in await SnapshotAsync(watched))
                    await responseStream.WriteAsync(change);
            }

            // a change that lost the race stays read; drain what the tick would duplicate
            while (changes.Reader.TryRead(out var missed))
            {
                if (watched.Count == 0 || watched.Contains(missed.Hash))
                    await responseStream.WriteAsync(new Hold.TrackAllResponse
                    {
                        PaymentHash = Google.Protobuf.ByteString.CopyFrom((byte[])missed.Hash),
                        State = ToHoldState(missed.Status)
                    });
            }
        }
    }

    private async Task<List<Hold.TrackAllResponse>> SnapshotAsync(HashSet<Hash> watched)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IInvoiceDbRepository>();
        var result = new List<Hold.TrackAllResponse>();
        for (var skip = 0; ; skip += ListPage)
        {
            var page = await repository.ListAsync(skip, ListPage);
            foreach (var invoice in page.Where(i => i.Label == Label)
                                        .Where(i => watched.Count == 0 || watched.Contains(i.PaymentHash)))
                result.Add(new Hold.TrackAllResponse
                {
                    PaymentHash = Google.Protobuf.ByteString.CopyFrom((byte[])invoice.PaymentHash),
                    Bolt11 = invoice.Bolt11 ?? string.Empty,
                    State = ToHoldState(invoice.Status)
                });

            if (page.Count < ListPage)
                return result;
        }
    }

    /// <summary>A CLTV delta of the request, which BOLT 11's <c>c</c> carries as at most 16 bits here.</summary>
    private static ushort CheckedCltvDelta(ulong value, string field) =>
        value <= ushort.MaxValue
            ? (ushort)value
            : throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                $"{field} {value} is not a CLTV delta of at most {ushort.MaxValue}"));

    private static Hold.InvoiceState ToHoldState(InvoiceStatus status) =>
        status switch
        {
            InvoiceStatus.Held => Hold.InvoiceState.Accepted,
            InvoiceStatus.Settled => Hold.InvoiceState.Paid,
            InvoiceStatus.Canceled => Hold.InvoiceState.Cancelled,
            InvoiceStatus.Accepted => Hold.InvoiceState.Paid,
            _ => Hold.InvoiceState.Unpaid
        };

    private Hold.Invoice ToHoldInvoice(InvoiceModel model)
    {
        var invoice = new Hold.Invoice
        {
            Id = 0,
            PaymentHash = Google.Protobuf.ByteString.CopyFrom((byte[])model.PaymentHash),
            Preimage = model.Preimage is { } preimage
                           ? Google.Protobuf.ByteString.CopyFrom((byte[])preimage)
                           : Google.Protobuf.ByteString.Empty,
            Invoice_ = model.Bolt11 ?? string.Empty,
            State = ToHoldState(model.Status),
            CreatedAt = (ulong)((DateTimeOffset)model.CreatedAt).ToUnixTimeSeconds(),
            SettledAt = model.SettledAt is { } settledAt
                               ? (ulong)((DateTimeOffset)settledAt).ToUnixTimeSeconds()
                               : 0,
            MinCltvExpiry = model.MinFinalCltvExpiry
        };
        foreach (var (channelId, htlcId, msat, cltv) in FindIncomingHtlcs(model.PaymentHash))
            invoice.Htlcs.Add(new Hold.Htlc
            {
                Id = (long)htlcId,
                State = ToHoldState(model.Status) is Hold.InvoiceState.Paid ? Hold.InvoiceState.Paid
                            : ToHoldState(model.Status),
                ChannelId = 0,
                Scid = string.Empty,
                Msat = msat,
                CltvExpiry = cltv
            });
        return invoice;
    }

    /// <summary>Best effort: the incoming HTLCs of the payment across our channels (their List surfaces them).</summary>
    private IEnumerable<(ChannelId ChannelId, ulong HtlcId, ulong Msat, ulong Cltv)> FindIncomingHtlcs(
        Hash paymentHash)
    {
        foreach (var channel in _channelMemory.FindChannels(_ => true))
        {
            var commitments = channel.Commitments;
            if (commitments is null)
                continue;

            foreach (var (key, record) in commitments.Htlcs)
                if (record.PaymentHash == paymentHash && key.Direction == HtlcDirection.Incoming)
                    yield return (channel.ChannelId, key.Id, record.AmountMsat, record.CltvExpiry);
        }
    }
}