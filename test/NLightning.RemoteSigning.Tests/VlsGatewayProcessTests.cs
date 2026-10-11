using NBitcoin;
using NLightning.Bolt11.Models;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.ValueObjects;
using NLightning.Infrastructure.VlsSigning;

namespace NLightning.RemoteSigning.Tests;

/// <summary>Actual Rust gateway transport and receipt acceptance; selected explicitly with a pinned binary.</summary>
public sealed class VlsGatewayProcessTests
{
    private const string Peer = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";

    [Fact(Explicit = true)]
    public async Task Given_RealVls_When_WeRestartAfterAllocation_Then_IdentityKeysAndExactReceiptsSurvive()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var identity = await gateway.ExchangeAsync(new { op = "identity" }, ct: ct);
        var allocation = new { op = "allocate", peer = Peer, dbid = 1UL };
        var result = await gateway.ExchangeAsync(allocation, "channel-allocation", ct: ct);
        Assert.True(result.GetProperty("ok").GetBoolean(), result.ToString());
        Assert.Equal(82, result.GetProperty("result").GetProperty("channel").GetString()!.Length);

        // Act: kill the actual Rust process, then provide the same seed and existing Redb database.
        await gateway.RestartAsync(ct);
        var restoredIdentity = await gateway.ExchangeAsync(new { op = "identity" }, ct: ct);
        var recovered = await gateway.ExchangeAsync(new
        {
            op = "reconcile",
            id = "channel-allocation",
            command = allocation
        }, ct: ct);
        var replayed = await gateway.ExchangeAsync(allocation, "channel-allocation", ct: ct);

        // Assert: recovery does not allocate another channel or accept a changed request.
        Assert.Equal(identity.GetProperty("result").GetRawText(), restoredIdentity.GetProperty("result").GetRawText());
        Assert.Equal("completed", recovered.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(result.GetProperty("result").GetRawText(), recovered.GetProperty("result").GetProperty("result").GetRawText());
        Assert.Equal(result.GetRawText(), replayed.GetRawText());
        var changed = await gateway.ExchangeAsync(new { op = "allocate", peer = Peer, dbid = 2UL },
                                                  "channel-allocation", ct: ct);
        Assert.False(changed.GetProperty("ok").GetBoolean());
        Assert.Contains("payload mismatch", changed.GetProperty("error").GetString());
        var absent = await gateway.ExchangeAsync(new
        {
            op = "reconcile",
            id = "absent-allocation",
            command = allocation
        }, ct: ct);
        Assert.Equal("not_found", absent.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact(Explicit = true)]
    public async Task Given_SeparateCredentials_When_NodeAttemptsApproval_Then_VlsRejectsIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var approval = new { op = "authorize_keysend", payee = Peer, hash = new string('a', 64), amount_msat = 100_000UL };

        // Act and assert: neither socket placement nor the node credential grants approval authority.
        var wrongToken = await gateway.ExchangeAsync(new { op = "identity" }, token: "incorrect", ct: ct);
        Assert.False(wrongToken.GetProperty("ok").GetBoolean());
        Assert.Contains("unauthenticated", wrongToken.GetProperty("error").GetString());
        var wrongSocket = await gateway.ExchangeAsync(approval, ct: ct);
        Assert.False(wrongSocket.GetProperty("ok").GetBoolean());
        Assert.Contains("forbidden", wrongSocket.GetProperty("error").GetString());
        var nodeTokenOnApprovalSocket = await gateway.ExchangeAsync(approval, token: VlsGatewayFixture.NodeToken,
                                                                   approval: true, ct: ct);
        Assert.False(nodeTokenOnApprovalSocket.GetProperty("ok").GetBoolean());
        Assert.Contains("unauthenticated", nodeTokenOnApprovalSocket.GetProperty("error").GetString());
        var accepted = await gateway.ExchangeAsync(approval, "operator-approved-payment", approval: true, ct: ct);
        Assert.True(accepted.GetProperty("ok").GetBoolean(), accepted.ToString());
        await gateway.RestartAsync(ct);
        var recovered = await gateway.ExchangeAsync(new
        {
            op = "reconcile",
            id = "operator-approved-payment",
            command = approval
        }, approval: true, ct: ct);
        Assert.Equal("completed", recovered.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(accepted.GetProperty("result").GetRawText(), recovered.GetProperty("result").GetProperty("result").GetRawText());
    }

    [Fact(Explicit = true)]
    public async Task Given_InvoiceApproval_When_InvoiceIsValidOrInvalid_Then_VlsEnforcesAuthorizationAndPersistsAdmission()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        using var key = new Key();
        var hash = new uint256(new string('3', 64));
        var secret = new uint256(new string('4', 64));
        var valid = new Invoice(LightningMoney.Satoshis(1_000), "VLS approval acceptance", hash, secret,
                                BitcoinNetwork.Regtest);
        var bolt11 = valid.Encode(key);

        // Act and assert: a valid signed invoice is admitted only through the operator socket.
        var command = new { op = "authorize_invoice", invoice = bolt11 };
        var denied = await gateway.ExchangeAsync(command, ct: ct);
        Assert.False(denied.GetProperty("ok").GetBoolean());
        var admitted = await gateway.ExchangeAsync(command, "invoice-approval", approval: true, ct: ct);
        Assert.True(admitted.GetProperty("ok").GetBoolean(), admitted.ToString());
        Assert.True(admitted.GetProperty("result").GetProperty("added").GetBoolean());
        await gateway.RestartAsync(ct);
        var recovered = await gateway.ExchangeAsync(new
        {
            op = "reconcile",
            id = "invoice-approval",
            command
        }, approval: true, ct: ct);
        Assert.Equal("completed", recovered.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(admitted.GetProperty("result").GetRawText(), recovered.GetProperty("result").GetProperty("result").GetRawText());

        // Invalid checksum and amountless invoices must never produce an approval receipt.
        var tampered = bolt11[..^1] + (bolt11[^1] == 'q' ? 'p' : 'q');
        var invalid = await gateway.ExchangeAsync(new { op = "authorize_invoice", invoice = tampered },
                                                  "invalid-invoice", approval: true, ct: ct);
        Assert.False(invalid.GetProperty("ok").GetBoolean());
        var amountless = new Invoice(LightningMoney.Zero, "amountless", new uint256(new string('5', 64)),
                                     secret, BitcoinNetwork.Regtest).Encode(key);
        var missingAmount = await gateway.ExchangeAsync(new { op = "authorize_invoice", invoice = amountless },
                                                        "amountless-invoice", approval: true, ct: ct);
        Assert.False(missingAmount.GetProperty("ok").GetBoolean());
        var expiring = new Invoice(LightningMoney.Satoshis(1_000), "expired", new uint256(new string('6', 64)),
                                    secret, BitcoinNetwork.Regtest);
        expiring.ExpiryDate = DateTimeOffset.FromUnixTimeSeconds(expiring.Timestamp);
        var expired = expiring.Encode(key);
        await Task.Delay(TimeSpan.FromMilliseconds(1_100), ct);
        var expiredApproval = await gateway.ExchangeAsync(new { op = "authorize_invoice", invoice = expired },
                                                          "expired-invoice", approval: true, ct: ct);
        Assert.False(expiredApproval.GetProperty("ok").GetBoolean());
    }

    [Fact(Explicit = true)]
    public async Task Given_VlsMode_When_IdentityOrAvailabilityIsWrong_Then_StartupFailsWithoutNativeFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile });
        var keys = new VlsSecureKeyManager(connection);
        Assert.Throws<NotSupportedException>(() => keys.GetNodeKeyPair());
        Assert.Throws<NotSupportedException>(() => keys.GetChannelKeyAtIndex(0));
        Assert.Throws<NotSupportedException>(() => keys.GetDepositP2WpkhKeyAtIndex(0, false));
        Assert.Throws<NotSupportedException>(() => keys.GetDepositP2TrKeyAtIndex(0, false));
        Assert.Throws<InvalidOperationException>(() => new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile, Network = "mainnet" }));
        Assert.Throws<InvalidOperationException>(() => new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile, ExpectedNodePublicKey = Peer }));
        await gateway.StopAsync();
        Assert.ThrowsAny<Exception>(() => new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile }));
    }

}