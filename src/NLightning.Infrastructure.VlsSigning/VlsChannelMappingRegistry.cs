using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Factories;
using NLightning.Domain.Channels.Commitments;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Persistence.Interfaces;
using NLightning.Domain.Signing.Recovery;
using NLightning.Domain.Signing.Vls;

namespace NLightning.Infrastructure.VlsSigning;

/// <summary>Single-process allocator. Database uniqueness rejects competing owners before keys reach the protocol.</summary>
public sealed class VlsChannelMappingRegistry(VlsSignerConnection connection, IServiceScopeFactory scopes)
{
    private readonly object _allocationGate = new();

    public (uint KeyIndex, JsonElement Result) Allocate(CompactPubKey peer)
    {
        lock (_allocationGate)
        {
            var mapping = Run(async uow =>
            {
                var prior = await uow.VlsChannelMappingDbRepository.GetAllAsync();
                foreach (var existing in prior) ValidateIdentity(existing);
                var index = prior.Count == 0 ? 0 : checked(prior.Max(m => m.KeyIndex) + 1);
                var dbid = checked((ulong)index + 1);
                var id = Guid.NewGuid();
                var envelope = VlsSignerConnection.EncodeRequest(id,
                    new JsonObject { ["op"] = "allocate", ["peer"] = peer.ToString(), ["dbid"] = dbid });
                var ticks = DateTime.UtcNow.Ticks;
                var reserved = new VlsChannelMapping(index, dbid, peer, null,
                    ((byte[])connection.Identity.NodePublicKey).ToArray(), connection.Identity.Network,
                    id, envelope, null, null, ticks, ticks);
                await uow.VlsChannelMappingDbRepository.AddAsync(reserved);
                await uow.SaveChangesAsync();
                return reserved;
            });
            return (mapping.KeyIndex, RecoverAllocation(mapping));
        }
    }

    public VlsChannelMapping GetByIndex(uint index)
    {
        var mapping = Run(uow => uow.VlsChannelMappingDbRepository.GetByKeyIndexAsync(index))
                   ?? throw new KeyNotFoundException("Unknown VLS channel key index.");
        ValidateIdentity(mapping);
        RecoverAllocation(mapping);
        return Run(uow => uow.VlsChannelMappingDbRepository.GetByKeyIndexAsync(index))!;
    }

    public VlsChannelMapping GetByChannelId(ChannelId channelId)
    {
        var mapping = Run(uow => uow.VlsChannelMappingDbRepository.GetByChannelIdAsync(channelId))
                   ?? throw new KeyNotFoundException("BOLT channel has no durable VLS allocation.");
        if (mapping.AllocationResponse is null || mapping.VlsChannelId is null)
            throw new InvalidOperationException("A bound BOLT channel has an incomplete VLS allocation.");
        ValidateIdentity(mapping);
        RecoverAllocation(mapping);
        return mapping;
    }

    /// <summary>Startup validation reads a completed receipt without allocating or advancing signer policy.</summary>
    public void ValidateChannel(ChannelModel channel)
    {
        ValidateChannelProfile(channel);
        var mapping = GetByChannelId(channel.ChannelId);
        if (mapping.KeyIndex != channel.LocalKeySet.KeyIndex || mapping.PeerId != channel.RemoteNodeId
         || mapping.AllocationResponse is null)
            throw new InvalidOperationException("Persisted channel does not match its durable VLS allocation.");
        using var parsed = JsonDocument.Parse(mapping.AllocationResponse);
        var keys = parsed.RootElement;
        if (keys.GetProperty("funding").GetString() != channel.LocalKeySet.FundingCompactPubKey.ToString()
         || keys.GetProperty("revocation").GetString() != channel.LocalKeySet.RevocationCompactBasepoint.ToString()
         || keys.GetProperty("payment").GetString() != channel.LocalKeySet.PaymentCompactBasepoint.ToString()
         || keys.GetProperty("delay").GetString() != channel.LocalKeySet.DelayedPaymentCompactBasepoint.ToString()
         || keys.GetProperty("htlc").GetString() != channel.LocalKeySet.HtlcCompactBasepoint.ToString())
            throw new InvalidOperationException("Persisted channel basepoints differ from its durable VLS allocation.");
    }

    internal static void ValidateChannelProfile(ChannelModel channel)
    {
        if (channel.Version != ChannelVersion.V1 || channel.ChannelParams.OptionAnchorOutputs
         || channel.ChannelParams.OptionSimpleTaproot || channel.ChannelParams.HasInferredParams
         || channel.ChannelParams.AnnounceChannel || channel.ChannelParams.MinimumDepth == 0
         || channel.LocalFundingKeyIndex != 0 || channel.FundingKeysUnknown)
            throw new InvalidOperationException("Persisted channel is outside the VLS single-funded static-remotekey prototype.");
        if (channel.ChannelParams.Local.HtlcMinimumAmount < LightningMoney.Satoshis(1_000)
         || channel.Commitments is { } commitments
         && (commitments.Params.MaxDustHtlcExposureMsat != 0 || commitments.Params.OptionAnchors
          || commitments.Params.OptionSimpleTaproot || commitments.PendingFundings.Count != 0))
            throw new InvalidOperationException("Persisted VLS channel requires zero dust exposure and a minimum HTLC of 1,000 satoshis.");
        if (channel.Commitments is null && channel.State is ChannelState.Open or ChannelState.ShuttingDown)
            throw new InvalidOperationException("An active VLS channel requires a persisted commitment and dust policy.");
        if (channel.LocalBalance.MilliSatoshi % 1_000 != 0 || channel.RemoteBalance.MilliSatoshi % 1_000 != 0)
            throw new InvalidOperationException("Persisted VLS channel balances must be whole-satoshi amounts.");
        if (channel.Commitments is not { } state) return;
        if (state.Htlcs.Values.Any(htlc => htlc.AmountMsat % 1_000 != 0)
         || HasFractionalAmounts(state.LocalCommit.Spec) || HasFractionalAmounts(state.RemoteCommit.Spec)
         || state.RemoteNextCommit is { } unacked && HasFractionalAmounts(unacked.Commit.Spec))
            throw new InvalidOperationException("Persisted VLS channel commitments and HTLCs must use whole-satoshi amounts.");
        foreach (var side in new[] { CommitmentSide.Local, CommitmentSide.Remote })
        {
            var dust = state.Params.Holder(side).DustLimitSatoshis;
            var feerate = Math.Max(state.LatestFeeratePerKw, state.FeeratePerKw(side));
            // This is the Domain prospective view's public HTLC-selection rule; removed outputs no longer count.
            var prospectiveTrimmed = state.Htlcs.Values.Any(htlc => !HtlcStateTable.IsRemovedFrom(htlc.State, side)
                && CommitmentFeeCalculator.IsHtlcTrimmed(htlc.AmountMsat, htlc.IsOfferedBy(side), dust,
                                                         feerate, state.Params.Format));
            var committed = state.BuildSpec(side);
            var saved = side == CommitmentSide.Local ? state.LocalCommit.Spec : state.RemoteCommit.Spec;
            if (prospectiveTrimmed
             || CommitmentFeeCalculator.TrimmedHtlcTotalMsat(committed, dust, state.Params.Format) != 0
             || CommitmentFeeCalculator.TrimmedHtlcTotalMsat(saved, dust, state.Params.Format) != 0
             || side == CommitmentSide.Remote && state.RemoteNextCommit is { } next
                && CommitmentFeeCalculator.TrimmedHtlcTotalMsat(next.Commit.Spec, dust, state.Params.Format) != 0)
                throw new InvalidOperationException("Persisted VLS channel contains trimmed HTLCs outside the zero-dust prototype.");
        }
    }

    private static bool HasFractionalAmounts(CommitmentSpec spec) =>
        spec.LocalMsat % 1_000 != 0 || spec.RemoteMsat % 1_000 != 0
     || spec.Htlcs.Any(htlc => htlc.AmountMsat % 1_000 != 0);

    public void Bind(ChannelId channelId, uint keyIndex)
    {
        GetByIndex(keyIndex);
        Run(async uow =>
        {
            await uow.VlsChannelMappingDbRepository.BindChannelAsync(keyIndex, channelId);
            await uow.SaveChangesAsync();
            return true;
        });
    }

    private JsonElement RecoverAllocation(VlsChannelMapping mapping)
    {
        var status = connection.ReconcileEnvelope(mapping.AllocationEnvelope);
        byte[] response;
        if (mapping.AllocationResponse is { } completed)
        {
            if (status.Outcome != RemoteSigningRequestOutcome.Completed || status.Response is null
             || !status.Response.AsSpan().SequenceEqual(completed))
                throw new InvalidOperationException("VLS lost or changed its completed channel allocation receipt.");
            response = completed;
        }
        else if (status.Outcome == RemoteSigningRequestOutcome.Completed && status.Response is not null)
            response = status.Response;
        else if (status.Outcome == RemoteSigningRequestOutcome.NotFound)
            response = connection.ExecuteEnvelope(mapping.AllocationEnvelope);
        else throw new InvalidOperationException("VLS channel allocation has an uncertain receipt outcome.");
        using var parsed = JsonDocument.Parse(response);
        var vlsId = Convert.FromHexString(parsed.RootElement.GetProperty("channel").GetString()!);
        if (vlsId.Length != 41 || !vlsId.AsSpan(0, 33).SequenceEqual((byte[])mapping.PeerId)
         || System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(vlsId.AsSpan(33)) != mapping.DbId
         || mapping.VlsChannelId is { } existingId && !existingId.AsSpan().SequenceEqual(vlsId))
            throw new InvalidOperationException("VLS allocation reply changed the persisted peer or database identity.");
        if (mapping.AllocationResponse is null)
            Run(async uow =>
            {
                await uow.VlsChannelMappingDbRepository.CompleteAllocationAsync(mapping.KeyIndex, response, vlsId);
                await uow.SaveChangesAsync();
                return true;
            });
        return parsed.RootElement.Clone();
    }

    private void ValidateIdentity(VlsChannelMapping mapping)
    {
        using var parsed = JsonDocument.Parse(mapping.AllocationEnvelope);
        var command = parsed.RootElement.GetProperty("command");
        if (!Guid.TryParseExact(parsed.RootElement.GetProperty("id").GetString(), "N", out var id)
         || id != mapping.AllocationRequestId || command.GetProperty("op").GetString() != "allocate"
         || command.GetProperty("peer").GetString() != mapping.PeerId.ToString()
         || command.GetProperty("dbid").GetUInt64() != mapping.DbId
         || parsed.RootElement.TryGetProperty("token", out _))
            throw new InvalidOperationException("VLS allocation envelope changed its immutable identity.");
        if (!mapping.SignerIdentity.AsSpan().SequenceEqual((byte[])connection.Identity.NodePublicKey)
         || !string.Equals(mapping.Network, connection.Identity.Network, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("VLS channel mapping belongs to another signer or network.");
    }

    private T Run<T>(Func<IUnitOfWork, Task<T>> action) => Task.Run(async () =>
    {
        using var scope = scopes.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }).GetAwaiter().GetResult();
}