namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The financial projector until A3-T4 registers its own: off, projects nothing, and serializes
/// <see cref="RunExclusiveAsync{T}"/> on a gate of its own.
/// </summary>
public sealed class NullFinancialBooksProjector : IFinancialBooksProjector, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public Task<int> ProjectAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);

    /// <inheritdoc />
    public async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> action,
                                              CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}