using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NLightning.GossipProbe;

/// <summary>
/// Writes every log line to <c>probe.log</c> in the run directory (and warnings and errors to the console), and counts
/// the warnings and errors per category for the samples.
/// </summary>
public sealed class ProbeLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();
    private readonly LogLevel _minimumLevel;
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    public ProbeLoggerProvider(string path, LogLevel minimumLevel)
    {
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
        _minimumLevel = minimumLevel;
    }

    /// <summary>Warnings logged so far.</summary>
    public long Warnings => _counts.Where(c => c.Key.StartsWith("Warning|", StringComparison.Ordinal)).Sum(c => c.Value);

    /// <summary>Errors and critical lines logged so far.</summary>
    public long Errors =>
        _counts.Where(c => c.Key.StartsWith("Error|", StringComparison.Ordinal)
                        || c.Key.StartsWith("Critical|", StringComparison.Ordinal)).Sum(c => c.Value);

    /// <summary>Warning/error counts by "level|category".</summary>
    public IReadOnlyDictionary<string, long> Counts => new Dictionary<string, long>(_counts);

    public ILogger CreateLogger(string categoryName) => new ProbeLogger(this, categoryName);

    public void Write(string line)
    {
        lock (_lock)
            _writer.WriteLine(line);
    }

    public void Dispose()
    {
        lock (_lock)
            _writer.Dispose();
    }

    /// <summary>The exception chain, one "Type: message" per level.</summary>
    private static string Describe(Exception? exception)
    {
        var text = string.Empty;
        for (var e = exception; e is not null; e = e.InnerException)
            text += $" | {e.GetType().Name}: {e.Message}";
        return text;
    }

    private sealed class ProbeLogger(ProbeLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            if (logLevel >= LogLevel.Warning)
                provider._counts.AddOrUpdate($"{logLevel}|{category}", 1, (_, v) => v + 1);

            var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{logLevel}] {category}: {formatter(state, exception)}"
                     + Describe(exception);
            provider.Write(line);
            if (logLevel >= LogLevel.Warning)
                Console.WriteLine(line.Length > 400 ? line[..400] + "..." : line);
        }
    }
}