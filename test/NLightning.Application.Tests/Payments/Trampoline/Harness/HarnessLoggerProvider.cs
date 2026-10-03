using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Tests.Payments.Trampoline.Harness;

/// <summary>
/// Writes a harness node's log lines, prefixed with the node's name, to the running test's output (shown for a failed
/// test, or live with <c>-showLiveOutput</c>). Turned on per harness by <see cref="TrampolineHarnessOptions.LogLevel"/>.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class HarnessLoggerProvider(string nodeName, LogLevel minimumLevel) : ILoggerProvider
{
    /// <summary>Framework categories (EF Core's queries...) log from <see cref="LogLevel.Warning"/> only.</summary>
    public ILogger CreateLogger(string categoryName) =>
        new HarnessLogger(nodeName, categoryName[(categoryName.LastIndexOf('.') + 1)..],
                          categoryName.StartsWith("Microsoft.", StringComparison.Ordinal)
                              ? (LogLevel)Math.Max((int)minimumLevel, (int)LogLevel.Warning)
                              : minimumLevel);

    public void Dispose()
    {
    }

    private sealed class HarnessLogger(string nodeName, string category, LogLevel minimumLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var line = $"[{nodeName}] {logLevel.ToString()[..4]} {category}: {formatter(state, exception)}";
            if (exception is not null)
                line += $" ({exception.GetType().Name}: {exception.Message})";
            try
            {
                TestContext.Current.TestOutputHelper?.WriteLine(line);
            }
            catch (InvalidOperationException)
            {
                // The test is over: a background task logged after it ended
            }
        }
    }
}