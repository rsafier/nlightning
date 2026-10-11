using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Tests.Utils.Mocks;

using Domain.Node.Fencing;

/// <summary>
/// A node write fence for tests (NL-1341): it records every check and refuses (throws
/// <see cref="NodeFencedException"/>) while <see cref="Refuse"/> is set. <see cref="OnSave"/> runs inside the save's
/// transaction before the verdict, to look at what the save wrote.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class FakeNodeWriteFence : INodeWriteFence
{
    private readonly List<NodeEffect> _effects = [];
    private int _saves;

    /// <summary>Whether every check throws <see cref="NodeFencedException"/>.</summary>
    public bool Refuse { get; set; }

    /// <summary>Called on every save check, with the save's connection and transaction, before the verdict.</summary>
    public Action<DbConnection, DbTransaction>? OnSave { get; set; }

    /// <summary>The save checks so far.</summary>
    public int SaveChecks => Volatile.Read(ref _saves);

    /// <summary>The effect checks so far, in order.</summary>
    public IReadOnlyList<NodeEffect> Effects
    {
        get
        {
            lock (_effects)
                return _effects.ToList();
        }
    }

    public ValueTask CheckSaveAsync(DbConnection connection, DbTransaction transaction,
                                    CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _saves);
        OnSave?.Invoke(connection, transaction);
        if (Refuse)
            throw new NodeFencedException("fenced (save)");

        return ValueTask.CompletedTask;
    }

    public ValueTask CheckEffectAsync(NodeEffect effect, CancellationToken cancellationToken)
    {
        lock (_effects)
            _effects.Add(effect);
        if (Refuse)
            throw new NodeFencedException($"fenced ({effect})");

        return ValueTask.CompletedTask;
    }
}