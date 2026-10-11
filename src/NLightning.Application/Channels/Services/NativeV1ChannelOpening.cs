using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Acceptance;
using NLightning.Domain.Channels.Interfaces;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Client.Requests;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Persistence.Interfaces;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Messages;
using NLightning.Domain.Protocol.ValueObjects;
using NLightning.Domain.Serialization.Interfaces;
using NLightning.Domain.Signing.Recovery;

namespace NLightning.Application.Channels.Services;

/// <summary>Persists native v1 opening inputs before allocation and retains their exact public signer receipt.</summary>
public sealed class NativeV1ChannelOpening(
    IChannelFactory factory, ILightningSigner signer, IServiceScopeFactory scopes,
    IChannelIdFactory ids, IMessageSerializer messages, IFeeService fees, IOptions<NodeOptions> nodeOptions,
    IRemoteSigningWorkflowCoordinator? workflows = null)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _peerGates = new();
    public bool Enabled => workflows is INativeChannelKeyAllocationRecovery;

    public async Task<ChannelModel> CreateOutboundAsync(OpenChannelClientRequest request,
        FeatureOptions features, CompactPubKey peer)
    {
        RequireEnabled();
        var gate = _peerGates.GetOrAdd(peer.ToString(), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            var opening = JsonSerializer.SerializeToUtf8Bytes(OutboundRequest.From(request, peer));
            var pending = await FindOutboundAsync(peer);
            SigningWorkflowDescriptor descriptor;
            OpeningIntent intent;
            if (pending is not null)
            {
                intent = ReadIntent(pending);
                if (!intent.Opening.AsSpan().SequenceEqual(opening))
                    throw Blocked("The peer has an unresolved opening with different saved inputs.");
                descriptor = Descriptor(pending);
            }
            else
            {
                var effectiveFee = request.FeeRatePerKw?.MilliSatoshi
                    ?? LightningMoney.Satoshis(nodeOptions.Value.GetCommitmentFeeRatePerKw(
                        (await fees.GetFeeRatePerKwAsync()).Satoshi)).MilliSatoshi;
                var configuration = Configuration();
                var context = Context(configuration, effectiveFee);
                var preflightRequest = OutboundRequest.From(request, peer).ToRequest();
                preflightRequest.FeeRatePerKw = new LightningMoney(effectiveFee);
                await AllocatedFactory().PrepareV1AsInitiatorAsync(preflightRequest, features, context);
                intent = new OpeningIntent("outbound", peer.ToString(), opening,
                    JsonSerializer.SerializeToUtf8Bytes(features), configuration, effectiveFee, null);
                descriptor = Descriptor(ids.CreateTemporaryChannelId(), intent);
            }
            using var scope = await workflows!.BeginAsync(descriptor);
            scope.Activate();
            var allocation = ((INativeChannelKeyAllocationRecovery)workflows).AllocateChannelKeys(scope, signer);
            await RequireNoStartedOpeningAsync(descriptor.ChannelId);
            var savedRequest = JsonSerializer.Deserialize<OutboundRequest>(intent.Opening)
                ?? throw Blocked("Opening request is missing.");
            var effectiveRequest = savedRequest.ToRequest();
            effectiveRequest.FeeRatePerKw = new LightningMoney(intent.EffectiveFeeMsat
                ?? throw Blocked("Opening fee is missing."));
            features = ReadFeatures(intent);
            return await AllocatedFactory().CreateAllocatedV1AsInitiatorAsync(effectiveRequest, features, peer,
                descriptor.ChannelId, allocation, Context(intent));
        }
        finally { gate.Release(); }
    }

    public async Task<ChannelModel> CreateInboundAsync(OpenChannel1Message message,
        FeatureOptions features, CompactPubKey peer, ChannelOpenDecision? decision)
    {
        RequireEnabled();
        var prior = await FindInboundAsync(message, peer);
        OpeningIntent intent;
        SigningWorkflowDescriptor descriptor;
        if (prior is not null)
        {
            intent = ReadIntent(prior);
            features = ReadFeatures(intent);
            decision = ReadDecision(intent);
            descriptor = Descriptor(prior);
        }
        else
        {
            var configuration = Configuration();
            var feeQuote = (await fees.GetFeeRatePerKwAsync()).MilliSatoshi;
            var prepared = await AllocatedFactory().PrepareV1AsNonInitiatorAsync(message, features,
                Context(configuration, feeQuote));
            ValidateDecision(message, peer, features, decision, prepared);
            intent = new OpeningIntent("inbound", peer.ToString(), await EncodeMessageAsync(message),
                JsonSerializer.SerializeToUtf8Bytes(features), configuration, feeQuote, DecisionBytes(decision));
            descriptor = Descriptor(message.Payload.ChannelId, intent);
        }
        using var scope = await workflows!.BeginAsync(descriptor);
        scope.Activate();
        var allocation = ((INativeChannelKeyAllocationRecovery)workflows).AllocateChannelKeys(scope, signer);
        await RequireNoStartedOpeningAsync(message.Payload.ChannelId);
        var channel = await AllocatedFactory().CreateAllocatedV1AsNonInitiatorAsync(message, features, peer, allocation,
            Context(intent));
        if (decision is not null)
        {
            var error = ChannelOpenDecisionRules.TryApply(decision, Handlers.OpenChannel1MessageHandler.ToOpenRequest(message, peer),
                channel.ChannelParams.Local, channel.ChannelParams.MinimumDepth, message.Payload.FundingAmount,
                out var local, out var minimumDepth, features);
            if (error is not null) throw Blocked("The saved channel acceptance cannot apply: " + error);
            channel.ApplyOpenDecision(local, minimumDepth);
        }
        return channel;
    }

    /// <summary>Returns the original acceptance before a restarted handler consults a current external decider.</summary>
    public async Task<InboundOpeningReplay?> TryRestoreInboundAsync(OpenChannel1Message message, CompactPubKey peer)
    {
        if (!Enabled) return null;
        var prior = await FindInboundAsync(message, peer);
        if (prior is null) return null;
        var intent = ReadIntent(prior);
        return new InboundOpeningReplay(ReadFeatures(intent), ReadDecision(intent));
    }

    public async Task<ISigningWorkflowScope?> BeginInitialCommitAsync(ChannelModel channel, ChannelId temporaryId)
    {
        if (!Enabled) return null;
        var snapshot = SigningWorkflowSnapshot.CreateOpening(channel, SigningWorkflowKind.Opening);
        var intent = JsonSerializer.SerializeToUtf8Bytes(new
        {
            TemporaryId = temporaryId.ToString(),
            channel.LocalKeySet.KeyIndex,
            Snapshot = Convert.ToHexString(snapshot.SnapshotFingerprint)
        });
        return await workflows!.BeginAsync(new SigningWorkflowDescriptor(channel.ChannelId, SigningWorkflowKind.Opening,
            0, 0, SHA256.HashData(intent))
        { PublicationIntent = intent });
    }

    /// <summary>Reconciles the original allocation before its public keys are committed with their real channel.</summary>
    public async Task StageConsumeAsync(ChannelModel channel, ChannelId temporaryId, IUnitOfWork unitOfWork)
    {
        if (!Enabled) return;
        var pending = await workflows!.GetPendingAsync(temporaryId);
        var allocationWorkflow = pending.SingleOrDefault(x => x.Kind == SigningWorkflowKind.ChannelKeyAllocation)
            ?? throw Blocked("The opening lost its durable key allocation intent.");
        var intent = ReadIntent(allocationWorkflow);
        if (intent.Peer != channel.RemoteNodeId.ToString())
            throw Blocked("The allocation belongs to another opening peer.");
        using var scope = await workflows.BeginAsync(Descriptor(allocationWorkflow));
        scope.Activate();
        var allocation = ((INativeChannelKeyAllocationRecovery)workflows).AllocateChannelKeys(scope, signer);
        var keys = channel.LocalKeySet;
        if (allocation.KeyIndex != keys.KeyIndex
         || allocation.Basepoints.FundingPubKey != keys.FundingCompactPubKey
         || allocation.Basepoints.RevocationBasepoint != keys.RevocationCompactBasepoint
         || allocation.Basepoints.PaymentBasepoint != keys.PaymentCompactBasepoint
         || allocation.Basepoints.DelayedPaymentBasepoint != keys.DelayedPaymentCompactBasepoint
         || allocation.Basepoints.HtlcBasepoint != keys.HtlcCompactBasepoint
         || allocation.FirstPerCommitmentPoint != keys.CurrentPerCommitmentCompactPoint)
            throw Blocked("The channel keys differ from their original allocation receipt.");
        await scope.StageConsumeAsync(unitOfWork);
    }

    /// <summary>Finds pre-channel intents after restart, including blocked allocations that must retain their identity.</summary>
    public async Task<IReadOnlyList<SigningWorkflow>> GetUnconsumedAsync()
    {
        RequireEnabled();
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .SigningWorkflowDbRepository.GetUnconsumedAsync(SigningWorkflowKind.ChannelKeyAllocation);
    }

    public async Task<OpenChannel1Message> ReadInboundMessageAsync(SigningWorkflow workflow)
    {
        var intent = ReadIntent(workflow);
        if (intent.Direction != "inbound") throw Blocked("This is not an inbound opening intent.");
        using var stream = new MemoryStream(intent.Opening, writable: false);
        return await messages.DeserializeMessageAsync<OpenChannel1Message>(stream)
            ?? throw Blocked("The saved inbound opening message is invalid.");
    }

    public OpenChannelClientRequest ReadOutboundRequest(SigningWorkflow workflow)
    {
        var intent = ReadIntent(workflow);
        if (intent.Direction != "outbound") throw Blocked("This is not an outbound opening intent.");
        return (JsonSerializer.Deserialize<OutboundRequest>(intent.Opening)
            ?? throw Blocked("The saved outbound opening request is invalid.")).ToRequest();
    }

    private async Task<SigningWorkflow?> FindOutboundAsync(CompactPubKey peer)
    {
        var matches = (await GetUnconsumedAsync()).Where(workflow =>
        {
            var intent = ReadIntent(workflow);
            return intent.Direction == "outbound" && intent.Peer == peer.ToString();
        }).ToArray();
        if (matches.Length > 1) throw Blocked("The peer has multiple unresolved opening intents.");
        return matches.SingleOrDefault();
    }

    private byte[] Configuration() => JsonSerializer.SerializeToUtf8Bytes(OpeningDefaults.From(nodeOptions.Value));
    private static byte[]? DecisionBytes(ChannelOpenDecision? decision) => decision is null ? null
        : JsonSerializer.SerializeToUtf8Bytes(DecisionSnapshot.From(decision));
    private static ChannelOpenDecision? ReadDecision(OpeningIntent intent) => intent.Decision is null ? null
        : (JsonSerializer.Deserialize<DecisionSnapshot>(intent.Decision)
            ?? throw Blocked("The saved acceptance is invalid.")).ToDecision();
    private static FeatureOptions ReadFeatures(OpeningIntent intent) =>
        JsonSerializer.Deserialize<FeatureOptions>(intent.Features) ?? throw Blocked("The saved features are invalid.");
    private static V1OpeningContext Context(OpeningIntent intent) => Context(intent.Configuration,
        intent.EffectiveFeeMsat ?? throw Blocked("The saved opening fee is missing."));
    private static V1OpeningContext Context(byte[] configuration, ulong feeMsat) => new(
        (JsonSerializer.Deserialize<OpeningDefaults>(configuration)
            ?? throw Blocked("The saved opening defaults are invalid.")).ToOptions(), new LightningMoney(feeMsat));

    private async Task<SigningWorkflow?> FindInboundAsync(OpenChannel1Message message, CompactPubKey peer)
    {
        var pending = (await workflows!.GetPendingAsync(message.Payload.ChannelId))
            .SingleOrDefault(x => x.Kind == SigningWorkflowKind.ChannelKeyAllocation);
        if (pending is null) return null;
        var intent = ReadIntent(pending);
        var encoded = await EncodeMessageAsync(message);
        if (intent.Direction != "inbound" || intent.Peer != peer.ToString()
         || !intent.Opening.AsSpan().SequenceEqual(encoded))
            throw Blocked("The peer changed its original saved opening request.");
        return pending;
    }
    private async Task<byte[]> EncodeMessageAsync(OpenChannel1Message message)
    {
        using var stream = new MemoryStream();
        await messages.SerializeAsync(message, stream);
        return stream.ToArray();
    }
    private static void ValidateDecision(OpenChannel1Message message, CompactPubKey peer, FeatureOptions features,
        ChannelOpenDecision? decision, V1OpeningPreparation prepared)
    {
        if (decision is null) return;
        var error = ChannelOpenDecisionRules.TryApply(decision, Handlers.OpenChannel1MessageHandler.ToOpenRequest(message, peer),
            prepared.Parameters.Local, prepared.Parameters.MinimumDepth, message.Payload.FundingAmount,
            out _, out _, features);
        if (error is not null) throw new ChannelErrorException("The channel acceptance cannot apply: " + error,
            message.Payload.ChannelId, ChannelOpenDecision.GenericRejection);
    }

    private static SigningWorkflowDescriptor Descriptor(ChannelId id, OpeningIntent intent)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(intent);
        return new SigningWorkflowDescriptor(id, SigningWorkflowKind.ChannelKeyAllocation, 0, 0,
            SHA256.HashData(bytes))
        { PublicationIntent = bytes };
    }

    private async Task RequireNoStartedOpeningAsync(ChannelId temporaryId)
    {
        using var scope = scopes.CreateScope();
        var pending = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .SigningWorkflowDbRepository.GetUnconsumedAsync(SigningWorkflowKind.Opening);
        foreach (var workflow in pending)
        {
            if (workflow.PublicationIntent is null) continue;
            using var intent = JsonDocument.Parse(workflow.PublicationIntent);
            if (intent.RootElement.TryGetProperty("TemporaryId", out var id) && id.GetString() == temporaryId.ToString())
                throw Blocked("This opening already began commitment-zero signing; resume its saved funding intent before renegotiating.");
        }
    }
    private static SigningWorkflowDescriptor Descriptor(SigningWorkflow workflow) => new(workflow.ChannelId,
        workflow.Kind, workflow.ExpectedLocalCommitmentNumber, workflow.ExpectedRemoteCommitmentNumber,
        workflow.SnapshotFingerprint.ToArray())
    { PublicationIntent = workflow.PublicationIntent?.ToArray() };
    private static OpeningIntent ReadIntent(SigningWorkflow workflow)
    {
        if (workflow.Kind != SigningWorkflowKind.ChannelKeyAllocation || workflow.PublicationIntent is null
         || !SHA256.HashData(workflow.PublicationIntent).AsSpan().SequenceEqual(workflow.SnapshotFingerprint))
            throw Blocked("The allocation lost its immutable opening intent.");
        return JsonSerializer.Deserialize<OpeningIntent>(workflow.PublicationIntent)
            ?? throw Blocked("The allocation opening intent is invalid.");
    }
    private IAllocatedV1ChannelFactory AllocatedFactory() => factory as IAllocatedV1ChannelFactory
        ?? throw Blocked("Native allocation recovery requires a factory accepting committed public keys.");
    private void RequireEnabled()
    {
        if (!Enabled) throw Blocked("Native allocation recovery is unavailable.");
    }
    private static InvalidOperationException Blocked(string reason) => new(reason);

    public sealed record InboundOpeningReplay(FeatureOptions Features, ChannelOpenDecision? Decision);

    private sealed record OpeningIntent(string Direction, string Peer, byte[] Opening, byte[] Features,
        byte[] Configuration, ulong? EffectiveFeeMsat, byte[]? Decision);

    private sealed record OpeningDefaults(string Network, uint MinimumDepth, ulong MinimumChannelMsat,
        ulong DustLimitMsat, ulong HtlcMinimumMsat, ushort MaxAcceptedHtlcs, ushort ToSelfDelay,
        uint MinCommitmentFeeRatePerKw, uint InFlightPercent, bool LimitInFlightOnSpliceableChannels,
        uint MaxAcceptedChannelReservePercent, uint MinAcceptedMaxHtlcValueInFlightPercent,
        ushort MaxAcceptedToSelfDelay, byte[] Features)
    {
        public static OpeningDefaults From(NodeOptions options) => new(options.BitcoinNetwork.Name,
            options.MinimumDepth, options.MinimumChannelSize.MilliSatoshi, options.DustLimitAmount.MilliSatoshi,
            options.HtlcMinimumAmount.MilliSatoshi, options.MaxAcceptedHtlcs, options.ToSelfDelay,
            options.MinCommitmentFeeRatePerKw, options.AllowUpToPercentageOfChannelFundsInFlight,
            options.LimitInFlightOnSpliceableChannels, options.MaxAcceptedChannelReservePercent,
            options.MinAcceptedMaxHtlcValueInFlightPercent, options.MaxAcceptedToSelfDelay,
            JsonSerializer.SerializeToUtf8Bytes(options.Features));
        public NodeOptions ToOptions() => new()
        {
            BitcoinNetwork = BitcoinNetwork.Resolve(Network),
            MinimumDepth = MinimumDepth,
            MinimumChannelSize = new LightningMoney(MinimumChannelMsat),
            DustLimitAmount = new LightningMoney(DustLimitMsat),
            HtlcMinimumAmount = new LightningMoney(HtlcMinimumMsat),
            MaxAcceptedHtlcs = MaxAcceptedHtlcs,
            ToSelfDelay = ToSelfDelay,
            MinCommitmentFeeRatePerKw = MinCommitmentFeeRatePerKw,
            AllowUpToPercentageOfChannelFundsInFlight = InFlightPercent,
            LimitInFlightOnSpliceableChannels = LimitInFlightOnSpliceableChannels,
            MaxAcceptedChannelReservePercent = MaxAcceptedChannelReservePercent,
            MinAcceptedMaxHtlcValueInFlightPercent = MinAcceptedMaxHtlcValueInFlightPercent,
            MaxAcceptedToSelfDelay = MaxAcceptedToSelfDelay,
            Features = JsonSerializer.Deserialize<FeatureOptions>(Features) ?? throw Blocked("Opening defaults lost their features.")
        };
    }

    private sealed record DecisionSnapshot(bool Accept, string? Error, byte[]? UpfrontShutdownScript,
        ushort? ToSelfDelay, ulong? ChannelReserveMsat, ulong? MaxHtlcValueInFlightMsat,
        ushort? MaxAcceptedHtlcs, ulong? HtlcMinimumMsat, uint? MinimumDepth, bool ZeroConf)
    {
        public static DecisionSnapshot From(ChannelOpenDecision decision) => new(decision.Accept, decision.Error,
            decision.UpfrontShutdownScript is { } script ? ((byte[])script).ToArray() : null, decision.ToSelfDelay,
            decision.ChannelReserve?.MilliSatoshi, decision.MaxHtlcValueInFlight?.MilliSatoshi,
            decision.MaxAcceptedHtlcs, decision.HtlcMinimum?.MilliSatoshi, decision.MinimumDepth, decision.ZeroConf);
        public ChannelOpenDecision ToDecision() => new()
        {
            Accept = Accept,
            Error = Error,
            UpfrontShutdownScript = UpfrontShutdownScript is { } bytes ? new BitcoinScript(bytes) : (BitcoinScript?)null,
            ToSelfDelay = ToSelfDelay,
            ChannelReserve = Money(ChannelReserveMsat),
            MaxHtlcValueInFlight = Money(MaxHtlcValueInFlightMsat),
            MaxAcceptedHtlcs = MaxAcceptedHtlcs,
            HtlcMinimum = Money(HtlcMinimumMsat),
            MinimumDepth = MinimumDepth,
            ZeroConf = ZeroConf
        };
        private static LightningMoney? Money(ulong? value) => value is { } amount ? new LightningMoney(amount) : null;
    }

    private sealed record OutboundRequest(string Peer, ulong FundingMsat, ulong? HtlcMinimumMsat,
        ulong? MaxHtlcValueInFlightMsat, ulong? ChannelReserveMsat, ushort? MaxAcceptedHtlcs, ulong? DustLimitMsat,
        ulong? PushMsat, ushort? ToSelfDelay, ulong? FeeMsat, bool ZeroConf, bool Public, bool DualFund,
        bool ForceV1, bool SimpleTaproot, string? Label, string[] Tags, ulong? RequestInboundSat, ulong? MaxLiquidityFeeSat)
    {
        public static OutboundRequest From(OpenChannelClientRequest request, CompactPubKey peer) => new(peer.ToString(),
            request.FundingAmount.MilliSatoshi, request.HtlcMinimumAmount?.MilliSatoshi,
            request.MaxHtlcValueInFlight?.MilliSatoshi, request.ChannelReserveAmount?.MilliSatoshi,
            request.MaxAcceptedHtlcs, request.DustLimitAmount?.MilliSatoshi, request.PushAmount?.MilliSatoshi,
            request.ToSelfDelay, request.FeeRatePerKw?.MilliSatoshi, request.IsZeroConfChannel, request.IsPublic,
            request.IsDualFunded, request.ForceV1, request.IsSimpleTaproot, request.Label, request.Tags.ToArray(),
            request.RequestInboundSat, request.MaxLiquidityFeeSat);
        public OpenChannelClientRequest ToRequest() => new(Peer, new LightningMoney(FundingMsat))
        {
            HtlcMinimumAmount = Money(HtlcMinimumMsat),
            MaxHtlcValueInFlight = Money(MaxHtlcValueInFlightMsat),
            ChannelReserveAmount = Money(ChannelReserveMsat),
            MaxAcceptedHtlcs = MaxAcceptedHtlcs,
            DustLimitAmount = Money(DustLimitMsat),
            PushAmount = Money(PushMsat),
            ToSelfDelay = ToSelfDelay,
            FeeRatePerKw = Money(FeeMsat),
            IsZeroConfChannel = ZeroConf,
            IsPublic = Public,
            IsDualFunded = DualFund,
            ForceV1 = ForceV1,
            IsSimpleTaproot = SimpleTaproot,
            Label = Label,
            Tags = Tags.ToArray(),
            RequestInboundSat = RequestInboundSat,
            MaxLiquidityFeeSat = MaxLiquidityFeeSat
        };
        private static LightningMoney? Money(ulong? amount) => amount is { } value ? new LightningMoney(value) : null;
    }
}