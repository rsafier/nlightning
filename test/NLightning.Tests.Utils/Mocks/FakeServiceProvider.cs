using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Tests.Utils.Mocks;

/// <summary>
/// A dictionary-backed <see cref="IServiceProvider"/> for unit tests.
/// </summary>
/// <remarks>
/// Like the real provider, <see cref="GetService"/> returns null for a type that was not added (NL-249), so
/// <c>GetService&lt;T&gt;()</c> sees "not registered" and <c>GetRequiredService&lt;T&gt;()</c> throws
/// <see cref="InvalidOperationException"/>. Set <see cref="Strict"/> to throw <see cref="KeyNotFoundException"/> instead,
/// to find out what a test resolves. <see cref="IServiceScopeFactory"/> (scopes share this provider) and
/// <see cref="IServiceProvider"/> itself are always resolvable.
/// </remarks>
public class FakeServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = [];

    public FakeServiceProvider()
    {
        _services.Add(typeof(IServiceScopeFactory), new FakeServiceScopeFactory(this));
    }

    /// <summary>
    /// Throw <see cref="KeyNotFoundException"/> for a type that was not added instead of returning null.
    /// </summary>
    public bool Strict { get; init; }

    public object? GetService(Type serviceType)
    {
        if (_services.TryGetValue(serviceType, out var service))
            return service;

        if (serviceType == typeof(IServiceProvider))
            return this;

        return Strict
                   ? throw new KeyNotFoundException($"{serviceType.FullName} was not added to the FakeServiceProvider.")
                   : null;
    }

    public void AddService<T>(T serviceType, object service)
    {
        _services.Add(serviceType as Type ?? throw new Exception("Send Interface for type"), service);
    }
}