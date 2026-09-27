using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Payments.Invoices;

using Channels.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Gossip.Announcements;
using Gossip.Interfaces;

/// <summary>
/// What <see cref="BlindedPathBuilder.BuildAsync"/> makes paths for.
/// </summary>
/// <param name="PaymentPreimage">The invoice's preimage: our hop's <c>path_id</c> is
/// <see cref="BlindedPathId.Compute"/> of it, so only this invoice is paid through the paths.</param>
/// <param name="Amount">The invoice amount (paths whose introduction node cannot send it to us are skipped), or null.
/// </param>
/// <param name="MinFinalCltvExpiryDelta">Our final CLTV delta (the invoice's <c>min_final_cltv_expiry_delta</c>), added
/// to the paths' CLTV delta.</param>
/// <param name="CurrentHeight">Our best block height.</param>
/// <param name="PathLifetimeBlocks">For how many blocks from <paramref name="CurrentHeight"/> the paths may be used
/// (our hop's <c>payment_constraints.max_cltv_expiry</c> is the height plus this plus our final delta).</param>
/// <param name="MaxPaths">The most paths to make.</param>
/// <param name="IncludePrivateChannels">Also use unannounced channels (their peer must still be reachable by the
/// sender, e.g. a public node); by default only announced ones.</param>
public sealed record BlindedPathRequest(
    Secret PaymentPreimage,
    LightningMoney? Amount,
    ushort MinFinalCltvExpiryDelta,
    uint CurrentHeight,
    uint PathLifetimeBlocks = BlindedPathBuilder.DefaultPathLifetimeBlocks,
    int MaxPaths = BlindedPathBuilder.DefaultMaxPaths,
    bool IncludePrivateChannels = false);

/// <summary>
/// Builds blinded paths to this node for our own receive (BOLT 4 "Route Blinding", recipient side; ONION M5): one
/// two-hop path per usable announced channel, its peer the introduction node and our node the blinded recipient.
/// Meant for BOLT 12 invoices and any other place that hands out blinded paths.
/// </summary>
/// <remarks>
/// <para>Candidates: every <c>Open</c> channel that is announced (<see cref="ChannelAnnouncementService.IsAnnounced"/>:
/// its peer is a public node, so a sender can route to it; any channel with
/// <see cref="BlindedPathRequest.IncludePrivateChannels"/>, an unannounced alias channel by its <c>RemoteAlias</c>),
/// whose link is up and whose peer's <c>channel_update</c> we
/// hold and is not disabled, largest peer balance first (only those that can send <see cref="BlindedPathRequest.Amount"/>
/// within the update's HTLC limits).</para>
/// <para>The introduction node's <c>encrypted_data_tlv</c>: <c>short_channel_id</c> of our channel, its
/// <c>payment_relay</c> = the peer's policy towards us (the fee and delta it charges to forward to us), and
/// <c>payment_constraints</c> (<c>max_cltv_expiry</c> = ours plus its delta, <c>htlc_minimum_msat</c> = the peer's
/// minimum). Ours: <c>path_id</c> and <c>payment_constraints</c> (height + lifetime + final delta, 1 msat). Both are
/// padded to the same length (BOLT 4 SHOULD). The pay info aggregates the relay with
/// <see cref="BlindedPayInfo.Aggregate"/> plus our final delta; its HTLC limits are the peer's.</para>
/// <para>No dummy hops: our hop is the last one (a receive through dummy hops after it is not implemented).</para>
/// </remarks>
public sealed class BlindedPathBuilder
{
    /// <summary>The default <see cref="BlindedPathRequest.PathLifetimeBlocks"/>: about two weeks.</summary>
    public const uint DefaultPathLifetimeBlocks = 2016;

    /// <summary>The default <see cref="BlindedPathRequest.MaxPaths"/>.</summary>
    public const int DefaultMaxPaths = 3;

    // encrypted_data_tlv padding (type 1): a record header is one type byte and one length byte
    private const int PaddingHeaderLength = 2;

    private readonly IRouteBlindingService _routeBlindingService;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelUpdateService _channelUpdateService;
    private readonly IPeerLivenessProbe? _peerLivenessProbe;
    private readonly ILogger<BlindedPathBuilder> _logger;

    public BlindedPathBuilder(IRouteBlindingService routeBlindingService, ISecureKeyManager secureKeyManager,
                              IChannelMemoryRepository channelMemoryRepository,
                              IChannelUpdateService channelUpdateService, ILogger<BlindedPathBuilder> logger,
                              IPeerLivenessProbe? peerLivenessProbe = null)
    {
        _routeBlindingService = routeBlindingService;
        _secureKeyManager = secureKeyManager;
        _channelMemoryRepository = channelMemoryRepository;
        _channelUpdateService = channelUpdateService;
        _logger = logger;
        _peerLivenessProbe = peerLivenessProbe;
    }

    /// <summary>
    /// The blinded paths to us for <paramref name="request"/> (empty when no channel qualifies).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">If <see cref="BlindedPathRequest.MaxPaths"/> is below 1 or the
    /// lifetime is 0.</exception>
    public async Task<IReadOnlyList<BlindedPaymentPath>> BuildAsync(BlindedPathRequest request,
                                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxPaths, 1, nameof(request));
        ArgumentOutOfRangeException.ThrowIfZero(request.PathLifetimeBlocks, nameof(request));

        var ourNodeId = _secureKeyManager.GetNodePubKey();
        var ourMaxCltvExpiry = checked(request.CurrentHeight + request.PathLifetimeBlocks
                                     + request.MinFinalCltvExpiryDelta);
        var candidates = new List<(ulong Spendable, BlindedPaymentPath Path)>();
        foreach (var channel in _channelMemoryRepository.FindChannels(c => c.State == ChannelState.Open))
        {
            var announced = ChannelAnnouncementService.IsAnnounced(channel);
            if (!announced && !request.IncludePrivateChannels)
                continue;

            // The peer resolves the short_channel_id among its own: our alias of the peer for an alias channel
            var shortChannelId = !announced && channel.ChannelParams.UseScidAlias > FeatureSupport.No
                                     ? channel.RemoteAlias ?? default
                                     : channel.ShortChannelId;
            if (shortChannelId == default)
                continue;

            if (!_channelUpdateService.TryGetRemoteChannelUpdate(channel.ChannelId, out var update)
             || update is null || update.IsDisabled)
                continue;

            var spendable = InvoiceService.GetPeerSpendable(channel);
            if (request.Amount is { } amount
             && (spendable < amount.MilliSatoshi || amount.MilliSatoshi > update.HtlcMaximumMsat))
                continue;

            if (_peerLivenessProbe is not null
             && !await _peerLivenessProbe.IsAliveAsync(channel.ChannelId, channel.RemoteNodeId, cancellationToken))
                continue;

            var relay = new BlindedPaymentRelay(update.CltvExpiryDelta, update.FeeProportionalMillionths,
                                                update.FeeBaseMsat);
            var introductionData = new BlindedRecipientData
            {
                ShortChannelId = shortChannelId,
                PaymentRelay = relay,
                PaymentConstraints = new BlindedPaymentConstraints(checked(ourMaxCltvExpiry + relay.CltvExpiryDelta),
                                                                   Math.Max(update.HtlcMinimumMsat, 1))
            };
            var ourData = new BlindedRecipientData
            {
                PathId = BlindedPathId.Compute(request.PaymentPreimage),
                PaymentConstraints = new BlindedPaymentConstraints(ourMaxCltvExpiry, 1)
            };

            var encoded = Pad([_routeBlindingService.EncodeRecipientData(introductionData),
                               _routeBlindingService.EncodeRecipientData(ourData)],
                              [introductionData, ourData]);
            var sessionKey = CreateSessionKey();
            BlindedPath path;
            try
            {
                path = _routeBlindingService.CreateBlindedPath([channel.RemoteNodeId, ourNodeId], encoded,
                                                               new PrivKey(sessionKey));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sessionKey);
            }

            var (feeBase, feeProportional, cltvDelta) = BlindedPayInfo.Aggregate([relay],
                                                                                 request.MinFinalCltvExpiryDelta);
            var payInfo = new BlindedPayInfo(feeBase, feeProportional, cltvDelta,
                                             introductionData.PaymentConstraints.HtlcMinimumMsat,
                                             update.HtlcMaximumMsat);
            candidates.Add((spendable, new BlindedPaymentPath(path, payInfo)));
        }

        var paths = candidates.OrderByDescending(c => c.Spendable)
                              .Take(request.MaxPaths)
                              .Select(c => c.Path)
                              .ToList();
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Built {Count} blinded path(s) to us through {Introductions}", paths.Count,
                             string.Join(", ", paths.Select(p => p.Path.FirstNodeId.ToString())));
        return paths;
    }

    /// <summary>
    /// Pads every <c>encrypted_data_tlv</c> to the same length with a <c>padding</c> record (BOLT 4: the recipient
    /// SHOULD, so the payloads do not reveal the hops' positions).
    /// </summary>
    private List<byte[]> Pad(byte[][] encoded, BlindedRecipientData[] data)
    {
        var target = encoded.Max(e => e.Length) + PaddingHeaderLength;
        var padded = new List<byte[]>(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            var withPadding = new BlindedRecipientData
            {
                Padding = new byte[target - encoded[i].Length - PaddingHeaderLength],
                ShortChannelId = data[i].ShortChannelId,
                NextNodeId = data[i].NextNodeId,
                PathId = data[i].PathId,
                NextPathKeyOverride = data[i].NextPathKeyOverride,
                PaymentRelay = data[i].PaymentRelay,
                PaymentConstraints = data[i].PaymentConstraints,
                AllowedFeatures = data[i].AllowedFeatures,
                UnknownOddRecords = data[i].UnknownOddRecords
            };
            padded.Add(_routeBlindingService.EncodeRecipientData(withPadding));
        }

        return padded;
    }

    private static byte[] CreateSessionKey()
    {
        // Reuse the onion's scalar rule: 32 CSPRNG bytes, drawn again until a valid secp256k1 private key
        return Routing.PaymentOnionFactory.CreateSessionKey();
    }
}