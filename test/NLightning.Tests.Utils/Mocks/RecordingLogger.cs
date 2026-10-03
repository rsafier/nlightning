using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace NLightning.Tests.Utils.Mocks;

/// <summary>A logger that keeps every entry at or above Debug with its level and formatted message.</summary>
[ExcludeFromCodeCoverage]
public sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

    /// <summary>The entries, in order.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries.ToArray();

    /// <summary>The messages logged at <paramref name="level"/>.</summary>
    public IReadOnlyList<string> At(LogLevel level) =>
        _entries.Where(e => e.Level == level).Select(e => e.Message).ToList();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter) =>
        _entries.Enqueue((logLevel, formatter(state, exception)));
}