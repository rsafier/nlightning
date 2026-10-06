using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.LnBackend;

using Domain.Accounting.Labels;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The <c>cln.Node</c> subset of the vendored CLN gRPC proxy proto that an unmodified ASP (Second's captaind, the
/// Bark server) drives this node's pay side and lifecycle through: <c>Getinfo</c> (node liveness, network and height),
/// <c>Xpay</c> (pay a BOLT 11 invoice and return its preimage) and <c>Listpays</c> (reconcile a payment attempt's
/// status). The gRPC field numbers are the wire contract with the full proto's Rust client.
/// </summary>
/// <remarks>
/// <para><c>Xpay</c> maps onto <see cref="IPaymentService.PayInvoiceAsync(string, LightningMoney?,
/// PayInvoiceOptions, CancellationToken)"/>: <c>amount_msat</c> is passed only when the invoice is amountless (CLN
/// does not tip), <c>maxfee</c> becomes <see cref="PayInvoiceOptions.MaxFee"/> and <c>retry_for</c> (seconds, clamped
/// to 1..300, 60 when unset) the <see cref="PayInvoiceOptions.Timeout"/>. The outcome maps by the returned
/// <see cref="PaymentModel.Status"/>: <see cref="PaymentStatus.Succeeded"/> answers with the
/// <c>payment_preimage</c>; <see cref="PaymentStatus.InFlight"/> (the wait ended while an HTLC was still pending)
/// answers <c>DEADLINE_EXCEEDED</c> — the caller keeps waiting on <c>Listpays</c>, where the row reports
/// <c>PENDING</c> until it resolves; <see cref="PaymentStatus.Failed"/> answers <c>FAILED_PRECONDITION</c> with the
/// row's <see cref="PaymentModel.FailureReason"/>. A bad invoice is an <see cref="StatusCode.InvalidArgument"/>, a
/// payment already in flight or succeeded a <see cref="StatusCode.FailedPrecondition"/>. The payment does not take the
/// call's cancellation (CLN's xpay keeps running in lightningd when its gRPC caller leaves): a caller that goes away
/// ends only its own wait (<c>CANCELLED</c>), the payment retries for its window and its outcome reaches
/// <c>Listpays</c>.</para>
/// <para>Why <c>DEADLINE_EXCEEDED</c> and not a failure for a payment still in flight (NL-1148 wave C): captaind
/// (<c>server/src/ln/cln/xpay.rs</c>) ignores how its xpay call ended — success or any error — and reconciles the
/// attempt by <c>Listpays</c> on the payment hash right after the call and then periodically (first after
/// <c>retry_for</c> + 15 s, backing off): no row fails the attempt (the user's HTLC VTXOs become revocable),
/// <c>PENDING</c> keeps it open, <c>COMPLETE</c> with the preimage succeeds it and <c>FAILED</c> fails it. So a payment
/// with an HTLC out must always be listed, and <c>FAILED</c> only once no part is in flight — which is
/// <see cref="PaymentStatus.Failed"/>'s own rule.</para>
/// <para><c>maxdelay</c> (the payer's CLTV cap) and <c>partial_msat</c> are accepted but not enforced: the route
/// planner already clamps the final CLTV expiry, and no partial (MPP-target) payment is driven through this seam.
/// <c>Listpays</c> filters by <c>payment_hash</c> (a lookup captaind makes with every reconciliation) or serves the
/// stored rows paged newest first, with <see cref="PaymentStatus.InFlight"/> reported <c>PENDING</c>,
/// <see cref="PaymentStatus.Failed"/> <c>FAILED</c> and <see cref="PaymentStatus.Succeeded"/> <c>COMPLETE</c> with
/// the preimage. <c>created_index</c>/<c>updated_index</c> (their client unwraps them to pick the latest attempt of
/// a hash) carry the row's Unix milliseconds.</para>
/// </remarks>
public sealed class ClnNodeBackendService : Cln.Node.NodeBase
{
    private const int ListPage = 64;

    /// <summary>Every payment this backend pays carries the label (as the hold side's invoices do).</summary>
    private const string Label = "ln-backend";

    private static readonly SourceLabels s_labelled = SourceLabels.Create(Label, []);

    private readonly ILogger<ClnNodeBackendService> _logger;
    private readonly ISecureKeyManager _keyManager;
    private readonly NodeOptions _nodeOptions;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IPaymentService _paymentService;
    private readonly IServiceScopeFactory _scopeFactory;

    public ClnNodeBackendService(ILogger<ClnNodeBackendService> logger, ISecureKeyManager keyManager,
                                 IOptions<NodeOptions> nodeOptions, IBlockchainMonitor blockchainMonitor,
                                 IPaymentService paymentService, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _keyManager = keyManager;
        _nodeOptions = nodeOptions.Value;
        _blockchainMonitor = blockchainMonitor;
        _paymentService = paymentService;
        _scopeFactory = scopeFactory;
    }

    public override Task<Cln.GetinfoResponse> Getinfo(Cln.GetinfoRequest request, ServerCallContext context)
    {
        return Task.FromResult(new Cln.GetinfoResponse
        {
            Id = Google.Protobuf.ByteString.CopyFrom((byte[])_keyManager.GetNodePubKey()),
            Alias = string.Empty,
            Version = typeof(ClnNodeBackendService).Assembly.GetName().Version?.ToString(3) ?? "0.0.1",
            Blockheight = _blockchainMonitor.LastProcessedBlockHeight,
            Network = NetworkName(_nodeOptions.BitcoinNetwork)
        });
    }

    /// <summary>CLN's own <c>network</c> string, which their client parses with <c>bitcoin::Network::from_str</c>:
    /// mainnet is <c>bitcoin</c> there, every other name is our own (already lower-case) network name.</summary>
    private static string NetworkName(BitcoinNetwork network) =>
        network.Name == NetworkConstants.Mainnet ? "bitcoin" : network.Name;

    public override async Task<Cln.XpayResponse> Xpay(Cln.XpayRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Invstring))
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                                              "invstring must be the BOLT 11 invoice to pay"));

        var seconds = request.HasRetryFor && request.RetryFor > 0 ? Math.Clamp(request.RetryFor, 1, 300) : 60;
        var options = new PayInvoiceOptions
        {
            MaxFee = request.Maxfee is { } maxFee ? LightningMoney.MilliSatoshis(maxFee.Msat) : null,
            Timeout = TimeSpan.FromSeconds(seconds),
            Labels = s_labelled
        };

        // The payment never takes the caller's cancellation: as CLN's xpay command keeps running in lightningd when its
        // gRPC caller goes away, the payment retries for its whole window and its outcome reaches Listpays, which is
        // what the caller (captaind) reconciles by; only this call's wait ends
        var payment = _paymentService.PayInvoiceAsync(request.Invstring,
                                                      request.AmountMsat is { } amount
                                                          ? LightningMoney.MilliSatoshis(amount.Msat)
                                                          : null,
                                                      options, CancellationToken.None);
        PayInvoiceResult result;
        try
        {
            result = await payment.WaitAsync(context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            _ = payment.ContinueWith(t => _logger.LogWarning(t.Exception, "Backend xpay ended after its caller left"),
                                     CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                                     TaskScheduler.Default);
            throw new RpcException(new Status(StatusCode.Cancelled,
                                              "the caller left; the payment continues and its outcome arrives over "
                                            + "Listpays"));
        }
        catch (ArgumentException e)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message));
        }
        catch (InvalidOperationException e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Backend xpay {PaymentHash} for {AmountMsat} msat is {Status}",
                                   Convert.ToHexString((byte[])result.Payment.PaymentHash).ToLowerInvariant(),
                                   result.Payment.Amount.MilliSatoshi, result.Payment.Status);

        return result.Payment.Status switch
        {
            PaymentStatus.Succeeded => Succeeded(result),
            PaymentStatus.InFlight => throw new RpcException(new Status(StatusCode.DeadlineExceeded,
                $"the payment is still in flight after {seconds}s; its outcome arrives over Listpays")),
            _ => throw new RpcException(new Status(StatusCode.FailedPrecondition,
                result.Payment.FailureReason ?? "the payment failed"))
        };
    }

    private static Cln.XpayResponse Succeeded(PayInvoiceResult result)
    {
        var payment = result.Payment;
        return new Cln.XpayResponse
        {
            PaymentPreimage = Google.Protobuf.ByteString.CopyFrom((byte[])payment.Preimage!),
            SuccessfulParts = (ulong)result.Parts,
            FailedParts = (ulong)Math.Max(0, result.Attempts - result.Parts),
            AmountMsat = new Cln.Amount { Msat = payment.Amount.MilliSatoshi },
            AmountSentMsat = new Cln.Amount { Msat = (payment.Amount + payment.Fee).MilliSatoshi }
        };
    }

    public override async Task<Cln.ListpaysResponse> Listpays(Cln.ListpaysRequest request,
                                                              ServerCallContext context)
    {
        var payments = new List<PaymentModel>();
        if (request.PaymentHash is { Length: > 0 } hash)
        {
            if (hash.Length != 32)
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                                                  "payment_hash must be the 32-byte payment hash"));
            using var scope = _scopeFactory.CreateScope();
            var one = await scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>()
                                 .GetByPaymentHashAsync(new Hash(hash.ToByteArray()));
            if (one is not null)
                payments.Add(one);
        }
        else
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPaymentDbRepository>();
            for (var skip = 0; !request.HasLimit || payments.Count < request.Limit; skip += ListPage)
            {
                var page = await repository.ListAsync(skip, ListPage);
                payments.AddRange(page);
                if (page.Count < ListPage)
                    break;
            }

            if (request.HasLimit)
                payments = [.. payments.Take((int)request.Limit)];
        }

        var response = new Cln.ListpaysResponse();
        foreach (var payment in payments)
        {
            if (request.HasStatus && ToRequestStatus(payment.Status) != request.Status)
                continue;
            if (request.HasBolt11 && payment.Bolt11 != request.Bolt11)
                continue;
            response.Pays.Add(ToListpaysPays(payment));
        }

        return response;
    }

    /// <summary>Our payment status as the request's <c>status</c> filter has it (its numbering differs from the
    /// pays entries': <c>COMPLETE</c> is 1 there and 2 here).</summary>
    private static Cln.ListpaysRequest.Types.ListpaysStatus ToRequestStatus(PaymentStatus status) =>
        status switch
        {
            PaymentStatus.Succeeded => Cln.ListpaysRequest.Types.ListpaysStatus.Complete,
            PaymentStatus.Failed => Cln.ListpaysRequest.Types.ListpaysStatus.Failed,
            _ => Cln.ListpaysRequest.Types.ListpaysStatus.Pending
        };

    private static Cln.ListpaysPays.Types.ListpaysPaysStatus ToPaysStatus(PaymentStatus status) =>
        status switch
        {
            PaymentStatus.Succeeded => Cln.ListpaysPays.Types.ListpaysPaysStatus.Complete,
            PaymentStatus.Failed => Cln.ListpaysPays.Types.ListpaysPaysStatus.Failed,
            _ => Cln.ListpaysPays.Types.ListpaysPaysStatus.Pending
        };

    private static Cln.ListpaysPays ToListpaysPays(PaymentModel model)
    {
        var createdIndex = (ulong)((DateTimeOffset)model.CreatedAt).ToUnixTimeMilliseconds();
        var pays = new Cln.ListpaysPays
        {
            PaymentHash = Google.Protobuf.ByteString.CopyFrom((byte[])model.PaymentHash),
            Status = ToPaysStatus(model.Status),
            Destination = Google.Protobuf.ByteString.CopyFrom((byte[])model.PayeeNodeId),
            CreatedAt = (ulong)((DateTimeOffset)model.CreatedAt).ToUnixTimeSeconds(),
            AmountMsat = new Cln.Amount { Msat = model.Amount.MilliSatoshi },
            // Nothing was sent unless the payee settled: a failed or pending payment sent no funds
            AmountSentMsat = new Cln.Amount
            {
                Msat = model.Status == PaymentStatus.Succeeded
                           ? (model.Amount + model.Fee).MilliSatoshi
                           : 0
            },
            Bolt11 = model.Bolt11 ?? string.Empty,
            // Their client unwraps created_index to pick the latest attempt of a hash: always set
            CreatedIndex = createdIndex,
            UpdatedIndex = model.CompletedAt is { } completedAt
                               ? (ulong)((DateTimeOffset)completedAt).ToUnixTimeMilliseconds()
                               : createdIndex
        };
        if (model.Label is { } label)
            pays.Label = label;
        if (model.CompletedAt is { } at)
            pays.CompletedAt = (ulong)((DateTimeOffset)at).ToUnixTimeSeconds();
        if (model.Preimage is { } preimage)
            pays.Preimage = Google.Protobuf.ByteString.CopyFrom((byte[])preimage);
        return pays;
    }
}