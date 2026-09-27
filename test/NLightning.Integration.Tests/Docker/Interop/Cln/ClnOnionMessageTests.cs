using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils.Bolt12;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Node.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Payloads;
using Fixtures;
using Utils;

/// <summary>
/// Proof M6 (BOLT 4 onion messages, <c>docs/agents/BOLT12_PLAN.md</c>) against Core Lightning
/// <see cref="ClnFixture.ClnTag"/>: CLN peels and forwards onion messages we build (as an unblinded prefix hop with
/// <c>next_path_key_override</c>, and as the introduction node of our blinded path), CLN's own onions
/// (<c>injectonionmessage</c>, <c>fetchinvoice</c>) reach and are decoded by our nodes, our answers go back through
/// CLN's <c>reply_path</c>, our nodes forward CLN's messages along a blinded path, a burst and a junk message
/// disconnect nobody, and CLN sees <c>option_onion_messages</c> (bit 39) on us.
/// </summary>
/// <remarks>
/// <para>No channels: CLN relays onion messages between peers without channels (BOLT 4 reader SHOULD accept them;
/// OM-R-09). Every test starts its own nodes with <c>AllowExperimentalFeatures</c> and <c>OptionOnionMessages =
/// Optional</c> (D9: 39 stays experimental until this proof passes), a <see cref="RawOnionMessageRecorder"/> and a
/// <see cref="RecordingOnionMessageHandler"/> for the test payload types.</para>
/// <para>CLN v24.08 removed <c>sendonionmessage</c> (#7461); CLN has no raw send RPC, so CLN sends through
/// <c>injectonionmessage {path_key, message}</c> (it processes our onion as if a peer had sent it) and through BOLT 12
/// <c>fetchinvoice</c> to an offer made by <see cref="MinimalOfferEncoder"/>. Checked against v26.06.8 by hand: no extra
/// flag is needed (its init features set bit 39), <c>decode</c> reads our regtest offer as valid, and
/// <c>fetchinvoice</c> to an unreachable issuer fails with 1003 "could not route or connect directly", so the issuer
/// must be CLN's peer.</para>
/// <para>Written against the M6-0 contracts in parallel with lanes M6-A..D; the integrator runs it after the merge.
/// The nodes' onion-message services come from <c>AddNltgNodeServices</c> (M6-D's <c>AddOnionMessageServices</c> in
/// <c>AddApplicationServices</c>, M6-B's packet builder in <c>AddBitcoinInfrastructure</c>).</para>
/// </remarks>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnOnionMessageTests : IAsyncLifetime
{
    /// <summary>
    /// An odd <c>onionmsg_tlv</c> payload field from 64 up, for tests (BOLT 4: odd types are ignorable; the plan's
    /// "odd custom type ≥ 64 for tests").
    /// </summary>
    public const ulong TestFieldType = 65;

    /// <summary>
    /// The odd payload field of a test reply.
    /// </summary>
    public const ulong TestReplyFieldType = 67;

    /// <summary>
    /// BOLT 12 <c>invoice_error</c> TLV 5, <c>error</c> (UTF-8).
    /// </summary>
    public const ulong InvoiceErrorErrorType = 5;

    /// <summary>
    /// The text of the <c>invoice_error</c> we answer CLN's <c>invoice_request</c>s with.
    /// </summary>
    public const string ProofErrorText = "nltg-m6-proof";

    /// <summary>
    /// The default per-peer message rate of our limiter (plan §3.4: 20 messages/s per peer).
    /// </summary>
    public const int PerPeerMessagesPerSecond = 20;

    /// <summary>
    /// The meter of our onion-message counters (plan OM3-T3).
    /// </summary>
    public const string OnionMessagesMeterName = "NLightning.OnionMessages";

    private const int TestTimeoutMs = 6 * 60 * 1_000;
    private const int BurstSize = 200;

    private static readonly TimeSpan s_deliveryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_quietWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_clnCallTimeout = TimeSpan.FromSeconds(150);

    private readonly ClnFixture _fixture;
    private readonly DockerClient _docker = new DockerClientConfiguration().CreateClient();
    private readonly List<ProofNode> _nodes = [];

    public ClnOnionMessageTests(ClnFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    private ClnClient Cln => _fixture.Cln;

    private CompactPubKey ClnId => Convert.FromHexString(_fixture.ClnNodeId);

    public async ValueTask InitializeAsync()
    {
        var info = await Cln.GetInfoAsync(TestContext.Current.CancellationToken);
        Console.WriteLine($"[cln] version {info["version"]}, init features {info["our_features"]?["init"]}");
    }

    public async ValueTask DisposeAsync()
    {
        // Record what CLN says about onion messages (its surface for Proof M6), and what it disliked
        Console.WriteLine("[cln] CLN onion log lines:\n"
                        + await Cln.GetLogLinesAsync("onion", CancellationToken.None, 80));
        Console.WriteLine("[cln] CLN unusual/broken log lines so far:\n"
                        + await Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 60, "unusual"));
        foreach (var proof in _nodes)
        {
            Console.WriteLine($"[{proof.Node.Name}] onion traffic: {proof.Recorder.Describe()}; delivered "
                            + $"{proof.Handler.Received.Count}");
            await proof.Node.DisposeAsync();
        }

        if (DockerDiagnostics.CurrentTestFailed)
            await DockerDiagnostics.DumpContainerLogsAsync([ClnFixture.ClnContainerName], 300);

        _docker.Dispose();
    }

    /// <summary>
    /// Proof M6 (a): N1 and N2 are connected only to CLN. N1 sends N2 a message whose first hop is CLN as an unblinded
    /// prefix hop N1 blinds itself (<c>next_node_id</c> = N2, <c>next_path_key_override</c> = the first path_key of
    /// N2's blinded path), with the odd test field 65 = "hello". CLN peels and unblinds our construction and forwards
    /// it; N2 accepts CLN's forward and delivers the bytes.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_N1AndN2ConnectedOnlyThroughCln_When_N1SendsWithClnAsAPrefixHop_Then_N2DeliversTheField()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n1 = await StartNodeAsync("nltg-om-a1");
        var n2 = await StartNodeAsync("nltg-om-a2");
        await ConnectToClnAsync(n1, ct);
        await ConnectToClnAsync(n2, ct);
        Assert.False(n1.Node.IsConnectedTo(n2.Node.NodeId));
        var pathToN2 = CreateMessagePath(n2, [n2.Node.NodeId], pathId: null);
        var message = n1.Builder.Build([ClnId], pathToN2, TestContents("hello"), replyPath: null);

        // Act
        await SendRawAsync(n1, ClnId, message, ct);

        // Assert
        var delivered = await WaitForDeliveryAsync(n2, TestFieldType, "hello", ct);
        Assert.Equal(ClnId, delivered.FromPeer);
        Assert.Null(delivered.ReplyPath);
        Assert.Null(delivered.PathId);
        Assert.Single(delivered.Contents.Records);
        var received = Assert.Single(n2.Recorder.ReceivedOnionMessages);
        Console.WriteLine($"[cln] CLN's forward to N2: path_key {Convert.ToHexStringLower(received.PathKey!)}, "
                        + $"packet {received.OnionMessagePacket!.Length} bytes");
        Assert.Equal(OnionMessageConstants.SmallPayloadsLength + 66, received.OnionMessagePacket.Length);
        // CLN forwarded the path_key our prefix hop told it to use (next_path_key_override = first_path_key)
        Assert.Equal((byte[])pathToN2.FirstPathKey, received.PathKey);
        Assert.Empty(n2.Recorder.SentWarningsAndErrors);
        Assert.True(await Cln.IsConnectedAsync(n1.Node.NodeIdHex, ct));
        Assert.True(await Cln.IsConnectedAsync(n2.Node.NodeIdHex, ct));
    }

    /// <summary>
    /// Proof M6 (a), through the service: N2 hands out a blinded path whose introduction node is CLN, and N1's
    /// <see cref="IOnionMessageService.SendAsync"/> sends to it (CLN is N1's direct peer, so there is no prefix).
    /// CLN unblinds its own hop of our path (<c>next_node_id</c> = N2) and forwards.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ABlindedPathWhoseIntroductionIsCln_When_N1SendsThroughTheService_Then_N2Delivers()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n1 = await StartNodeAsync("nltg-om-s1");
        var n2 = await StartNodeAsync("nltg-om-s2");
        await ConnectToClnAsync(n1, ct);
        await ConnectToClnAsync(n2, ct);
        var pathId = RandomNumberGenerator.GetBytes(32);
        var pathToN2 = CreateMessagePath(n2, [ClnId, n2.Node.NodeId], pathId);

        // Act
        var result = await n1.Service.SendAsync(
                         OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(pathToN2)),
                         TestContents("through-cln"), replyPath: null, ct);

        // Assert
        Assert.Equal(OnionMessageSendStatus.Sent, result.Status);
        var delivered = await WaitForDeliveryAsync(n2, TestFieldType, "through-cln", ct);
        Assert.Equal(ClnId, delivered.FromPeer);
        Assert.NotNull(delivered.PathId);
        Assert.Equal(pathId, delivered.PathId.Value.ToArray());
        Assert.Single(n1.Recorder.SentOnionMessages);
        Assert.Empty(n2.Recorder.SentWarningsAndErrors);
    }

    /// <summary>
    /// Proof M6 (b): the test builds an onion with our packet builder whose first hop is CLN (a prefix hop with
    /// <c>next_path_key_override</c>) and whose recipient is N1, and hands it to CLN's <c>injectonionmessage</c>. CLN
    /// processes it as if a peer had sent it and forwards it to N1, which delivers it.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AnOnionForClnThenN1_When_InjectedIntoCln_Then_ClnForwardsItAndN1Delivers()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n1 = await StartNodeAsync("nltg-om-b1");
        await ConnectToClnAsync(n1, ct);
        var pathId = RandomNumberGenerator.GetBytes(16);
        var pathToN1 = CreateMessagePath(n1, [n1.Node.NodeId], pathId);
        var message = n1.Builder.Build([ClnId], pathToN1, TestContents("injected"), replyPath: null);

        // Act
        var injected = await InjectOnionMessageAsync(message, ct);

        // Assert
        Console.WriteLine($"[cln] injectonionmessage: {injected}");
        var delivered = await WaitForDeliveryAsync(n1, TestFieldType, "injected", ct);
        Assert.Equal(ClnId, delivered.FromPeer);
        Assert.Equal(pathId, delivered.PathId!.Value.ToArray());
        var received = Assert.Single(n1.Recorder.ReceivedOnionMessages);
        Assert.Equal((byte[])pathToN1.FirstPathKey, received.PathKey);
        Assert.Empty(n1.Recorder.SentWarningsAndErrors);
    }

    /// <summary>
    /// Proof M6 (c) and the reply-path round trip with CLN: N1 has an offer for its own node id (made by
    /// <see cref="MinimalOfferEncoder"/>, regtest chain). CLN's <c>fetchinvoice</c> sends N1 an
    /// <c>invoice_request</c> (64) with a CLN-made <c>reply_path</c>; N1 decodes it and its test handler answers
    /// <c>invoice_error</c> (68, <c>error</c> = <see cref="ProofErrorText"/>) through that reply path. CLN accepts our
    /// message through its blinded path and fails <c>fetchinvoice</c> with our error.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurOffer_When_ClnFetchesAnInvoice_Then_WeDecodeItsRequestAndItGetsOurErrorByItsReplyPath()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n1 = await StartNodeAsync("nltg-om-c1");
        await ConnectToClnAsync(n1, ct);
        var replies = AnswerInvoiceRequestsWithError(n1, ct);
        var offer = MinimalOfferEncoder.Encode(ChainConstants.Regtest.Value, "nltg m6 proof",
                                               (byte[])n1.Node.NodeId);
        Console.WriteLine($"[cln] offer {offer}: {(await TryDecodeAsync(offer, ct))?.ToJsonString()}");

        // Act
        var fetch = await CallClnRawAsync("fetchinvoice", ct, ("offer", offer), ("amount_msat", 1000),
                                          ("timeout", 60));

        // Assert: we got CLN's invoice_request with a reply path
        Console.WriteLine($"[cln] fetchinvoice (exit {fetch.ExitCode}): {fetch.Output}");
        var request = await WaitForDeliveryAsync(n1, OnionMessageConstants.InvoiceRequestType, ct);
        Assert.NotNull(request.ReplyPath);
        Assert.Equal(ClnId, request.FromPeer);
        var clnPathId = request.PathId is { } p ? Convert.ToHexStringLower(p.Span) : "none";
        Console.WriteLine($"[om] CLN's path to N1 carries path_id {clnPathId}");
        DescribeReplyPath("CLN's invoice_request", request.ReplyPath);
        Console.WriteLine($"VECTOR cln-{ClnFixture.ClnTag} invoice_request "
                        + Convert.ToHexStringLower(request.Contents.Records
                                                          .Single(r => r.Type
                                                                    == OnionMessageConstants.InvoiceRequestType)
                                                          .Value.Span));
        foreach (var raw in n1.Recorder.ReceivedOnionMessages)
            Console.WriteLine($"VECTOR cln-{ClnFixture.ClnTag} onion_message {raw.Hex}");

        // ...we sent our invoice_error through it
        var reply = await Poll.ForAsync(() => replies.TryPeek(out var r) ? r : null, s_deliveryTimeout,
                                        "N1's invoice_error sent", ct);
        Assert.Equal(OnionMessageSendStatus.Sent, reply.Status);
        Assert.NotEmpty(n1.Recorder.SentOnionMessages);

        // ...and CLN accepted it: fetchinvoice fails with our error text (CLN's surface for a received invoice_error)
        Assert.NotEqual(0, fetch.ExitCode);
        Assert.True(ContainsProofError(fetch.Output),
                    $"CLN's fetchinvoice does not report our invoice_error: {fetch.Output}");
        Assert.Empty(n1.Recorder.SentWarningsAndErrors);
        Assert.True(await Cln.IsConnectedAsync(n1.Node.NodeIdHex, ct));
    }

    /// <summary>
    /// Proof M6, forwarding by us: N2's offer has one <c>offer_path</c> N1 → N2 (introduction N1, no issuer id). CLN's
    /// <c>fetchinvoice</c> sends its <c>invoice_request</c> to N1, which unblinds its hop (<c>next_node_id</c> = N2)
    /// and forwards it to N2; N2 delivers it with the offer path's <c>path_id</c> and answers <c>invoice_error</c>
    /// through CLN's reply path, which CLN reports.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AnOfferPathThroughN1_When_ClnFetchesAnInvoice_Then_N1ForwardsToN2AndClnGetsN2sError()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n1 = await StartNodeAsync("nltg-om-f1");
        var n2 = await StartNodeAsync("nltg-om-f2");
        await ConnectToClnAsync(n1, ct);
        await ConnectToClnAsync(n2, ct);
        await n1.Node.ConnectToAsync(n2.Node, ct);
        var replies = AnswerInvoiceRequestsWithError(n2, ct);
        var pathId = RandomNumberGenerator.GetBytes(32);
        var offerPath = CreateMessagePath(n2, [n1.Node.NodeId, n2.Node.NodeId], pathId);
        var offer = MinimalOfferEncoder.Encode(ChainConstants.Regtest.Value, "nltg m6 forward", issuerId: null,
                                               [WireBlindedPath.FromBlindedPath(offerPath)]);
        Console.WriteLine($"[cln] offer {offer}: {(await TryDecodeAsync(offer, ct))?.ToJsonString()}");

        // Act
        var fetch = await CallClnRawAsync("fetchinvoice", ct, ("offer", offer), ("amount_msat", 1000),
                                          ("timeout", 60));

        // Assert: N1 forwarded CLN's message to N2, which delivered it through our offer path
        Console.WriteLine($"[cln] fetchinvoice (exit {fetch.ExitCode}): {fetch.Output}");
        var request = await WaitForDeliveryAsync(n2, OnionMessageConstants.InvoiceRequestType, ct);
        Assert.Equal(n1.Node.NodeId, request.FromPeer);
        Assert.Equal(pathId, request.PathId!.Value.ToArray());
        Assert.NotNull(request.ReplyPath);
        DescribeReplyPath("CLN's invoice_request through N1", request.ReplyPath);
        Assert.Empty(n1.Handler.Received);
        Assert.NotEmpty(n1.Recorder.ReceivedOnionMessages);
        Assert.NotEmpty(n1.Recorder.SentOnionMessages);

        // ...and CLN got N2's answer through its reply path
        var reply = await Poll.ForAsync(() => replies.TryPeek(out var r) ? r : null, s_deliveryTimeout,
                                        "N2's invoice_error sent", ct);
        Assert.Equal(OnionMessageSendStatus.Sent, reply.Status);
        Assert.NotEqual(0, fetch.ExitCode);
        Assert.True(ContainsProofError(fetch.Output),
                    $"CLN's fetchinvoice does not report N2's invoice_error: {fetch.Output}");
        Assert.Empty(n1.Recorder.SentWarningsAndErrors);
        Assert.Empty(n2.Recorder.SentWarningsAndErrors);
    }

    /// <summary>
    /// Our reply path through CLN: N2 sends N1 a message over N1's blinded path (introduction CLN) with a fresh
    /// <c>reply_path</c> of its own (<see cref="IOnionMessageService.SendAndWaitForReplyAsync"/>; its introduction node
    /// is N2's only peer, CLN). N1's handler answers through that reply path, CLN relays both ways, and N2's caller gets
    /// the reply.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_N2sReplyPath_When_N1AnswersThroughCln_Then_N2sCallerGetsTheReply()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n1 = await StartNodeAsync("nltg-om-r1");
        var n2 = await StartNodeAsync("nltg-om-r2");
        await ConnectToClnAsync(n1, ct);
        await ConnectToClnAsync(n2, ct);
        var answers = new ConcurrentQueue<OnionMessageSendResult>();
        n1.Handler.OnMessage = async (message, token) =>
        {
            if (message.ReplyPath is null || !HasField(message, TestFieldType))
                return;

            answers.Enqueue(await n1.Service.SendAsync(OnionMessageDestination.ToBlindedPath(message.ReplyPath),
                                                       OnionMessageContents.Single(
                                                           TestReplyFieldType, Encoding.UTF8.GetBytes("pong")),
                                                       replyPath: null, token));
        };
        var pathToN1 = CreateMessagePath(n1, [ClnId, n1.Node.NodeId], pathId: null);

        // Act
        var result = await n2.Service.SendAndWaitForReplyAsync(
                         OnionMessageDestination.ToBlindedPath(WireBlindedPath.FromBlindedPath(pathToN1)),
                         TestContents("ping"), [TestReplyFieldType], s_deliveryTimeout, ct);

        // Assert
        var ping = await WaitForDeliveryAsync(n1, TestFieldType, "ping", ct);
        DescribeReplyPath("N2's reply path", ping.ReplyPath);
        Assert.Equal(ClnId, ping.FromPeer);
        var answer = await Poll.ForAsync(() => answers.TryPeek(out var a) ? a : null, s_deliveryTimeout,
                                         "N1's answer sent", ct);
        Assert.Equal(OnionMessageSendStatus.Sent, answer.Status);
        Assert.Equal(OnionMessageSendStatus.Replied, result.Status);
        Assert.NotNull(result.Reply);
        Assert.Equal(ClnId, result.Reply.FromPeer);
        Assert.NotNull(result.Reply.PathId);
        var field = Assert.Single(result.Reply.Contents.Records, r => r.Type == TestReplyFieldType);
        Assert.Equal("pong", Encoding.UTF8.GetString(field.Value.Span));
        // The reply went to the caller, not to a handler
        Assert.DoesNotContain(n2.Handler.Received, m => HasField(m, TestReplyFieldType));
    }

    /// <summary>
    /// Proof M6 (d), rate limit: N2 sends <see cref="BurstSize"/> messages to N1 through CLN in about a second. CLN
    /// relays them (under its own limits, recorded), N1 admits at most its per-peer rate and drops the rest silently,
    /// and every connection stays up: nobody sends a warning or disconnects.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_ABurstThroughCln_When_N1RateLimits_Then_ExcessDroppedAndNobodyDisconnects()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n1 = await StartNodeAsync("nltg-om-d1");
        var n2 = await StartNodeAsync("nltg-om-d2");
        await ConnectToClnAsync(n1, ct);
        await ConnectToClnAsync(n2, ct);
        using var meter = new OnionMessageMeterRecorder();
        var messages = Enumerable.Range(0, BurstSize)
                                 .Select(i => n2.Builder.Build(
                                             [ClnId], CreateMessagePath(n1, [n1.Node.NodeId], pathId: null),
                                             TestContents($"burst-{i}"), replyPath: null))
                                 .ToList();

        // Act
        var stopwatch = Stopwatch.StartNew();
        foreach (var message in messages)
            await SendRawAsync(n2, ClnId, message, ct);
        stopwatch.Stop();
        var received = await WaitUntilQuietAsync(() => n1.Recorder.ReceivedOnionMessages.Count, ct);

        // Assert
        var delivered = n1.Handler.Received.Count(m => HasField(m, TestFieldType));
        var receivedTimes = n1.Recorder.ReceivedOnionMessages.Select(m => m.At).ToList();
        var firstSecond = receivedTimes.Count == 0
                              ? 0
                              : receivedTimes.Count(t => t - receivedTimes[0] < TimeSpan.FromSeconds(1));
        var rateDrops = meter.Sum(m => m.Instrument.Contains("drop", StringComparison.OrdinalIgnoreCase)
                                    && m.Tags.Contains("rate", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"[cln] N2 sent {BurstSize} in {stopwatch.Elapsed}; CLN forwarded {received} to N1 "
                        + $"({firstSecond} in the first second); N1 delivered {delivered}, rate drops {rateDrops}");
        Console.WriteLine($"[meter] {meter.Describe()}");
        Console.WriteLine("[cln] CLN rate-limit log lines:\n" + await Cln.GetLogLinesAsync("limit", ct, 20));
        Assert.True(received > 0, "CLN forwarded nothing to N1");
        Assert.True(delivered <= received);
        if (firstSecond > 2 * PerPeerMessagesPerSecond)
        {
            Assert.True(delivered < received,
                        $"N1 delivered all {received} messages, {firstSecond} of them within one second");
            Assert.True(rateDrops > 0, "N1 counted no rate drop");
        }

        Assert.Empty(n1.Recorder.SentWarningsAndErrors);
        Assert.Empty(n2.Recorder.SentWarningsAndErrors);
        await Poll.StaysTrueAsync(() => n1.Node.IsConnectedTo(ClnId) && n2.Node.IsConnectedTo(ClnId), s_quietWindow,
                                  "N1 and N2 stay connected to CLN", ct);
        Assert.True(await Cln.IsConnectedAsync(n1.Node.NodeIdHex, ct), "CLN dropped N1");
        Assert.True(await Cln.IsConnectedAsync(n2.Node.NodeIdHex, ct), "CLN dropped N2");
    }

    /// <summary>
    /// Proof M6 (d), junk: a message with a corrupt HMAC from a peer is ignored (not delivered, no warning to the
    /// sender, no disconnect); a good message right after it is delivered.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AMessageWithACorruptHmac_When_Received_Then_IgnoredWithoutAWarning()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n1 = await StartNodeAsync("nltg-om-j1");
        var n2 = await StartNodeAsync("nltg-om-j2");
        await n2.Node.ConnectToAsync(n1.Node, ct);
        var good = n2.Builder.Build([], CreateMessagePath(n1, [n1.Node.NodeId], pathId: null),
                                    TestContents("after-junk"), replyPath: null);
        var junkSource = n2.Builder.Build([], CreateMessagePath(n1, [n1.Node.NodeId], pathId: null),
                                          TestContents("junk"), replyPath: null);
        var packet = junkSource.Payload.OnionMessagePacket.ToArray();
        packet[^1] ^= 0x01;
        var junk = new OnionMessageMessage(new OnionMessagePayload(junkSource.Payload.PathKey, packet));

        // Act
        await SendRawAsync(n2, n1.Node.NodeId, junk, ct);
        await Poll.UntilAsync(() => n1.Recorder.ReceivedOnionMessages.Count == 1, s_deliveryTimeout,
                              "N1 received the junk", ct);
        await SendRawAsync(n2, n1.Node.NodeId, good, ct);

        // Assert
        await WaitForDeliveryAsync(n1, TestFieldType, "after-junk", ct);
        Assert.DoesNotContain(n1.Handler.Received, m => HasField(m, TestFieldType, "junk"));
        Assert.Equal(2, n1.Recorder.ReceivedOnionMessages.Count);
        Assert.Empty(n1.Recorder.SentWarningsAndErrors);
        await Poll.StaysTrueAsync(() => n1.Node.IsConnectedTo(n2.Node.NodeId) && n2.Node.IsConnectedTo(n1.Node.NodeId),
                                  s_quietWindow, "N1 and N2 stay connected", ct);
    }

    /// <summary>
    /// Proof M6 (e): CLN's <c>listpeers</c> shows bit 39 (<c>option_onion_messages</c>, optional) in our init
    /// features, and CLN advertises 38/39 itself, which our node records for CLN.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_OurNodeWithOnionMessages_When_ConnectedToCln_Then_BothSeeTheOthersFeature()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n1 = await StartNodeAsync("nltg-om-e1");

        // Act
        await ConnectToClnAsync(n1, ct);

        // Assert
        var peer = await Cln.GetPeerAsync(n1.Node.NodeIdHex, ct);
        var ourFeatures = peer?["features"]?.GetValue<string>() ?? string.Empty;
        var clnFeatures = (await Cln.GetInfoAsync(ct))["our_features"]?["init"]?.GetValue<string>() ?? string.Empty;
        Console.WriteLine($"[cln] CLN lists our features {ourFeatures}; CLN's init features {clnFeatures}");
        Assert.True(IsBitSet(ourFeatures, (int)Feature.OptionOnionMessages), "CLN does not see bit 39 on us");
        Assert.True(IsBitSet(clnFeatures, (int)Feature.OptionOnionMessages)
                 || IsBitSet(clnFeatures, (int)Feature.OptionOnionMessages - 1),
                    "CLN does not advertise option_onion_messages");
        Assert.True(n1.Node.PeerManager.GetPeer(ClnId)!.Features.IsFeatureSet(Feature.OptionOnionMessages));
    }

    /// <summary>
    /// Whether the big-endian feature hex CLN prints sets <paramref name="bit"/>.
    /// </summary>
    internal static bool IsBitSet(string featuresHex, int bit)
    {
        var bytes = Convert.FromHexString(featuresHex);
        var index = bytes.Length - 1 - bit / 8;
        return index >= 0 && (bytes[index] & (1 << (bit % 8))) != 0;
    }

    /// <summary>
    /// The invoice_error we answer with: TLV 5 <c>error</c> = <see cref="ProofErrorText"/>.
    /// </summary>
    internal static byte[] ProofInvoiceError()
    {
        using var stream = new MemoryStream();
        MinimalOfferEncoder.WriteRecord(stream, InvoiceErrorErrorType, Encoding.UTF8.GetBytes(ProofErrorText));
        return stream.ToArray();
    }

    /// <summary>
    /// Whether CLN's output names our error, as text or inside the hex of the invoice_error it received.
    /// </summary>
    internal static bool ContainsProofError(string clnOutput) =>
        clnOutput.Contains(ProofErrorText, StringComparison.Ordinal)
     || clnOutput.Contains(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(ProofErrorText)),
                           StringComparison.OrdinalIgnoreCase);

    private static OnionMessageContents TestContents(string text) =>
        OnionMessageContents.Single(TestFieldType, Encoding.UTF8.GetBytes(text));

    private static bool HasField(ReceivedOnionMessage message, ulong type, string? text = null) =>
        message.Contents.Records.Any(r => r.Type == type
                                       && (text is null || Encoding.UTF8.GetString(r.Value.Span) == text));

    private static PrivKey NewSessionKey() => new(RandomNumberGenerator.GetBytes(32));

    private static void DescribeReplyPath(string what, WireBlindedPath? path)
    {
        if (path is null)
        {
            Console.WriteLine($"[om] {what}: no reply path");
            return;
        }

        var first = path.FirstNode.NodeId is { } nodeId
                        ? $"node {Convert.ToHexStringLower(nodeId)}"
                        : $"scid {path.FirstNode.ShortChannelId} direction {path.FirstNode.Direction}";
        Console.WriteLine($"[om] {what}: introduction {first}, {path.Hops.Count} hops, encrypted data lengths "
                        + string.Join('/', path.Hops.Select(h => h.EncryptedRecipientData.Length)));
    }

    /// <summary>
    /// A blinded message path created by <paramref name="creator"/> over <paramref name="nodeIds"/> (introduction
    /// first): every hop but the last gets <c>next_node_id</c>, the last gets <paramref name="pathId"/> if any.
    /// </summary>
    private static BlindedPath CreateMessagePath(ProofNode creator, IReadOnlyList<CompactPubKey> nodeIds,
                                                 byte[]? pathId)
    {
        var blinding = creator.Node.Services.GetRequiredService<IRouteBlindingService>();
        var data = new List<byte[]>();
        for (var i = 0; i < nodeIds.Count - 1; i++)
            data.Add(blinding.EncodeRecipientData(new BlindedRecipientData { NextNodeId = nodeIds[i + 1] }));
        data.Add(blinding.EncodeRecipientData(new BlindedRecipientData { PathId = pathId }));

        return blinding.CreateBlindedPath(nodeIds, data, NewSessionKey());
    }

    private static async Task SendRawAsync(ProofNode from, CompactPubKey peerId, OnionMessageMessage message,
                                           CancellationToken ct)
    {
        var peer = from.Node.PeerManager.GetPeer(peerId)
                ?? throw new InvalidOperationException($"{from.Node.Name} is not connected to {peerId}");
        if (!peer.TryGetPeerService(out var peerService))
            throw new InvalidOperationException($"{from.Node.Name} has no connection to {peerId}");

        await peerService.SendOnionMessageAsync(message, ct);
    }

    private static Task<ReceivedOnionMessage> WaitForDeliveryAsync(ProofNode node, ulong type, CancellationToken ct) =>
        Poll.ForAsync(() => node.Handler.Received.FirstOrDefault(m => HasField(m, type)), s_deliveryTimeout,
                      $"{node.Node.Name} delivered a message with field {type}", ct);

    private static Task<ReceivedOnionMessage> WaitForDeliveryAsync(ProofNode node, ulong type, string text,
                                                                   CancellationToken ct) =>
        Poll.ForAsync(() => node.Handler.Received.FirstOrDefault(m => HasField(m, type, text)), s_deliveryTimeout,
                      $"{node.Node.Name} delivered field {type} = \"{text}\"", ct);

    /// <summary>
    /// Waits until <paramref name="count"/> has not changed for <see cref="s_quietWindow"/>, and returns it.
    /// </summary>
    private static async Task<int> WaitUntilQuietAsync(Func<int> count, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + s_deliveryTimeout;
        var last = count();
        var lastChange = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            var now = count();
            if (now != last)
            {
                last = now;
                lastChange = DateTime.UtcNow;
            }
            else if (last > 0 && DateTime.UtcNow - lastChange >= s_quietWindow)
            {
                break;
            }
        }

        return last;
    }

    /// <summary>
    /// Makes <paramref name="node"/>'s handler answer every <c>invoice_request</c> with our <c>invoice_error</c>
    /// through the request's reply path.
    /// </summary>
    /// <returns>The send result of each answer.</returns>
    private static ConcurrentQueue<OnionMessageSendResult> AnswerInvoiceRequestsWithError(ProofNode node,
        CancellationToken ct)
    {
        var results = new ConcurrentQueue<OnionMessageSendResult>();
        node.Handler.OnMessage = async (message, token) =>
        {
            if (message.ReplyPath is null || !HasField(message, OnionMessageConstants.InvoiceRequestType))
                return;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, ct);
            results.Enqueue(await node.Service.SendAsync(OnionMessageDestination.ToBlindedPath(message.ReplyPath),
                                                         OnionMessageContents.Single(
                                                             OnionMessageConstants.InvoiceErrorType,
                                                             ProofInvoiceError()),
                                                         replyPath: null, linked.Token));
        };
        return results;
    }

    private async Task<ProofNode> StartNodeAsync(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new RecordingOnionMessageHandler(
            [OnionMessageConstants.InvoiceRequestType, TestFieldType, TestReplyFieldType]);
        var recorder = new RawOnionMessageRecorder();
        var node = await NLightningTestNode.CreateAsync(_fixture.Bitcoin, name, configureNodeOptions: o =>
        {
            o.Features.AllowExperimentalFeatures = true;
            o.Features.OptionOnionMessages = FeatureSupport.Optional;
        });
        node.ConfigureServices = services =>
        {
            recorder.Install(services);
            services.AddSingleton<IOnionMessageHandler>(handler);
        };
        var proof = new ProofNode(node, recorder, handler);
        _nodes.Add(proof);
        await node.StartAsync(ct);
        Assert.True(proof.Service.IsAvailable, $"{name}: onion messages are off");
        return proof;
    }

    private async Task ConnectToClnAsync(ProofNode node, CancellationToken ct)
    {
        await node.Node.PeerManager.ConnectToPeerAsync(new PeerAddressInfo(_fixture.ClnAddress)).WaitAsync(ct);
        await Poll.UntilAsync(async () => node.Node.IsConnectedTo(ClnId)
                                       && await Cln.IsConnectedAsync(node.Node.NodeIdHex, ct),
                              s_connectTimeout, $"{node.Node.Name} and CLN connected", ct);
    }

    /// <summary>
    /// CLN's <c>injectonionmessage path_key message</c>: <paramref name="message"/>'s path_key and
    /// <c>onion_message_packet</c> (hex), processed by CLN as if a peer had sent them. Checked against v26.06.8 by hand:
    /// <c>help</c> lists <c>injectonionmessage path_key message</c>, the old name <c>blinding</c> is refused (-32602),
    /// and a bad packet fails with code -1 <c>onion_message_parse: can't parse onionpacket</c>.
    /// </summary>
    private async Task<string> InjectOnionMessageAsync(OnionMessageMessage message, CancellationToken ct)
    {
        var pathKey = Convert.ToHexStringLower((byte[])message.Payload.PathKey);
        var packet = Convert.ToHexStringLower(message.Payload.OnionMessagePacket.Span);
        return (await Cln.CallAsync("injectonionmessage", ct, ("path_key", pathKey), ("message", packet)))
           .ToJsonString();
    }

    private async Task<JsonNode?> TryDecodeAsync(string bolt12, CancellationToken ct)
    {
        try
        {
            return await Cln.CallAsync("decode", ct, ("string", bolt12));
        }
        catch (ClnRpcException e)
        {
            Console.WriteLine($"[cln] decode failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Runs <c>lightning-cli</c> in the CLN container and returns its exit code and whole output, error data included
    /// (<see cref="ClnClient"/> keeps only an error's message, and CLN puts a received <c>invoice_error</c> in the
    /// error's data).
    /// </summary>
    private async Task<ClnRawResult> CallClnRawAsync(string method, CancellationToken ct,
                                                     params (string Key, object Value)[] parameters)
    {
        List<string> cmd = ["lightning-cli", "--network=regtest", "--notifications=none", "-k", method];
        cmd.AddRange(parameters.Select(p => $"{p.Key}={Convert.ToString(p.Value, CultureInfo.InvariantCulture)}"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_clnCallTimeout);
        var exec = await _docker.Exec.ExecCreateContainerAsync(ClnFixture.ClnContainerName,
                                                               new ContainerExecCreateParameters
                                                               {
                                                                   Cmd = cmd,
                                                                   AttachStdout = true,
                                                                   AttachStderr = true
                                                               }, timeout.Token);
        string stdout, stderr;
        using (var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, false, timeout.Token))
            (stdout, stderr) = await stream.ReadOutputToEndAsync(timeout.Token);

        var inspect = await _docker.Exec.InspectContainerExecAsync(exec.ID, timeout.Token);
        return new ClnRawResult(inspect.ExitCode, $"{stdout} {stderr}".Trim());
    }

    private sealed record ClnRawResult(long ExitCode, string Output);

    /// <summary>
    /// One of our nodes in the proof, with its wire recorder and test handler.
    /// </summary>
    private sealed record ProofNode(NLightningTestNode Node, RawOnionMessageRecorder Recorder,
                                    RecordingOnionMessageHandler Handler)
    {
        public IOnionMessageService Service => Node.Services.GetRequiredService<IOnionMessageService>();

        public IOnionMessagePacketBuilder Builder => Node.Services.GetRequiredService<IOnionMessagePacketBuilder>();
    }

    /// <summary>
    /// A test <see cref="IOnionMessageHandler"/>: records every delivered message, then runs
    /// <see cref="OnMessage"/> if set.
    /// </summary>
    private sealed class RecordingOnionMessageHandler(IReadOnlyCollection<ulong> payloadTypes) : IOnionMessageHandler
    {
        private readonly ConcurrentQueue<ReceivedOnionMessage> _received = new();

        public IReadOnlyCollection<ulong> PayloadTypes { get; } = payloadTypes;

        public IReadOnlyList<ReceivedOnionMessage> Received => _received.ToArray();

        public Func<ReceivedOnionMessage, CancellationToken, Task>? OnMessage { get; set; }

        public async Task HandleAsync(ReceivedOnionMessage message, CancellationToken cancellationToken)
        {
            _received.Enqueue(message);
            if (OnMessage is { } onMessage)
                await onMessage(message, cancellationToken);
        }
    }

    /// <summary>
    /// Listens to the <see cref="OnionMessagesMeterName"/> meter of every node in the process (the counter names and
    /// tags are lane M6-D's; the proof matches them loosely and prints them).
    /// </summary>
    private sealed class OnionMessageMeterRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<MeterMeasurement> _measurements = new();

        public OnionMessageMeterRecorder()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == OnionMessagesMeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public long Sum(Func<MeterMeasurement, bool> predicate) =>
            _measurements.Where(predicate).Sum(m => m.Value);

        public string Describe() =>
            string.Join(", ", _measurements.GroupBy(m => $"{m.Instrument}{m.Tags}")
                                           .Select(g => $"{g.Key}: {g.Sum(m => m.Value)}"));

        public void Dispose() => _listener.Dispose();

        private void Add(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var text = new StringBuilder();
            foreach (var tag in tags)
                text.Append('{').Append(tag.Key).Append('=').Append(tag.Value).Append('}');
            _measurements.Enqueue(new MeterMeasurement(instrument.Name, text.ToString(), value));
        }
    }

    private sealed record MeterMeasurement(string Instrument, string Tags, long Value);
}

/// <summary>
/// Container-free checks of the Proof M6 helpers: <see cref="MinimalOfferEncoder"/> against the BOLT 12
/// <c>offers-test.json</c> vectors, the <see cref="RawOnionMessageRecorder"/> wire parsing and the feature-bit reader.
/// </summary>
public sealed class ClnOnionMessageProofHelperTests
{
    private const string BobIssuerId = "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619";
    private const string Description = "Test vectors";

    [Fact]
    public void Given_OnlyAnIssuerId_When_Encoded_Then_EqualsTheMinimalOfferVector()
    {
        // Arrange
        var issuer = Convert.FromHexString(BobIssuerId);

        // Act
        var offer = MinimalOfferEncoder.Encode(null, null, issuer);

        // Assert
        Assert.Equal("lno1zcss9mk8y3wkklfvevcrszlmu23kfrxh49px20665dqwmn4p72pksese", offer);
    }

    [Fact]
    public void Given_ADescription_When_Encoded_Then_EqualsTheVector()
    {
        // Arrange
        var issuer = Convert.FromHexString(BobIssuerId);

        // Act
        var offer = MinimalOfferEncoder.Encode(null, Description, issuer);

        // Assert
        Assert.Equal("lno1pgx9getnwss8vetrw3hhyuckyypwa3eyt44h6txtxquqh7lz5djge4afgfjn7k4rgrkuag0jsd5xvxg", offer);
    }

    [Fact]
    public void Given_TheTestnetChain_When_Encoded_Then_EqualsTheVector()
    {
        // Arrange
        var chain = Convert.FromHexString("43497fd7f826957108f4a30fd9cec3aeba79972084e90ead01ea330900000000");

        // Act
        var offer = MinimalOfferEncoder.Encode(chain, Description, Convert.FromHexString(BobIssuerId));

        // Assert
        Assert.Equal("lno1qgsyxjtl6luzd9t3pr62xr7eemp6awnejusgf6gw45q75vcfqqqqqqq2p32x2um5ypmx2cm5dae8x93pqthvwfzadd7"
                   + "jejes8q9lhc4rvjxd022zv5l44g6qah82ru5rdpnpj", offer);
    }

    [Fact]
    public void Given_ABlindedPathViaBob_When_Encoded_Then_EqualsTheVector()
    {
        // Arrange
        var path = VectorPath(SciddirOrPubkey.FromNodeId(
                                  Convert.FromHexString(
                                      "0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c")));

        // Act
        var offer = MinimalOfferEncoder.Encode(null, Description, Convert.FromHexString(BobIssuerId), [path]);

        // Assert
        Assert.Equal("lno1pgx9getnwss8vetrw3hhyucs5ypjgef743p5fzqq9nqxh0ah7y87rzv3ud0eleps9kl2d5348hq2k8qzqgpqyqszqgpqyq"
                   + "szqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgqpqqqq"
                   + "qqqqqqqqqqqqqqqqqqqqqqqzqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqqzq3zyg3zyg3zyg3vggzam"
                   + "rjghtt05kvkvpcp0a79gmy3nt6jsn98ad2xs8de6sl9qmgvcvs", offer);
    }

    [Fact]
    public void Given_ABlindedPathWithASciddir_When_Encoded_Then_EqualsTheVector()
    {
        // Arrange: short_channel_id 0x0x42, direction 0
        var path = VectorPath(SciddirOrPubkey.FromShortChannelId(42UL, 0));

        // Act
        var offer = MinimalOfferEncoder.Encode(null, Description, Convert.FromHexString(BobIssuerId), [path]);

        // Assert
        Assert.Equal("lno1pgx9getnwss8vetrw3hhyucs3yqqqqqqqqqqqqp2qgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpq"
                   + "yqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqqyqqqqqqqqqqqqqqqqqqqqqqqqqqqgpqyqszqgpqyq"
                   + "szqgpqyqszqgpqyqszqgpqyqszqgpqyqszqgpqyqqgzyg3zyg3zyg3z93pqthvwfzadd7jejes8q9lhc4rvjxd022zv5l44g6q"
                   + "ah82ru5rdpnpj", offer);
    }

    [Fact]
    public void Given_NoIssuerAndNoPath_When_Encoded_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => MinimalOfferEncoder.Encode(null, Description, null));
    }

    [Fact]
    public void Given_ARecordedOnionMessage_When_Parsed_Then_PathKeyAndPacketAreItsFields()
    {
        // Arrange: u16 513, path_key, u16 len = 70, 70 packet bytes
        var pathKey = Convert.FromHexString(BobIssuerId);
        var packet = Enumerable.Range(0, 70).Select(i => (byte)i).ToArray();
        var wire = new byte[] { 0x02, 0x01 }.Concat(pathKey).Concat(new byte[] { 0x00, 70 }).Concat(packet).ToArray();
        var recorder = new RawOnionMessageRecorder();

        // Act
        recorder.Record(wire, outbound: false);
        recorder.Record([0x00, 0x01, 0x00], outbound: true);
        recorder.Record([0x01, 0x00], outbound: true);

        // Assert
        var recorded = Assert.Single(recorder.ReceivedOnionMessages);
        Assert.Equal(pathKey, recorded.PathKey);
        Assert.Equal(packet, recorded.OnionMessagePacket);
        Assert.Single(recorder.SentWarningsAndErrors);
        Assert.Empty(recorder.SentOnionMessages);
    }

    [Theory]
    [InlineData("8000000000", 39, true)]
    [InlineData("4000000000", 38, true)]
    [InlineData("4000000000", 39, false)]
    [InlineData("02", 1, true)]
    [InlineData("02", 39, false)]
    public void Given_ClnFeatureHex_When_ReadingABit_Then_BigEndianBitOrder(string hex, int bit, bool expected)
    {
        // Act / Assert
        Assert.Equal(expected, ClnOnionMessageTests.IsBitSet(hex, bit));
    }

    [Fact]
    public void Given_OurInvoiceError_When_Checked_Then_ItCarriesTheProofText()
    {
        // Arrange
        var error = ClnOnionMessageTests.ProofInvoiceError();

        // Act / Assert
        Assert.Equal(ClnOnionMessageTests.InvoiceErrorErrorType, error[0]);
        Assert.Equal(ClnOnionMessageTests.ProofErrorText.Length, error[1]);
        Assert.True(ClnOnionMessageTests.ContainsProofError(Convert.ToHexStringLower(error)));
        Assert.True(ClnOnionMessageTests.ContainsProofError($"{{\"error\":\"{ClnOnionMessageTests.ProofErrorText}\"}}"));
    }

    private static WireBlindedPath VectorPath(SciddirOrPubkey firstNode)
    {
        var twos = Enumerable.Repeat((byte)0x02, 33).ToArray();
        return new WireBlindedPath(firstNode, twos,
                                   [
                                       new BlindedPathHop(twos, new byte[16]),
                                       new BlindedPathHop(twos, Enumerable.Repeat((byte)0x11, 8).ToArray())
                                   ]);
    }
}