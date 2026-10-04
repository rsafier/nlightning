using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace NLightning.Application.Tests.Payments.Send.Harness;

using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Money;

/// <summary>
/// The node's <see cref="IChannelOperations"/> with <see cref="IChannelOperations.OfferHtlcAsync"/> refusable by a test
/// (<see cref="PaymentHarnessNode.OfferRefusal"/>): a non-null exception is thrown as the engine would throw it, before
/// anything is added; every other call goes to the production service.
/// </summary>
[ExcludeFromCodeCoverage]
public class OfferRefusingChannelOperations : DispatchProxy
{
    private IChannelOperations _inner = null!;
    private PaymentHarnessNode _node = null!;

    internal static IChannelOperations Create(IChannelOperations inner, PaymentHarnessNode node)
    {
        var proxy = Create<IChannelOperations, OfferRefusingChannelOperations>();
        var self = (OfferRefusingChannelOperations)(object)proxy;
        self._inner = inner;
        self._node = node;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(IChannelOperations.OfferHtlcAsync)
         && _node.OfferRefusal?.Invoke((ChannelId)args![0]!, (LightningMoney)args[1]!) is { } refusal)
            return Task.FromException<ulong>(refusal);

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }
}