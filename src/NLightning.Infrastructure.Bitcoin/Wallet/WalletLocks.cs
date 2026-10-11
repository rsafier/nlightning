namespace NLightning.Infrastructure.Bitcoin.Wallet;

/// <summary>
/// The wallet's allocation locks, one instance per node (a singleton of the node's container, NL-1387). The indexes
/// they guard live in the node's own database, so nodes hosted in one process never wait on each other, and one
/// node's hung allocation cannot stall another. Every wait takes the caller's token.
/// </summary>
public sealed class WalletLocks
{
    private readonly SemaphoreSlim _addressGeneration = new(1, 1);
    private readonly SemaphoreSlim _accounts = new(1, 1);

    /// <summary>
    /// Serializes the default wallet's address lookup and batch generation across scopes (NL-283): two scopes that
    /// both find no unused address would otherwise compute the same first index and the second save would hit the
    /// (Index, IsChange, AddressType) key.
    /// </summary>
    public ValueTask<IDisposable> EnterAddressGenerationAsync(CancellationToken cancellationToken) =>
        EnterAsync(_addressGeneration, cancellationToken);

    /// <summary>
    /// Serializes named-account allocation (<see cref="WalletAccountService"/>) with the chain monitor's block commit
    /// that stages account discovery.
    /// </summary>
    public ValueTask<IDisposable> EnterAccountsAsync(CancellationToken cancellationToken) =>
        EnterAsync(_accounts, cancellationToken);

    private static async ValueTask<IDisposable> EnterAsync(SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}