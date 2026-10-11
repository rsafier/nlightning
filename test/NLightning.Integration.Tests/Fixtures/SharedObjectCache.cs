using System.Collections.Concurrent;

namespace NLightning.Integration.Tests.Fixtures;

/// <summary>
/// Objects a fixture shares between the tests of its collection, created once per key (see
/// <see cref="LightningRegtestNetworkFixture.GetOrCreateAsync{T}"/>).
/// </summary>
public sealed class SharedObjectCache
{
    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _shared = new();

    /// <summary>
    /// Returns the object stored under <paramref name="key"/>, creating it once with <paramref name="factory"/>. A
    /// failed creation is not cached, so the next caller tries again.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The object stored under <paramref name="key"/> is not a <typeparamref name="T"/> (the key is already used for
    /// another type). The stored object stays.
    /// </exception>
    public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        var lazy = _shared.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await factory()));
        object value;
        try
        {
            value = await lazy.Value;
        }
        catch
        {
            _shared.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(key, lazy));
            throw;
        }

        return value as T
            ?? throw new InvalidOperationException(
                   $"The shared object '{key}' is a {value.GetType().Name}, not a {typeof(T).Name}; use another key");
    }

    /// <summary>
    /// Disposes every object that was created (<see cref="IAsyncDisposable"/> first, then
    /// <see cref="IDisposable"/>) and empties the cache. A failing disposal is logged and does not stop the others.
    /// </summary>
    public void DisposeAll()
    {
        foreach (var lazy in _shared.Values)
        {
            try
            {
                if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully)
                    continue;

                switch (lazy.Value.Result)
                {
                    case IAsyncDisposable asyncDisposable:
                        asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Failed to dispose a shared test object: {e.Message}");
            }
        }

        _shared.Clear();
    }
}