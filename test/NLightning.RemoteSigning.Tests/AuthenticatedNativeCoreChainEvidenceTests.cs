using System.Net;
using System.Text;
using System.Text.Json;
using NBitcoin;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Protocol.Constants;
using NLightning.Infrastructure.Bitcoin.Managers;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

public sealed class AuthenticatedNativeCoreChainEvidenceTests
{
    [Fact]
    public void Given_AuthenticatedCoreAndSignerDerivedOwnership_When_ReadingOutput_Then_CoreValueAndScriptCarryEnrolledLabels()
    {
        using var fixture = new Fixture();
        fixture.Evidence.RequireFresh(fixture.Binding);
        var output = fixture.Evidence.GetOutput(fixture.Binding, fixture.TransactionId, 3);
        fixture.Evidence.RequireUnchanged(fixture.Binding);

        Assert.Equal(1_000, output.AmountSatoshis);
        Assert.Equal(fixture.Script, output.ScriptPubKey);
        Assert.Equal(fixture.Binding.OwnerId, output.OwnerId);
        Assert.Equal(fixture.Binding.NodeId, output.NodeId);
        Assert.Equal(fixture.TransactionId, output.TransactionId);
        Assert.Equal(3u, output.OutputIndex);
        Assert.True(output.Unspent);
        Assert.True(fixture.Core.CheckedAuthentication);
        Assert.True(fixture.Core.IncludedMempool);
    }

    [Theory]
    [InlineData("mature-coinbase")]
    [InlineData("unconfirmed")]
    public void Given_SpendableCoreOutput_When_Reading_Then_MatureCoinbaseAndUnconfirmedNonCoinbaseAreAccepted(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Core.Attack = scenario;
        fixture.Evidence.RequireFresh(fixture.Binding);
        Assert.True(fixture.Evidence.GetOutput(fixture.Binding, fixture.TransactionId, 3).Unspent);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("genesis")]
    [InlineData("initial-download")]
    [InlineData("headers-ahead")]
    [InlineData("verification-progress")]
    [InlineData("stale-tip")]
    [InlineData("future-tip")]
    [InlineData("missing-tip-time")]
    [InlineData("wrong-response-id")]
    [InlineData("rpc-error")]
    [InlineData("authentication")]
    [InlineData("oversized-answer")]
    [InlineData("tip-changes-during-start")]
    public void Given_UnusableNetworkOrFreshnessProof_When_StartingBatch_Then_NoOutputEvidenceIsAccepted(string attack)
    {
        using var fixture = new Fixture();
        fixture.Core.Attack = attack;
        Assert.ThrowsAny<Exception>(() => fixture.Evidence.RequireFresh(fixture.Binding));
        Assert.Throws<InvalidOperationException>(() => fixture.Evidence.GetOutput(fixture.Binding, fixture.TransactionId, 3));
        Assert.Equal(0, fixture.Core.OutputRequests);
    }

    [Theory]
    [InlineData("spent")]
    [InlineData("output-tip")]
    [InlineData("negative-amount")]
    [InlineData("fractional-satoshi")]
    [InlineData("excess-amount")]
    [InlineData("empty-script")]
    [InlineData("invalid-script")]
    [InlineData("negative-confirmations")]
    [InlineData("excess-confirmations")]
    [InlineData("immature-coinbase")]
    public void Given_InvalidCoreOutput_When_Reading_Then_BatchFailsClosed(string attack)
    {
        using var fixture = new Fixture();
        fixture.Evidence.RequireFresh(fixture.Binding);
        fixture.Core.Attack = attack;
        Assert.ThrowsAny<Exception>(() => fixture.Evidence.GetOutput(fixture.Binding, fixture.TransactionId, 3));
        Assert.Throws<InvalidOperationException>(() => fixture.Evidence.RequireUnchanged(fixture.Binding));
    }

    [Fact]
    public void Given_UnregisteredScript_When_Reading_Then_CoreCannotSupplyOwnerLabels()
    {
        using var fixture = new Fixture();
        fixture.Core.Script = "0014" + new string('f', 40);
        fixture.Evidence.RequireFresh(fixture.Binding);
        var output = fixture.Evidence.GetOutput(fixture.Binding, fixture.TransactionId, 3);
        Assert.Empty(output.OwnerId);
        Assert.Empty(output.NodeId);
        Assert.False(fixture.Registry.IsOwned(fixture.Binding, output.ScriptPubKey));
        Assert.False(fixture.Registry.IsOwned(fixture.Binding with { OwnerId = "other-owner" }, fixture.Script));
    }

    [Fact]
    public void Given_OutputAlreadyRead_When_TipChangesBeforeBatchCompletion_Then_FinalCheckRejectsEvidence()
    {
        using var fixture = new Fixture();
        fixture.Evidence.RequireFresh(fixture.Binding);
        fixture.Evidence.GetOutput(fixture.Binding, fixture.TransactionId, 3);
        fixture.Core.Tip = new string('c', 64);
        Assert.Throws<InvalidOperationException>(() => fixture.Evidence.RequireUnchanged(fixture.Binding));
        Assert.Throws<InvalidOperationException>(() => fixture.Evidence.GetOutput(fixture.Binding, fixture.TransactionId, 3));
    }

    [Fact]
    public void Given_ExpiredBatch_When_Reading_Then_CoreIsNotAskedToRefreshItImplicitly()
    {
        using var fixture = new Fixture();
        fixture.Evidence.RequireFresh(fixture.Binding);
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Throws<InvalidOperationException>(() => fixture.Evidence.GetOutput(fixture.Binding, fixture.TransactionId, 3));
        Assert.Equal(0, fixture.Core.OutputRequests);
    }

    [Fact]
    public void Given_AnotherEnrollment_When_RequestingEvidence_Then_TransportIsNeverCalled()
    {
        using var fixture = new Fixture();
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Evidence.RequireFresh(fixture.Binding with { NodeId = "other-node" }));
        Assert.Equal(0, fixture.Core.Requests);
    }

    [Fact]
    public void Given_TimedOutCore_When_StartingBatch_Then_NoEvidenceRemainsUsable()
    {
        using var fixture = new Fixture(TimeSpan.FromMilliseconds(50));
        fixture.Core.Attack = "timeout";
        Assert.Throws<InvalidOperationException>(() => fixture.Evidence.RequireFresh(fixture.Binding));
        Assert.Throws<InvalidOperationException>(() => fixture.Evidence.RequireUnchanged(fixture.Binding));
    }

    [Fact]
    public void Given_RedirectedCoreResponse_When_StartingBatch_Then_EvidenceIsRejected()
    {
        using var fixture = new Fixture();
        fixture.Core.Attack = "redirect";
        Assert.Throws<InvalidOperationException>(() => fixture.Evidence.RequireFresh(fixture.Binding));
        Assert.Equal(1, fixture.Core.Requests);
    }

    [Theory]
    [InlineData("http://remote.example", false)]
    [InlineData("https://user:password@remote.example", false)]
    [InlineData("file:///tmp/evidence", false)]
    public void Given_UntrustedEndpoint_When_ConstructingAdapter_Then_ConfigurationIsRejected(string endpoint, bool trustedPlainHttp)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => new AuthenticatedNativeCoreChainEvidence(fixture.Binding,
            fixture.Options with { Endpoint = new Uri(endpoint), AllowTrustedPlainHttp = trustedPlainHttp },
            fixture.Registry, "signer", "password"));
    }

    [Fact]
    public void Given_DuplicateDerivationOrWrongSignerKeys_When_InstallingOwnership_Then_RegistryIsRejected()
    {
        using var fixture = new Fixture();
        var locator = new NativeWalletKeyLocator(0, false, AddressType.P2Wpkh);
        Assert.Throws<ArgumentException>(() => new NativeSignerWalletScriptRegistry(fixture.Binding, fixture.Keys, [locator, locator]));
        Assert.Throws<UnauthorizedAccessException>(() => new NativeSignerWalletScriptRegistry(fixture.Binding with
        { PublicKey = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798" }, fixture.Keys, [locator]));
    }

    private sealed class Fixture : IDisposable
    {
        public SecureKeyManager Keys { get; }
        public NativeSignerBinding Binding { get; }
        public byte[] Script { get; }
        public string TransactionId { get; } = new('a', 64);
        public NativeSignerWalletScriptRegistry Registry { get; }
        public Clock Clock { get; } = new();
        public CoreHandler Core { get; }
        public NativeCoreChainEvidenceOptions Options { get; }
        public AuthenticatedNativeCoreChainEvidence Evidence { get; }

        public Fixture(TimeSpan? timeout = null)
        {
            Keys = SecureKeyManager.FromSeed(Enumerable.Repeat((byte)1, 32).ToArray(), NetworkConstants.Regtest, _ => { });
            Binding = new NativeSignerBinding("node-a", "owner-a", "signer-a", "regtest", Keys.GetNodePubKey().ToString());
            Registry = new NativeSignerWalletScriptRegistry(Binding, Keys, [new NativeWalletKeyLocator(0, false, AddressType.P2Wpkh)]);
            Script = new PubKey((byte[])Keys.GetWalletPublicKey(0, false, AddressType.P2Wpkh)).WitHash.ScriptPubKey.ToBytes();
            Core = new CoreHandler(Clock, Convert.ToHexString(Script), TransactionId);
            Options = new NativeCoreChainEvidenceOptions(new Uri("https://core.example/"), Network.RegTest.GetGenesis().GetHash().ToString(),
                timeout ?? TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromHours(2), TimeSpan.FromMinutes(5));
            Evidence = new AuthenticatedNativeCoreChainEvidence(Binding, Options, Registry, "signer", "password", Core, Clock);
        }

        public void Dispose() { Evidence.Dispose(); Keys.Dispose(); }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) { _now += duration; _ticks += duration.Ticks; }
    }

    private sealed class CoreHandler(Clock clock, string script, string transactionId) : HttpMessageHandler
    {
        public string? Attack { get; set; }
        public string Tip { get; set; } = new('b', 64);
        public string Script { get; set; } = script;
        public bool CheckedAuthentication { get; private set; }
        public bool IncludedMempool { get; private set; }
        public int Requests { get; private set; }
        public int OutputRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            CheckedAuthentication = request.Headers.Authorization?.Scheme == "Basic"
                && request.Headers.Authorization.Parameter == Convert.ToBase64String(Encoding.UTF8.GetBytes("signer:password"));
            if (Attack == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (Attack == "authentication") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (Attack == "redirect") return new HttpResponseMessage(HttpStatusCode.Redirect)
            { Headers = { Location = new Uri("https://other.example/") } };
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            var id = root.GetProperty("id").GetInt64();
            var method = root.GetProperty("method").GetString();
            object? result;
            if (method == "getblockhash")
            {
                if (Attack == "tip-changes-during-start") Tip = new string('c', 64);
                result = Attack == "genesis" ? new string('d', 64) : Network.RegTest.GetGenesis().GetHash().ToString();
            }
            else if (method == "getblockchaininfo")
            {
                var values = new Dictionary<string, object>
                {
                    ["chain"] = Attack == "network" ? "main" : "regtest",
                    ["blocks"] = 200L,
                    ["headers"] = Attack == "headers-ahead" ? 201L : 200L,
                    ["initialblockdownload"] = Attack == "initial-download",
                    ["verificationprogress"] = Attack == "verification-progress" ? 0.5m : 1m,
                    ["bestblockhash"] = Tip,
                    ["time"] = (clock.GetUtcNow() + (Attack == "stale-tip" ? TimeSpan.FromHours(-3)
                        : Attack == "future-tip" ? TimeSpan.FromHours(1) : TimeSpan.Zero)).ToUnixTimeSeconds()
                };
                if (Attack == "missing-tip-time") values.Remove("time");
                result = values;
            }
            else
            {
                Assert.Equal("gettxout", method);
                OutputRequests++;
                var parameters = root.GetProperty("params");
                Assert.Equal(transactionId, parameters[0].GetString());
                Assert.Equal(3u, parameters[1].GetUInt32());
                IncludedMempool = parameters[2].GetBoolean();
                result = Attack == "spent" ? null : new
                {
                    bestblock = Attack == "output-tip" ? new string('c', 64) : Tip,
                    confirmations = Attack == "negative-confirmations" ? -1 : Attack == "excess-confirmations" ? 300
                        : Attack == "immature-coinbase" ? 99 : Attack == "unconfirmed" ? 0 : 100,
                    coinbase = Attack is "immature-coinbase" or "mature-coinbase",
                    value = Attack == "negative-amount" ? -0.00001m : Attack == "fractional-satoshi" ? 0.000000001m
                        : Attack == "excess-amount" ? 21_000_001m : 0.00001m,
                    scriptPubKey = new { hex = Attack == "empty-script" ? "" : Attack == "invalid-script" ? "xz" : Script }
                };
            }
            var response = JsonSerializer.Serialize(new
            { id = Attack == "wrong-response-id" ? id + 1 : id, error = Attack == "rpc-error" ? new { code = -1 } : null, result });
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Attack == "oversized-answer" ? new string('x', 70_000) : response, Encoding.UTF8, "application/json") };
        }
    }
}