using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin.Secp256k1;

namespace NLightning.Application.Tests.Offers.Receive;

using Application.Offers.Receive;
using Domain.Crypto.ValueObjects;
using Domain.Node.Interfaces;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Tlv;
using Payments;

/// <summary>
/// BIP-340 over NBitcoin's secp256k1 for the offer tests: BOLT 12 tagged hashes
/// <c>H(tag, root) = SHA256(SHA256(tag) || SHA256(tag) || root)</c>, signed with a given key.
/// </summary>
internal static class Bip340
{
    public static byte[] TaggedHash(string tag, ReadOnlySpan<byte> message)
    {
        var tagHash = System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(tag));
        return System.Security.Cryptography.SHA256.HashData([.. tagHash, .. tagHash, .. message]);
    }

    public static byte[] Sign(byte[] privateKey, string tag, Hash merkleRoot)
    {
        if (!ECPrivKey.TryCreate(privateKey, out var key))
            throw new ArgumentException("Not a private key.", nameof(privateKey));

        var signature = key.SignBIP340(TaggedHash(tag, merkleRoot));
        var bytes = new byte[64];
        signature.WriteToSpan(bytes);
        return bytes;
    }

    public static bool Verify(string tag, Hash merkleRoot, CompactPubKey signerId, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64 || !SecpSchnorrSignature.TryCreate(signature, out var sig)
         || !ECPubKey.TryCreate((ReadOnlySpan<byte>)signerId, null, out _, out var pubKey))
            return false;

        return pubKey.ToXOnlyPubKey().SigVerifyBIP340(sig, TaggedHash(tag, merkleRoot));
    }

    public static CompactPubKey PublicKey(byte[] privateKey)
    {
        if (!ECPrivKey.TryCreate(privateKey, out var key))
            throw new ArgumentException("Not a private key.", nameof(privateKey));

        var bytes = new byte[33];
        key.CreatePubKey().WriteToSpan(true, bytes, out _);
        return new CompactPubKey(bytes);
    }
}

/// <summary>
/// An <see cref="IBolt12Signer"/> with a real node key (BIP-340), standing in for lane B12-B's signer.
/// </summary>
internal sealed class TestBolt12Signer(byte[] nodePrivateKey) : IBolt12Signer
{
    public int NodeSignatures { get; private set; }

    public bool Verify(string tag, Hash merkleRoot, CompactPubKey signerId, ReadOnlyMemory<byte> signature) =>
        Bip340.Verify(tag, merkleRoot, signerId, signature.Span);

    public byte[] SignAsNode(string tag, Hash merkleRoot)
    {
        NodeSignatures++;
        return Bip340.Sign(nodePrivateKey, tag, merkleRoot);
    }

    public CompactPubKey DerivePayerId(ReadOnlyMemory<byte> invoiceRequestMetadata) =>
        throw new NotSupportedException();

    public byte[] SignAsPayer(ReadOnlyMemory<byte> invoiceRequestMetadata, string tag, Hash merkleRoot) =>
        throw new NotSupportedException();

    public byte[] SignAsBlindedRecipient(CompactPubKey pathKey, string tag, Hash merkleRoot) =>
        throw new NotSupportedException();
}

/// <summary>
/// An <see cref="IOfferDbRepository"/> that commits immediately, with the invoice counts read from an invoice store.
/// </summary>
internal sealed class InMemoryOfferDbRepository(InMemoryInvoiceDbRepository invoices) : IOfferDbRepository
{
    private readonly Dictionary<Hash, OfferModel> _offers = [];

    public IReadOnlyCollection<OfferModel> Offers => _offers.Values;

    public int? UnpaidOverride { get; set; }

    public Task AddAsync(OfferModel offer)
    {
        if (!_offers.TryAdd(offer.OfferId, offer))
            throw new InvalidOperationException("Duplicate offer id.");

        return Task.CompletedTask;
    }

    public Task UpdateAsync(OfferModel offer)
    {
        _offers[offer.OfferId] = offer;
        return Task.CompletedTask;
    }

    public Task<OfferModel?> GetByIdAsync(Hash offerId) => Task.FromResult(_offers.GetValueOrDefault(offerId));

    public Task<OfferModel?> GetByOfferBytesAsync(ReadOnlyMemory<byte> offerBytes) =>
        Task.FromResult(_offers.Values.FirstOrDefault(o => o.OfferBytes.Span.SequenceEqual(offerBytes.Span)));

    public Task<IReadOnlyList<OfferModel>> ListAsync(bool activeOnly, int skip, int take) =>
        Task.FromResult<IReadOnlyList<OfferModel>>(_offers.Values
                                                          .Where(o => !activeOnly
                                                                   || o.Status == Domain.Offers.Enums.OfferStatus
                                                                                       .Active)
                                                          .OrderByDescending(o => o.CreatedAt)
                                                          .Skip(skip).Take(take).ToList());

    public Task<OfferInvoiceCounts> GetInvoiceCountsAsync(Hash offerId, DateTimeOffset now)
    {
        var ofOffer = invoices.Invoices.Where(i => i.Bolt12?.OfferId == offerId).ToList();
        return Task.FromResult(new OfferInvoiceCounts(
                                   ofOffer.Count(i => i.Status == Domain.Payments.Enums.InvoiceStatus.Settled),
                                   UnpaidOverride ?? ofOffer.Count(i => i.Status == Domain.Payments.Enums
                                                                           .InvoiceStatus.Open && !i.IsExpired(now))));
    }

    public Task<int> CountUnpaidInvoicesAsync(DateTimeOffset now) =>
        Task.FromResult(UnpaidOverride ?? invoices.Invoices.Count(i => i.Bolt12 is not null
                                                                    && i.Status == Domain.Payments.Enums.InvoiceStatus
                                                                                         .Open
                                                                    && !i.IsExpired(now)));
}

/// <summary>
/// The persistence of the offer tests: one <see cref="IUnitOfWork"/> per scope over shared in-memory stores, with the
/// saves counted and an event per save (to check what was stored before a reply went out).
/// </summary>
internal sealed class OfferTestStore : IDisposable
{
    public OfferTestStore()
    {
        Invoices = new InMemoryInvoiceDbRepository();
        Offers = new InMemoryOfferDbRepository(Invoices);
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.SetupGet(u => u.OfferDbRepository).Returns(Offers);
            unitOfWork.SetupGet(u => u.InvoiceDbRepository).Returns(Invoices);
            unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
            {
                Saves++;
                OnSave?.Invoke();
                return Task.CompletedTask;
            });
            return unitOfWork.Object;
        });
        Provider = services.BuildServiceProvider();
    }

    public InMemoryInvoiceDbRepository Invoices { get; }

    public InMemoryOfferDbRepository Offers { get; }

    public ServiceProvider Provider { get; }

    public IServiceScopeFactory ScopeFactory => Provider.GetRequiredService<IServiceScopeFactory>();

    public int Saves { get; private set; }

    public Action? OnSave { get; set; }

    public void Dispose() => Provider.Dispose();
}

/// <summary>
/// An <see cref="IOnionMessageService"/> that records what is sent.
/// </summary>
internal sealed class RecordingOnionMessageService : IOnionMessageService
{
    public List<(OnionMessageDestination Destination, OnionMessageContents Contents)> Sent { get; } = [];

    public bool IsAvailable { get; set; } = true;

    public Action? OnSend { get; set; }

    public void HandleIncoming(IPeerService peer, OnionMessageMessage message) => throw new NotSupportedException();

    public Task<OnionMessageSendResult> SendAsync(OnionMessageDestination destination, OnionMessageContents contents,
                                                  BlindedPath? replyPath,
                                                  CancellationToken cancellationToken = default)
    {
        OnSend?.Invoke();
        Sent.Add((destination, contents));
        return Task.FromResult(new OnionMessageSendResult(OnionMessageSendStatus.Sent));
    }

    public Task<OnionMessageSendResult> SendAndWaitForReplyAsync(OnionMessageDestination destination,
                                                                 OnionMessageContents contents,
                                                                 IReadOnlyCollection<ulong> expectedReplyTypes,
                                                                 TimeSpan timeout,
                                                                 CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>
/// A clock that moves only when told, for both the wall clock and the timestamps.
/// </summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    private long _ticks = 1_000_000;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _ticks += by.Ticks;
    }
}

/// <summary>
/// Builds signed invoice_requests the way a BOLT 12 payer does (copy the offer, add the invreq fields, sign with the
/// payer key).
/// </summary>
internal sealed class InvoiceRequestBuilder
{
    public static readonly byte[] PayerKey = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private readonly List<Bolt12TlvRecord> _records;

    public InvoiceRequestBuilder(ReadOnlyMemory<byte> offerBytes)
    {
        if (!Bolt12Wire.TryParse(offerBytes, out var offer))
            throw new ArgumentException("Not an offer.", nameof(offerBytes));

        _records = [.. offer!.Records];
        Set(Bolt12TlvTypes.InvreqMetadata, RandomNumberGenerator.GetBytes(32));
        Set(Bolt12TlvTypes.InvreqPayerId, (byte[])Bip340.PublicKey(PayerKey));
    }

    public InvoiceRequestBuilder Set(ulong type, ReadOnlyMemory<byte> value)
    {
        _records.RemoveAll(r => r.Type == type);
        _records.Add(new Bolt12TlvRecord(type, value));
        _records.Sort((a, b) => a.Type.CompareTo(b.Type));
        return this;
    }

    public InvoiceRequestBuilder Remove(ulong type)
    {
        _records.RemoveAll(r => r.Type == type);
        return this;
    }

    public InvoiceRequestBuilder Chain(ReadOnlyMemory<byte> chainHash) => Set(Bolt12TlvTypes.InvreqChain, chainHash);

    public InvoiceRequestBuilder Amount(ulong msat) =>
        Set(Bolt12TlvTypes.InvreqAmount, TruncatedInt.EncodeTu64(msat));

    public InvoiceRequestBuilder Quantity(ulong quantity) =>
        Set(Bolt12TlvTypes.InvreqQuantity, TruncatedInt.EncodeTu64(quantity));

    public InvoiceRequestBuilder PayerNote(string note) =>
        Set(Bolt12TlvTypes.InvreqPayerNote, Encoding.UTF8.GetBytes(note));

    public IReadOnlyList<Bolt12TlvRecord> Records => _records;

    /// <summary>The request signed by <paramref name="payerKey"/> (default <see cref="PayerKey"/>).</summary>
    public byte[] Build(byte[]? payerKey = null)
    {
        var records = _records.Where(r => !Bolt12Wire.IsSignatureType(r.Type)).ToList();
        var root = Bolt12Wire.ComputeMerkleRoot(records);
        records.Add(new Bolt12TlvRecord(Bolt12TlvTypes.Signature,
                                        Bip340.Sign(payerKey ?? PayerKey,
                                                    Bolt12Constants.InvoiceRequestSignatureTag, root)));
        records.Sort((a, b) => a.Type.CompareTo(b.Type));
        return Bolt12Wire.Encode(records);
    }
}

/// <summary>
/// Blinded paths for tests: shapes only (the codec does not check points against the curve).
/// </summary>
internal static class TestPaths
{
    public static CompactPubKey Point(byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }

    public static WireBlindedPath ReplyPath() =>
        new(SciddirOrPubkey.FromNodeId(Point(0x11)), Point(0x12),
            [new BlindedPathHop(Point(0x13), new byte[20]), new BlindedPathHop(Point(0x14), new byte[30])]);

    public static BlindedPaymentPath PaymentPath(byte fill = 0x21) =>
        new(new BlindedPath(Point(fill), Point((byte)(fill + 1)),
                            [new BlindedPathHop(Point((byte)(fill + 2)), new byte[40]),
                             new BlindedPathHop(Point((byte)(fill + 3)), new byte[40])]),
            new BlindedPayInfo(1_000, 100, 184, 1, 5_000_000_000));
}