using System.Runtime.ExceptionServices;

namespace NLightning.Integration.Tests.Docker.Abcd;

/// <summary>
/// The outcome of the one attempt to build a shared object, stored in the fixture's cache whether it succeeded or
/// failed. The fixture cache drops a failed creation so the next caller retries; an expensive build that leaves
/// state behind on shared nodes (the ABCD network opens channels on the shared LND nodes and mines blocks) must not
/// be retried by every test, so its failure is stored here and replayed instead.
/// </summary>
/// <typeparam name="T">The built object.</typeparam>
public sealed class OnceOnlyBuild<T> : IAsyncDisposable where T : class, IAsyncDisposable
{
    private readonly T? _value;
    private readonly ExceptionDispatchInfo? _failure;

    private OnceOnlyBuild(T? value, ExceptionDispatchInfo? failure)
    {
        _value = value;
        _failure = failure;
    }

    /// <summary>
    /// The exception of the failed build, or <c>null</c> when it succeeded.
    /// </summary>
    public Exception? Failure => _failure?.SourceException;

    /// <summary>
    /// Runs <paramref name="build"/> once and captures its result or its exception.
    /// </summary>
    public static async Task<OnceOnlyBuild<T>> RunAsync(Func<Task<T>> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        try
        {
            return new OnceOnlyBuild<T>(await build(), null);
        }
        catch (Exception e)
        {
            return new OnceOnlyBuild<T>(null, ExceptionDispatchInfo.Capture(e));
        }
    }

    /// <summary>
    /// The built object.
    /// </summary>
    /// <param name="what">What was built, for the exception message.</param>
    /// <exception cref="InvalidOperationException">The build failed; the original exception is the inner one.</exception>
    public T GetOrThrow(string what) =>
        _value ?? throw new InvalidOperationException(
            $"The {what} was never built: its only build attempt failed (see the output of the first test that "
          + $"needed it). {_failure!.SourceException.GetType().Name}: {_failure.SourceException.Message}",
            _failure.SourceException);

    public ValueTask DisposeAsync() => _value?.DisposeAsync() ?? ValueTask.CompletedTask;
}