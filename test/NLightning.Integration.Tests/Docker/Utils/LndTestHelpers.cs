using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Invoicesrpc;
using Lnrpc;
using LNUnit.LND;
using Routerrpc;

namespace NLightning.Integration.Tests.Docker.Utils;

/// <summary>
/// LND calls the multi-node Docker tests share: invoices with explicit route hints (how Alice reaches our private
/// channels without gossip), hold invoices, <c>SendPaymentV2</c> pinned to one path, mission-control resets and
/// lookups that poll with a deadline.
/// </summary>
public static class LndTestHelpers
{
    public static readonly TimeSpan DefaultPaymentTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// A random 32-byte preimage and its SHA-256 payment hash.
    /// </summary>
    public static (byte[] Preimage, byte[] PaymentHash) NewPreimage()
    {
        var preimage = RandomNumberGenerator.GetBytes(32);
        return (preimage, SHA256.HashData(preimage));
    }

    /// <summary>
    /// One route-hint hop: the channel <paramref name="shortChannelId"/> from <paramref name="nodeIdHex"/> towards
    /// the payee, with that node's forwarding policy.
    /// </summary>
    public static HopHint HopHint(string nodeIdHex, ulong shortChannelId, uint feeBaseMsat,
                                  uint feeProportionalMillionths, uint cltvExpiryDelta) => new()
                                  {
                                      NodeId = nodeIdHex.ToLowerInvariant(),
                                      ChanId = shortChannelId,
                                      FeeBaseMsat = feeBaseMsat,
                                      FeeProportionalMillionths = feeProportionalMillionths,
                                      CltvExpiryDelta = cltvExpiryDelta
                                  };

    /// <summary>
    /// A route hint made of <paramref name="hops"/>, first hop first (the payee is after the last one).
    /// </summary>
    public static RouteHint RouteHint(params HopHint[] hops)
    {
        var hint = new RouteHint();
        hint.HopHints.AddRange(hops);
        return hint;
    }

    /// <summary>
    /// Adds an invoice carrying exactly <paramref name="routeHints"/> (LND adds no hints of its own unless
    /// <paramref name="addPrivateHints"/>).
    /// </summary>
    public static async Task<AddInvoiceResponse> AddInvoiceAsync(LNDNodeConnection node, long valueMsat,
                                                                 IEnumerable<RouteHint> routeHints,
                                                                 CancellationToken cancellationToken,
                                                                 string memo = "", bool addPrivateHints = false,
                                                                 ulong? cltvExpiry = null)
    {
        var invoice = new Invoice { ValueMsat = valueMsat, Memo = memo, Private = addPrivateHints };
        invoice.RouteHints.AddRange(routeHints);
        if (cltvExpiry is { } cltv)
            invoice.CltvExpiry = cltv;

        return await node.LightningClient.AddInvoiceAsync(invoice, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Adds a hold invoice for <paramref name="paymentHash"/>: LND accepts the HTLC and holds it until
    /// <see cref="SettleInvoiceAsync"/> or <see cref="CancelInvoiceAsync"/>.
    /// </summary>
    public static async Task<AddHoldInvoiceResp> AddHoldInvoiceAsync(LNDNodeConnection node, byte[] paymentHash,
                                                                      long valueMsat,
                                                                      IEnumerable<RouteHint> routeHints,
                                                                      CancellationToken cancellationToken,
                                                                      string memo = "", ulong? cltvExpiry = null)
    {
        var request = new AddHoldInvoiceRequest
        {
            Hash = ByteString.CopyFrom(paymentHash),
            ValueMsat = valueMsat,
            Memo = memo
        };
        request.RouteHints.AddRange(routeHints);
        if (cltvExpiry is { } cltv)
            request.CltvExpiry = cltv;

        return await node.InvoiceClient.AddHoldInvoiceAsync(request, cancellationToken: cancellationToken);
    }

    public static async Task SettleInvoiceAsync(LNDNodeConnection node, byte[] preimage,
                                                CancellationToken cancellationToken)
    {
        await node.InvoiceClient.SettleInvoiceAsync(new SettleInvoiceMsg { Preimage = ByteString.CopyFrom(preimage) },
                                                    cancellationToken: cancellationToken);
    }

    public static async Task CancelInvoiceAsync(LNDNodeConnection node, byte[] paymentHash,
                                                CancellationToken cancellationToken)
    {
        await node.InvoiceClient.CancelInvoiceAsync(
            new CancelInvoiceMsg { PaymentHash = ByteString.CopyFrom(paymentHash) },
            cancellationToken: cancellationToken);
    }

    public static async Task<Invoice> LookupInvoiceAsync(LNDNodeConnection node, byte[] paymentHash,
                                                         CancellationToken cancellationToken)
    {
        return await node.LightningClient.LookupInvoiceAsync(new PaymentHash { RHash = ByteString.CopyFrom(paymentHash) },
                                                             cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Polls the invoice until it reaches <paramref name="state"/>.
    /// </summary>
    /// <exception cref="TimeoutException">Not reached in time; the message names the last state seen.</exception>
    public static async Task<Invoice> WaitForInvoiceStateAsync(LNDNodeConnection node, byte[] paymentHash,
                                                               Invoice.Types.InvoiceState state, TimeSpan timeout,
                                                               CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var invoice = await LookupInvoiceAsync(node, paymentHash, cancellationToken);
            if (invoice.State == state)
                return invoice;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Invoice {Convert.ToHexString(paymentHash)} on {node.LocalAlias} is {invoice.State}, not {state}");

            await Task.Delay(s_pollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// A <c>SendPaymentV2</c> request for <paramref name="paymentRequest"/> that LND cannot split or reroute: one
    /// part, only through <paramref name="outgoingChannelIds"/> (empty for any), fee limit
    /// <paramref name="feeLimitMsat"/>.
    /// </summary>
    public static SendPaymentRequest PinnedPayment(string paymentRequest, IEnumerable<ulong> outgoingChannelIds,
                                                   long feeLimitMsat = 1_000_000, int timeoutSeconds = 60)
    {
        var request = new SendPaymentRequest
        {
            PaymentRequest = paymentRequest,
            MaxParts = 1,
            FeeLimitMsat = feeLimitMsat,
            TimeoutSeconds = timeoutSeconds,
            NoInflightUpdates = true
        };
        request.OutgoingChanIds.AddRange(outgoingChannelIds);
        return request;
    }

    /// <summary>
    /// Sends a payment with <c>SendPaymentV2</c> and returns the final update (<c>SUCCEEDED</c> or <c>FAILED</c>).
    /// Not awaiting the task leaves the payment in flight (e.g. towards a hold invoice).
    /// </summary>
    /// <exception cref="TimeoutException">No final state within <paramref name="timeout"/>.</exception>
    public static async Task<Payment> SendPaymentV2Async(LNDNodeConnection node, SendPaymentRequest request,
                                                         CancellationToken cancellationToken,
                                                         TimeSpan? timeout = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? DefaultPaymentTimeout + TimeSpan.FromSeconds(10));

        using var call = node.RouterClient.SendPaymentV2(request, cancellationToken: timeoutCts.Token);
        Payment? last = null;
        try
        {
            await foreach (var update in call.ResponseStream.ReadAllAsync(timeoutCts.Token))
            {
                last = update;
                if (update.Status is Payment.Types.PaymentStatus.Succeeded or Payment.Types.PaymentStatus.Failed)
                    return update;
            }
        }
        catch (RpcException e) when (e.StatusCode == StatusCode.Cancelled && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Payment from {node.LocalAlias} not final in time (last {last?.Status})", e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Payment from {node.LocalAlias} not final in time (last {last?.Status})", e);
        }

        throw new InvalidOperationException(
            $"SendPaymentV2 stream from {node.LocalAlias} ended without a final state (last {last?.Status})");
    }

    /// <summary>
    /// Forgets what LND learned from earlier payment failures, so a new payment is not steered by them.
    /// </summary>
    public static async Task ResetMissionControlAsync(LNDNodeConnection node, CancellationToken cancellationToken)
    {
        await node.RouterClient.ResetMissionControlAsync(new ResetMissionControlRequest(),
                                                         cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The channel with LND's <c>txid:index</c> channel point (select channels by point, never by index).
    /// </summary>
    public static async Task<Channel?> GetChannelByPointAsync(LNDNodeConnection node, string channelPoint,
                                                              CancellationToken cancellationToken)
    {
        var channels = await node.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                    cancellationToken: cancellationToken);
        return channels.Channels.FirstOrDefault(c => c.ChannelPoint.Equals(channelPoint,
                                                                           StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="node"/> lists <paramref name="peerIdHex"/> as a connected peer.
    /// </summary>
    public static async Task<bool> IsConnectedToAsync(LNDNodeConnection node, string peerIdHex,
                                                      CancellationToken cancellationToken)
    {
        var peers = await node.LightningClient.ListPeersAsync(new ListPeersRequest(),
                                                              cancellationToken: cancellationToken);
        return peers.Peers.Any(p => p.PubKey.Equals(peerIdHex, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Makes <paramref name="node"/> drop its connection to <paramref name="peerIdHex"/> (LND does not refuse this for
    /// a channel peer). A channel peer of ours reconnects by itself.
    /// </summary>
    public static async Task DisconnectPeerAsync(LNDNodeConnection node, string peerIdHex,
                                                 CancellationToken cancellationToken)
    {
        await node.LightningClient.DisconnectPeerAsync(
            new DisconnectPeerRequest { PubKey = peerIdHex.ToLowerInvariant() }, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The LND version string (<c>0.20.0-beta commit=...</c>), for the test log.
    /// </summary>
    public static async Task<string> GetVersionAsync(LNDNodeConnection node, CancellationToken cancellationToken)
    {
        var info = await node.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken);
        return info.Version;
    }
}