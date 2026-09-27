namespace NLightning.Application.Tests.Offers.Send;

using Application.Offers.Send;
using Domain.Money;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Models;
using Domain.Protocol.Constants;
using static OfferSendTestData;

/// <summary>
/// BOLT 12 "Invoices" reader (B12-INV-03/04/05) as the payer applies it to the invoice answering its request.
/// </summary>
public class InvoiceVerifierTests
{
    private readonly TestOfferIssuer _issuer = new();
    private readonly TestBolt12Signer _payer = new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    [Fact]
    public void Given_AValidInvoice_When_Verifying_Then_ItsFieldsAndPathsAreReturned()
    {
        // Arrange
        var (offer, request) = Request();
        var paths = new[] { PaymentPath(Key(0x10)), PaymentPath(Key(0x11)) };
        var bytes = _issuer.CreateInvoice(request.Bytes, PaymentHash, paths, 10_000, Now, 600,
                                          features: [0x02, 0x00, 0x00]);

        // Act
        var ok = Verify(bytes, request, offer, out var verified, out var reason);

        // Assert
        Assert.True(ok, reason);
        Assert.Equal(_issuer.NodeId, verified!.Invoice.NodeId);
        Assert.Equal(LightningMoney.MilliSatoshis(10_000), verified.Invoice.Amount);
        Assert.Equal(PaymentHash, verified.Invoice.PaymentHash);
        Assert.Equal(600u, verified.Invoice.RelativeExpirySeconds);
        Assert.Equal(Now, verified.Invoice.CreatedAt);
        Assert.Equal(2, verified.Invoice.PathCount);
        Assert.Equal(2, verified.Paths.Count);
        Assert.Equal(Key(0x11), verified.Paths[1].Path.FirstNodeId);
        Assert.Equal(paths[0].PayInfo, verified.Paths[0].PayInfo with { Features = paths[0].PayInfo.Features });
        Assert.True(verified.AllowsMpp);
        Assert.Equal(request.Bytes, verified.Invoice.InvoiceRequestBytes.ToArray());
    }

    [Fact]
    public void Given_NoMppBit_When_Verifying_Then_NoSplitAllowed()
    {
        // Arrange
        var (offer, request) = Request();
        var bytes = _issuer.CreateInvoice(request.Bytes, PaymentHash, [PaymentPath(Key(0x10))], 10_000, Now);

        // Act
        Assert.True(Verify(bytes, request, offer, out var verified, out var reason), reason);

        // Assert
        Assert.False(verified!.AllowsMpp);
        Assert.Equal(Bolt12Constants.DefaultInvoiceRelativeExpirySeconds, verified.Invoice.RelativeExpirySeconds);
    }

    [Fact]
    public void Given_AnOfferWithPathsOnly_When_TheNodeIdIsTheFinalBlindedIdWeSentTo_Then_Accepted()
    {
        // Arrange
        var recipient = _issuer.NodeId;
        var sentTo = MessagePath(Key(0x05), recipient);
        var offer = OfferToPay.Parse(_issuer.CreateOffer(chain: Chain, withIssuerId: false, paths: [sentTo]), Chain, Now);
        var request = InvoiceRequestFactory.Create(offer, new PayOfferRequest(offer.Text), Chain, _payer);
        var bytes = _issuer.CreateInvoice(request.Bytes, PaymentHash, [PaymentPath(Key(0x10))], 10_000, Now);

        // Act / Assert
        Assert.True(InvoiceVerifier.TryVerify(bytes, request, offer, sentTo, Chain, Now, _payer, n => n.NodeId,
                                              out _, out var reason), reason);
        Assert.False(InvoiceVerifier.TryVerify(bytes, request, offer, MessagePath(Key(0x05), Key(0x07)), Chain, Now,
                                               _payer, n => n.NodeId, out _, out reason));
        Assert.StartsWith("B12-INV-04", reason);
    }

    [Theory]
    [InlineData("no amount", "B12-INV-03")]
    [InlineData("no created_at", "B12-INV-03")]
    [InlineData("no payment_hash", "B12-INV-03")]
    [InlineData("no node_id", "B12-INV-03")]
    [InlineData("other chain", "B12-INV-03")]
    [InlineData("unknown even feature", "B12-INV-03")]
    [InlineData("expired", "B12-INV-03")]
    [InlineData("no paths", "B12-INV-03")]
    [InlineData("no blindedpay", "B12-INV-03")]
    [InlineData("payinfo count", "B12-INV-03")]
    [InlineData("only an unknown path feature", "B12-INV-03")]
    [InlineData("changed request field", "B12-INV-04")]
    [InlineData("dropped request field", "B12-INV-04")]
    [InlineData("other node", "B12-INV-04")]
    [InlineData("other amount", "B12-INV-04")]
    [InlineData("bad signature", "B12-INV-04")]
    [InlineData("second signature element", "B12-SIG-03")]
    [InlineData("out of range", "B12-ENC-04")]
    [InlineData("unknown even type", "B12-ENC-03")]
    public void Given_AnInvoiceBreakingAReaderRule_When_Verifying_Then_RejectedWithTheRule(string @case,
        string requirement)
    {
        // Arrange
        var (offer, request) = Request();
        var path = PaymentPath(Key(0x10));
        var otherKey = Enumerable.Repeat((byte)0x55, 32).ToArray();
        byte[] Mutated(Func<List<Bolt12TlvRecord>, List<Bolt12TlvRecord>> mutate) =>
            _issuer.CreateInvoice(request.Bytes, PaymentHash, [path], 10_000, Now, mutate: mutate);
        List<Bolt12TlvRecord> Without(List<Bolt12TlvRecord> records, ulong type) =>
            records.Where(r => r.Type != type).ToList();
        List<Bolt12TlvRecord> Replace(List<Bolt12TlvRecord> records, ulong type, byte[] value) =>
            [.. Without(records, type), new Bolt12TlvRecord(type, value)];
        var bytes = @case switch
        {
            "no amount" => Mutated(r => Without(r, Bolt12TlvTypes.InvoiceAmount)),
            "no created_at" => Mutated(r => Without(r, Bolt12TlvTypes.InvoiceCreatedAt)),
            "no payment_hash" => Mutated(r => Without(r, Bolt12TlvTypes.InvoicePaymentHash)),
            "no node_id" => Mutated(r => Without(r, Bolt12TlvTypes.InvoiceNodeId)),
            "other chain" => Mutated(r => Replace(r, Bolt12TlvTypes.InvreqChain, (byte[])ChainConstants.Testnet)),
            "unknown even feature" => _issuer.CreateInvoice(request.Bytes, PaymentHash, [path], 10_000, Now,
                                                            features: [0x01, 0x00]),
            "expired" => _issuer.CreateInvoice(request.Bytes, PaymentHash, [path], 10_000, Now.AddSeconds(-601), 600),
            "no paths" => Mutated(r => Without(r, Bolt12TlvTypes.InvoicePaths)),
            "no blindedpay" => Mutated(r => Without(r, Bolt12TlvTypes.InvoiceBlindedPay)),
            "payinfo count" => Mutated(r => Replace(r, Bolt12TlvTypes.InvoiceBlindedPay,
                                                   InvoiceVerifier.WritePayInfos([path.PayInfo, path.PayInfo]))),
            "only an unknown path feature" => _issuer.CreateInvoice(request.Bytes, PaymentHash,
                                                                    [PaymentPath(Key(0x10), features: [0x04])], 10_000,
                                                                    Now),
            "changed request field" => Mutated(r => Replace(r, Bolt12TlvTypes.InvreqPayerNote, "other"u8.ToArray())),
            "dropped request field" => Mutated(r => Without(r, Bolt12TlvTypes.OfferMetadata)),
            "other node" => _issuer.CreateInvoice(request.Bytes, PaymentHash, [path], 10_000, Now,
                                                  nodeId: TestBolt12Signer.PubKeyOf(otherKey), signingKey: otherKey),
            "other amount" => _issuer.CreateInvoice(request.Bytes, PaymentHash, [path], 10_001, Now),
            "bad signature" => _issuer.CreateInvoice(request.Bytes, PaymentHash, [path], 10_000, Now,
                                                     signingKey: otherKey),
            "second signature element" => Mutated(r => [.. r, new Bolt12TlvRecord(242, new byte[64])]),
            "out of range" => Mutated(r => [.. r, new Bolt12TlvRecord(4_000_000_001, new byte[] { 1 })]),
            _ => Mutated(r => [.. r, new Bolt12TlvRecord(178, new byte[] { 1 })])
        };

        // Act
        var ok = Verify(bytes, request, offer, out _, out var reason);

        // Assert
        Assert.False(ok);
        Assert.StartsWith(requirement, reason);
    }

    [Fact]
    public void Given_AnOddSignatureRangeElement_When_Verifying_Then_ItIsIgnored()
    {
        // Arrange
        var (offer, request) = Request();
        var bytes = _issuer.CreateInvoice(request.Bytes, PaymentHash, [PaymentPath(Key(0x10))], 10_000, Now,
                                          mutate: r => [.. r, new Bolt12TlvRecord(241, new byte[] { 1, 2, 3 })]);

        // Act
        var ok = Verify(bytes, request, offer, out var verified, out var reason);

        // Assert
        Assert.True(ok, reason);
        Assert.Equal(_issuer.NodeId, verified!.Invoice.NodeId);
    }

    [Fact]
    public void Given_APathWithAnUnknownIntroduction_When_Verifying_Then_OnlyTheOtherPathIsUsed()
    {
        // Arrange
        var (offer, request) = Request();
        var unknown = Key(0x12);
        var bytes = _issuer.CreateInvoice(request.Bytes, PaymentHash, [PaymentPath(unknown), PaymentPath(Key(0x10))],
                                          10_000, Now);

        // Act
        var ok = InvoiceVerifier.TryVerify(bytes, request, offer, null, Chain, Now, _payer,
                                           n => n.NodeId == unknown ? null : n.NodeId, out var verified, out var reason);

        // Assert
        Assert.True(ok, reason);
        Assert.Equal(Key(0x10), Assert.Single(verified!.Paths).Path.FirstNodeId);
        Assert.Equal(2, verified.Invoice.PathCount);
    }

    private (OfferToPay Offer, BuiltInvoiceRequest Request) Request()
    {
        var offer = OfferToPay.Parse(_issuer.CreateOffer(chain: Chain), Chain, Now);
        return (offer, InvoiceRequestFactory.Create(offer, new PayOfferRequest(offer.Text, PayerNote: "hi"), Chain,
                                                    _payer));
    }

    private bool Verify(byte[] bytes, BuiltInvoiceRequest request, OfferToPay offer, out VerifiedInvoice? verified,
                        out string? reason) =>
        InvoiceVerifier.TryVerify(bytes, request, offer, null, Chain, Now, _payer, n => n.NodeId, out verified,
                                  out reason);
}