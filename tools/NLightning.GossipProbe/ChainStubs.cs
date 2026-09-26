using System.Reflection;

namespace NLightning.GossipProbe;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Enums;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Models;
using Domain.Money;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The chain as the probe sees it: no bitcoind. <see cref="IBlockchainMonitor"/> is a proxy that reports a fixed tip
/// (for the sync's <c>query_channel_range(0, tip + 1)</c>), raises no block, and throws on anything that would publish a
/// transaction (the probe never has one to publish).
/// </summary>
public class ChainMonitorStub : DispatchProxy
{
    private uint _tip;

    /// <summary>Creates the monitor proxy with <paramref name="tip"/> as its last processed block.</summary>
    public static IBlockchainMonitor Create(uint tip)
    {
        var proxy = Create<IBlockchainMonitor, ChainMonitorStub>();
        ((ChainMonitorStub)(object)proxy)._tip = tip;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        var name = targetMethod.Name;
        if (name == "get_LastProcessedBlockHeight")
            return _tip;
        if (name.Contains("Publish", StringComparison.Ordinal) || name.Contains("Broadcast", StringComparison.Ordinal))
            throw new InvalidOperationException($"The gossip probe has no chain: {name} must never be called");

        var returnType = targetMethod.ReturnType;
        if (returnType == typeof(void))
            return null;
        if (returnType == typeof(Task))
            return Task.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType);
            return fromResult.Invoke(null, [resultType.IsValueType ? Activator.CreateInstance(resultType) : null]);
        }

        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}

/// <summary>
/// The funding output lookup of a node without bitcoind: every lookup is <see cref="FundingOutputStatus.BlockUnavailable"/>
/// and counted. With <c>Gossip:AssumeChannelValid</c> the ingress and the pruner must never call it (the probe reports
/// the count).
/// </summary>
public sealed class CountingFundingOutputLookup : IFundingOutputLookup
{
    private long _lookups;

    /// <summary>Lookups asked for so far.</summary>
    public long Lookups => Interlocked.Read(ref _lookups);

    public Task<FundingOutputLookupResult> LookupAsync(ShortChannelId shortChannelId,
                                                       CancellationToken cancellationToken = default) =>
        Count();

    public Task<FundingOutputLookupResult> VerifyAsync(ShortChannelId shortChannelId, CompactPubKey bitcoinKey1,
                                                       CompactPubKey bitcoinKey2,
                                                       LightningMoney? expectedAmount = null,
                                                       CancellationToken cancellationToken = default) =>
        Count();

    public void InvalidateFrom(uint height)
    {
    }

    private Task<FundingOutputLookupResult> Count()
    {
        Interlocked.Increment(ref _lookups);
        return Task.FromResult(FundingOutputLookupResult.Failed(FundingOutputStatus.BlockUnavailable));
    }
}