using System.Data.Common;

namespace NLightning.Domain.Node.Fencing;

/// <summary>Blocking forms of the <see cref="INodeWriteFence"/> checks, for synchronous callers.</summary>
public static class NodeWriteFenceExtensions
{
    /// <summary>Runs <see cref="INodeWriteFence.CheckEffectAsync"/> and waits for it; throws what the fence throws.</summary>
    public static void CheckEffect(this INodeWriteFence fence, NodeEffect effect)
    {
        ArgumentNullException.ThrowIfNull(fence);
        Wait(fence.CheckEffectAsync(effect, CancellationToken.None));
    }

    /// <summary>Runs <see cref="INodeWriteFence.CheckSaveAsync"/> and waits for it; throws what the fence throws.</summary>
    public static void CheckSave(this INodeWriteFence fence, DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(fence);
        Wait(fence.CheckSaveAsync(connection, transaction, CancellationToken.None));
    }

    private static void Wait(ValueTask check)
    {
        if (check.IsCompletedSuccessfully)
            return;

        check.AsTask().GetAwaiter().GetResult();
    }
}