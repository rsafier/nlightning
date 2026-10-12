using System.Collections.Concurrent;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Lnrpc;
using NLightning.Testing.Lnd.Routerrpc;

namespace NLightning.Integration.Tests.Docker;

using Abcd;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Payments.Interfaces;
using Utils;

public partial class LndGrpcWave3FlowTests
{
    /// <summary>Uses real HTTP response headers (after source attachment) and the HTLC subscribed event as
    /// readiness barriers. No fixed sleep or synthetic source publication is used in this proof.</summary>
    private static async Task<SubscriptionProof> ObserveSubscriptionsAsync(LndNodeConnection client, CancellationToken ct)
    {
        var proof = new SubscriptionProof(client, ct);
        try
        {
            await proof.ReadyAsync(ct);
            return proof;
        }
        catch
        {
            await proof.DisposeAsync();
            throw;
        }
    }

    private sealed class SubscriptionProof : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly AsyncServerStreamingCall<PeerEvent> _peerStream;
        private readonly AsyncServerStreamingCall<ChannelEventUpdate> _channelStream;
        private readonly AsyncServerStreamingCall<GraphTopologyUpdate> _graphStream;
        private readonly AsyncServerStreamingCall<Transaction> _transactionStream;
        private readonly AsyncServerStreamingCall<HtlcEvent> _htlcStream;
        private readonly ConcurrentQueue<PeerEvent> _peers = new();
        private readonly ConcurrentQueue<ChannelEventUpdate> _channels = new();
        private readonly ConcurrentQueue<GraphTopologyUpdate> _graph = new();
        private readonly ConcurrentQueue<Transaction> _transactions = new();
        private readonly ConcurrentQueue<HtlcEvent> _htlcs = new();
        private readonly Task[] _readers;

        public SubscriptionProof(LndNodeConnection client, CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _peerStream = client.LightningClient.SubscribePeerEvents(new PeerEventSubscription(), cancellationToken: _stop.Token);
            _channelStream = client.LightningClient.SubscribeChannelEvents(new ChannelEventSubscription(), cancellationToken: _stop.Token);
            _graphStream = client.LightningClient.SubscribeChannelGraph(new GraphTopologySubscription(), cancellationToken: _stop.Token);
            _transactionStream = client.LightningClient.SubscribeTransactions(new GetTransactionsRequest(), cancellationToken: _stop.Token);
            _htlcStream = client.RouterClient.SubscribeHtlcEvents(new SubscribeHtlcEventsRequest(), cancellationToken: _stop.Token);
            _readers = [ReadAsync(_peerStream, _peers), ReadAsync(_channelStream, _channels), ReadAsync(_graphStream, _graph),
                        ReadAsync(_transactionStream, _transactions), ReadAsync(_htlcStream, _htlcs)];
        }

        public async Task ReadyAsync(CancellationToken ct)
        {
            await Task.WhenAll(_peerStream.ResponseHeadersAsync, _channelStream.ResponseHeadersAsync,
                               _graphStream.ResponseHeadersAsync, _transactionStream.ResponseHeadersAsync)
                      .WaitAsync(s_timeout, ct);
            await UntilAsync(() => _htlcs.Any(x => x.SubscribedEvent is not null), "HTLC subscribed handshake", ct);
        }

        public async Task AssertObservedAsync(LndGrpcWave3FlowTests test, LndNodeConnection ours,
                                               LndNodeConnection alice, ChannelId payeeChannel, CancellationToken ct)
        {
            // Existing Wave3 operations produce true forwarding, interceptor local refusal and settlement,
            // pending/open channels, peer connection changes, deposits and a confirmed wallet spend.
            await UntilAsync(() => _peers.Any(x => x.PubKey == alice.LocalNodePubKey
                                                && x.Type == PeerEvent.Types.EventType.PeerOnline), "real peer online", ct);
            await UntilAsync(() => _peers.Any(x => x.PubKey == alice.LocalNodePubKey
                                                && x.Type == PeerEvent.Types.EventType.PeerOffline), "real peer offline after rejected open", ct);
            await UntilAsync(() => _channels.Any(x => x.Type == ChannelEventUpdate.Types.UpdateType.PendingOpenChannel)
                                && _channels.Any(x => x.Type == ChannelEventUpdate.Types.UpdateType.OpenChannel)
                                && _channels.Any(x => x.Type == ChannelEventUpdate.Types.UpdateType.ActiveChannel),
                             "pending, open and active channel events", ct);
            await UntilAsync(() => _htlcs.Any(x => x.EventType == HtlcEvent.Types.EventType.Forward
                                                && x.ForwardEvent is not null)
                                && _htlcs.Any(x => x.EventType == HtlcEvent.Types.EventType.Forward
                                                && x.LinkFailEvent is not null)
                                && _htlcs.Any(x => x.EventType == HtlcEvent.Types.EventType.Forward
                                                && x.SettleEvent is not null),
                             "forward, interceptor refusal and settlement events", ct);
            await UntilAsync(() => _transactions.Any(x => x.Amount > 0 && x.NumConfirmations > 0)
                                && _transactions.Any(x => x.Amount < 0 && x.NumConfirmations > 0),
                             "confirmed real wallet deposit and spend", ct);

            // Direct send: the same real SendPaymentV2 RPC that wallet clients use.
            var sentInvoice = await test.Payee.CreateInvoiceAsync(LightningMoney.Satoshis(2_000), "subscription: send", ct);
            var sent = await LndTestHelpers.SendPaymentV2Async(ours, new SendPaymentRequest
            {
                PaymentRequest = sentInvoice.Bolt11,
                TimeoutSeconds = 30,
                FeeLimitSat = 100
            }, ct);
            Assert.Equal(Payment.Types.PaymentStatus.Succeeded, sent.Status);
            await UntilAsync(() => _htlcs.Any(x => x.EventType == HtlcEvent.Types.EventType.Send
                                                && x.ForwardEvent is not null)
                                && _htlcs.Any(x => x.EventType == HtlcEvent.Types.EventType.Send
                                                && x.SettleEvent is not null), "direct SEND forwarding and settle", ct);

            // Direct receive: alice pays an invoice made by our LND RPC.
            var receivedInvoice = await ours.LightningClient.AddInvoiceAsync(new Invoice
            {
                Value = 2_000,
                Memo = "subscription: receive"
            }, cancellationToken: ct);
            await LndTestHelpers.ResetMissionControlAsync(alice, ct);
            var received = await LndTestHelpers.SendPaymentV2Async(alice, new SendPaymentRequest
            {
                PaymentRequest = receivedInvoice.PaymentRequest,
                TimeoutSeconds = 30,
                FeeLimitSat = 100
            }, ct);
            Assert.Equal(Payment.Types.PaymentStatus.Succeeded, received.Status);
            await UntilAsync(() => _htlcs.Any(x => x.EventType == HtlcEvent.Types.EventType.Receive
                                                && x.SettleEvent is not null), "direct RECEIVE settle", ct);

            // A genuine refusal at the downstream payee must arrive as ForwardFailEvent at our forwarding node.
            var canceled = await test.Payee.CreateInvoiceAsync(LightningMoney.Satoshis(2_000), "subscription: downstream failure", ct);
            await test.Payee.Services.GetRequiredService<IHoldInvoiceService>().CancelHoldInvoiceAsync(canceled.PaymentHash, ct);
            var failuresBefore = _htlcs.Count(x => x.ForwardFailEvent is not null);
            await LndTestHelpers.ResetMissionControlAsync(alice, ct);
            var downstream = await LndTestHelpers.SendPaymentV2Async(alice, new SendPaymentRequest
            {
                PaymentRequest = canceled.Bolt11,
                TimeoutSeconds = 30,
                FeeLimitSat = 100
            }, ct);
            Assert.Equal(Payment.Types.PaymentStatus.Failed, downstream.Status);
            await UntilAsync(() => _htlcs.Count(x => x.ForwardFailEvent is not null) > failuresBefore,
                             "downstream encrypted failure observed", ct);

            // SETTLE is a real external interceptor decision, with the invoice's matching preimage.
            var settledInvoice = await test.Payee.CreateInvoiceAsync(LightningMoney.Satoshis(2_000), "subscription: intercepted settle", ct);
            var settledModel = await test.Payee.Services.GetRequiredService<IInvoiceService>()
                                         .GetInvoiceAsync(settledInvoice.PaymentHash, ct);
            Assert.NotNull(settledModel);
            var settledBefore = _htlcs.Count(x => x.SettleEvent is not null);
            using (var interceptor = ours.RouterClient.HtlcInterceptor(cancellationToken: ct))
            {
                var hub = test.Node.Services.GetRequiredService<Application.Payments.Interception.HtlcInterceptorHub>();
                await UntilAsync(() => hub.IsActive, "settling interceptor active", ct);
                var paying = LndTestHelpers.SendPaymentV2Async(alice, new SendPaymentRequest
                {
                    PaymentRequest = settledInvoice.Bolt11,
                    TimeoutSeconds = 30,
                    FeeLimitSat = 100
                }, ct);
                Assert.True(await interceptor.ResponseStream.MoveNext(ct).WaitAsync(s_timeout, ct));
                await interceptor.RequestStream.WriteAsync(new ForwardHtlcInterceptResponse
                {
                    IncomingCircuitKey = interceptor.ResponseStream.Current.IncomingCircuitKey,
                    Action = ResolveHoldForwardAction.Settle,
                    Preimage = ByteString.CopyFrom((byte[])settledModel.Preimage!.Value)
                }, ct);
                Assert.Equal(Payment.Types.PaymentStatus.Succeeded, (await paying).Status);
            }
            await UntilAsync(() => _htlcs.Count(x => x.SettleEvent is not null) > settledBefore,
                             "external interceptor SETTLE observed", ct);

            // Public channel gossip goes through real funding verification and signed announcement ingress.
            await ChainSync.MineAndWaitAsync(test._fixture, 6, [alice], test._nodes, ct);
            await UntilAsync(() => _graph.SelectMany(x => x.ChannelUpdates).Any(x =>
                x.AdvertisingNode == test.Node.NodeIdHex && x.ConnectingNode == test.Payee.NodeIdHex),
                "our public graph channel updates", ct);
            var graphChannel = _graph.SelectMany(x => x.ChannelUpdates).First(x =>
                x.AdvertisingNode == test.Node.NodeIdHex && x.ConnectingNode == test.Payee.NodeIdHex);
            Assert.True(graphChannel.ChanId > 0);
            Assert.Equal(1_000_000, graphChannel.Capacity);

            // Closing the public channel gives both lifecycle and real spent-edge graph events.
            var channel = await test.Node.GetChannelAsync(payeeChannel, ct);
            using var close = ours.LightningClient.CloseChannel(new CloseChannelRequest
            {
                ChannelPoint = new ChannelPoint
                {
                    FundingTxidBytes = ByteString.CopyFrom((byte[])channel.FundingTxId!.Value),
                    OutputIndex = channel.FundingOutputIndex!.Value
                },
                SatPerVbyte = 5
            }, cancellationToken: ct);
            Assert.True(await close.ResponseStream.MoveNext(ct).WaitAsync(s_timeout, ct));
            await ChainSync.MineAndWaitAsync(test._fixture, 6, [alice], test._nodes, ct);
            await UntilAsync(() => _channels.Any(x => x.Type == ChannelEventUpdate.Types.UpdateType.ClosedChannel)
                                && _channels.Any(x => x.Type == ChannelEventUpdate.Types.UpdateType.InactiveChannel),
                             "closed/inactive lifecycle events", ct);
            await UntilAsync(() => _graph.SelectMany(x => x.ClosedChans).Any(x => x.ChanId == graphChannel.ChanId),
                             "our spent graph channel event", ct);
            Console.WriteLine($"Subscription proof: peer={_peers.Count}, channel={_channels.Count}, graph={_graph.Count}, "
                            + $"wallet={_transactions.Count}, HTLC={_htlcs.Count}; all five real streams observed.");
        }

        private static Task UntilAsync(Func<bool> predicate, string description, CancellationToken ct) =>
            Poll.UntilAsync(() => Task.FromResult(predicate()), s_timeout, description, ct);

        private async Task ReadAsync<T>(AsyncServerStreamingCall<T> stream, ConcurrentQueue<T> events)
        {
            try
            {
                while (await stream.ResponseStream.MoveNext(_stop.Token))
                    events.Enqueue(stream.ResponseStream.Current);
            }
            catch (Exception e) when (_stop.IsCancellationRequested && e is OperationCanceledException or RpcException)
            {
                // Intentional subscription cancellation only; unexpected RPC failures fault the reader.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _peerStream.Dispose();
            _channelStream.Dispose();
            _graphStream.Dispose();
            _transactionStream.Dispose();
            _htlcStream.Dispose();
            await Task.WhenAll(_readers);
            _stop.Dispose();
        }
    }
}