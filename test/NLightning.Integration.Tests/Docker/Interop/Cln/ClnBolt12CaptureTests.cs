using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Daemon.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Offers.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.OnionMessages.Interfaces;
using Fixtures;
using Utils;

/// <summary>
/// NL-450 (BOLT 12 plan B0-T4): records Core Lightning <see cref="ClnFixture.ClnTag"/>'s BOLT 12 messages as
/// <c>VECTOR cln-&lt;tag&gt; &lt;kind&gt; &lt;hex&gt;</c> lines for <c>Tests.Utils/Vectors/Bolt12Vectors</c>: CLN's
/// offer, the invoice_request CLN's <c>fetchinvoice</c> sends to our offer, CLN's invoice for our invoice_request (and
/// our invoice_request, so the invoice can be checked against it), and CLN's invoice_error for a disabled offer.
/// </summary>
/// <remarks>
/// Explicit: it only records (<c>-- xUnit.Explicit=only</c> or the v3 runner's <c>-explicit only</c>, from the host).
/// The bytes are taken as the node's own services see them: a recording decorator of <see cref="IOnionMessageService"/>
/// (the replies to our requests and what we send) and of the registered <see cref="IOnionMessageHandler"/>s (what CLN
/// sends to our offer), both installed through <see cref="NLightningTestNode.ConfigureServices"/>. The recorded
/// vectors are checked by <c>BOLT12/Bolt12ClnVectorTests</c> (parse, byte-exact re-encode, validate, signatures).
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnBolt12CaptureTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 8 * 60 * 1_000;

    private readonly ClnFixture _fixture;
    private readonly Bolt12MessageRecorder _recorder = new();
    private ClnChannelSession? _session;

    public ClnBolt12CaptureTests(ClnFixture fixture, ITestOutputHelper output)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private string Tag => $"cln-{ClnFixture.ClnTag}";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
            await _session.DisposeAsync();
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMs)]
    public async Task Given_ClnAndOurNode_When_TheyExchangeBolt12Messages_Then_TheBytesAreRecorded()
    {
        // Arrange: a private channel we fund to CLN (so our offers are introduced by CLN), recorders installed
        var ct = TestContext.Current.CancellationToken;
        _session = await ClnChannelSession.BuildOurFundedAsync(_fixture, "nltg-b12-capture",
                                                               LightningMoney.Satoshis(1_000_000),
                                                               LightningMoney.Satoshis(500_000), ct,
                                                               node => node.ConfigureServices = _recorder.Install);
        Console.WriteLine($"VECTOR {Tag} node_id {_fixture.ClnNodeId}");
        Console.WriteLine($"VECTOR nltg node_id {_session.Node.NodeIdHex}");

        // Act 1: CLN's offer, fetched by us: our invoice_request and CLN's invoice
        var offer = await _fixture.Cln.CallAsync("offer", ct, ("amount", "10000sat"),
                                                 ("description", "nltg nl-450 capture"), ("issuer", "cln"));
        var offerString = offer["bolt12"]!.GetValue<string>();
        PrintVector("offer", Bolt12Bech32.Decode(offerString, Bolt12Constants.OfferHrp));
        var fetched = await HandleAsync<PayOfferClientRequest, FetchInvoiceClientResponse>(
                          new PayOfferClientRequest(offerString) { PayerNote = "nl-450" }, ct);
        Assert.Equal(FetchInvoiceStatus.Received, fetched.Status);
        PrintVector("invoice", fetched.Invoice!);
        PrintVector("nltg_invoice_request", _recorder.LastSent(OnionMessageConstants.InvoiceRequestType));
        Assert.Equal(fetched.Invoice, _recorder.LastReply(OnionMessageConstants.InvoiceType));

        // Act 2: CLN's invoice_error for a disabled offer
        await _fixture.Cln.CallAsync("disableoffer", ct, ("offer_id", offer["offer_id"]!.GetValue<string>()));
        var refused = await HandleAsync<PayOfferClientRequest, FetchInvoiceClientResponse>(
                          new PayOfferClientRequest(offerString), ct);
        Console.WriteLine($"[cln] fetch of a disabled offer: {refused.Status} ({refused.Error})");
        PrintVector("invoice_error", _recorder.LastReply(OnionMessageConstants.InvoiceErrorType));

        // Act 3: our offer, fetched by CLN: CLN's invoice_request
        var ours = await HandleAsync<CreateOfferClientRequest, CreateOfferClientResponse>(
                       new CreateOfferClientRequest
                       {
                           Amount = LightningMoney.Satoshis(5_000),
                           Description = "nltg nl-450 offer"
                       }, ct);
        PrintVector("nltg_offer", Bolt12Bech32.Decode(ours.Offer.Bolt12, Bolt12Constants.OfferHrp));
        var clnFetch = await _fixture.Cln.CallAsync("fetchinvoice", ct, ("offer", ours.Offer.Bolt12),
                                                    ("payer_note", "cln nl-450"), ("timeout", 60));
        PrintVector("invoice_request", _recorder.LastReceived(OnionMessageConstants.InvoiceRequestType));
        PrintVector("nltg_invoice",
                    Bolt12Bech32.Decode(clnFetch["invoice"]!.GetValue<string>(), Bolt12Constants.InvoiceHrp));
    }

    private void PrintVector(string kind, byte[] bytes) =>
        Console.WriteLine($"VECTOR {(kind.StartsWith("nltg", StringComparison.Ordinal) ? "nltg" : Tag)} "
                        + $"{kind} {Convert.ToHexStringLower(bytes)}");

    private async Task<TResponse> HandleAsync<TRequest, TResponse>(TRequest request, CancellationToken ct)
    {
        using var scope = _session!.Node.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<IClientCommandHandler<TRequest, TResponse>>();
        return await handler.HandleAsync(request, ct);
    }

    /// <summary>
    /// Records the BOLT 12 payload fields (64, 66, 68) our node sends, receives as replies and hands to its handlers.
    /// </summary>
    private sealed class Bolt12MessageRecorder
    {
        private readonly ConcurrentQueue<(string Direction, ulong Type, byte[] Value)> _records = new();

        public void Install(IServiceCollection services)
        {
            services.RemoveAll<IOnionMessageService>();
            services.AddSingleton<IOnionMessageService>(
                sp => new RecordingService(sp.GetRequiredService<Application.OnionMessages.OnionMessageService>(),
                                           this));

            var handlers = services.Where(d => d.ServiceType == typeof(IOnionMessageHandler)).ToList();
            foreach (var descriptor in handlers)
            {
                services.Remove(descriptor);
                services.AddSingleton<IOnionMessageHandler>(sp => new RecordingHandler(Resolve(sp, descriptor), this));
            }
        }

        public byte[] LastSent(ulong type) => Last("sent", type);

        public byte[] LastReply(ulong type) => Last("reply", type);

        public byte[] LastReceived(ulong type) => Last("received", type);

        private byte[] Last(string direction, ulong type) =>
            _records.LastOrDefault(r => r.Direction == direction && r.Type == type).Value
         ?? throw new InvalidOperationException($"No {direction} record of type {type}.");

        private void Record(string direction, OnionMessageContents? contents)
        {
            foreach (var record in contents?.Records ?? [])
                if (record.Type >= OnionMessageConstants.FirstPayloadFieldType)
                    _records.Enqueue((direction, record.Type, record.Value.ToArray()));
        }

        private static IOnionMessageHandler Resolve(IServiceProvider sp, ServiceDescriptor descriptor) =>
            descriptor.ImplementationInstance as IOnionMessageHandler
         ?? (descriptor.ImplementationFactory is { } factory
                 ? (IOnionMessageHandler)factory(sp)
                 : (IOnionMessageHandler)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));

        private sealed class RecordingService(IOnionMessageService inner, Bolt12MessageRecorder recorder)
            : IOnionMessageService
        {
            public bool IsAvailable => inner.IsAvailable;

            public void HandleIncoming(IPeerService peer, OnionMessageMessage message) =>
                inner.HandleIncoming(peer, message);

            public Task<OnionMessageSendResult> SendAsync(OnionMessageDestination destination,
                                                          OnionMessageContents contents, BlindedPath? replyPath,
                                                          CancellationToken cancellationToken = default)
            {
                recorder.Record("sent", contents);
                return inner.SendAsync(destination, contents, replyPath, cancellationToken);
            }

            public async Task<OnionMessageSendResult> SendAndWaitForReplyAsync(
                OnionMessageDestination destination, OnionMessageContents contents,
                IReadOnlyCollection<ulong> expectedReplyTypes, TimeSpan timeout,
                CancellationToken cancellationToken = default)
            {
                recorder.Record("sent", contents);
                var result = await inner.SendAndWaitForReplyAsync(destination, contents, expectedReplyTypes, timeout,
                                                                  cancellationToken);
                recorder.Record("reply", result.Reply?.Contents);
                return result;
            }
        }

        private sealed class RecordingHandler(IOnionMessageHandler inner, Bolt12MessageRecorder recorder)
            : IOnionMessageHandler
        {
            public IReadOnlyCollection<ulong> PayloadTypes => inner.PayloadTypes;

            public Task HandleAsync(ReceivedOnionMessage message, CancellationToken cancellationToken)
            {
                recorder.Record("received", message.Contents);
                return inner.HandleAsync(message, cancellationToken);
            }
        }
    }
}