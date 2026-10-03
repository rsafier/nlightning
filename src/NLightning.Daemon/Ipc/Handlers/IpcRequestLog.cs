using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;

/// <summary>
/// How an IPC handler logs a request it could not serve (NL-883): a refused or invalid operator request is one Warning
/// line without a stack trace (the operator already gets the message back, and the exception goes to Debug); a fault of
/// ours is an Error with the exception and its stack, also when a handler wrapped it as a refusal (NL-894,
/// <see cref="IsFault"/>).
/// </summary>
internal static class IpcRequestLog
{
    private const string OurNamespace = "NLightning";

    /// <summary>
    /// Logs a <see cref="ClientException"/>: <see cref="ErrorCodes.ServerError"/>, or any code wrapping a fault
    /// (<see cref="IsFault"/>), is an Error with the stack; every other one a refusal (one Warning line).
    /// </summary>
    public static void LogClientException(ILogger logger, ClientCommand command, ClientException exception)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.ErrorCode == ErrorCodes.ServerError || IsFault(exception.InnerException))
            logger.LogError(exception, "{Command} failed: {Message}", command, exception.Message);
        else
            LogRefused(logger, command, exception.Message, exception.InnerException);
    }

    /// <summary>
    /// Logs a request that ended with <paramref name="exception"/>, caught by type (an
    /// <see cref="InvalidOperationException"/> of unknown origin): a refusal of ours is one Warning line with
    /// <paramref name="reason"/>, a fault (<see cref="IsFault"/>) an Error with the stack.
    /// </summary>
    public static void LogRefusedOrFault(ILogger logger, ClientCommand command, string reason, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);
        if (IsFault(exception))
            logger.LogError(exception, "{Command} failed: {Message}", command, reason);
        else
            LogRefused(logger, command, reason, exception);
    }

    /// <summary>
    /// Logs a refused or invalid operator request as one Warning line, without the exception; the exception, when
    /// given, goes to Debug with its stack, so it can still be recovered.
    /// </summary>
    public static void LogRefused(ILogger logger, ClientCommand command, string reason, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogWarning("{Command} refused: {Message}", command, reason);
        if (exception is not null && logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug(exception, "{Command} refusal: the exception behind it", command);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> (what a handler caught or wrapped) is a fault rather than a refusal (NL-894):
    /// an exception that only a bug raises (a null reference, a disposed object, a bad cast or index, something not
    /// implemented), or one thrown outside our code (LINQ's <c>Single()</c> on an empty sequence, EF Core's "a second
    /// operation was started on this context": both an <see cref="InvalidOperationException"/>). An exception thrown by
    /// our own code (a service's refusal) or of one of our own types is a refusal; null is a refusal.
    /// </summary>
    internal static bool IsFault(Exception? exception)
    {
        if (exception is null)
            return false;

        if (exception is NullReferenceException or ObjectDisposedException or InvalidCastException
                      or IndexOutOfRangeException or NotImplementedException or OutOfMemoryException)
            return true;

        if (exception.GetType().Namespace?.StartsWith(OurNamespace, StringComparison.Ordinal) == true)
            return false;

        // Where it was thrown (an async method's state machine is nested in its class, so it has the class's namespace);
        // unknown when the runtime has no metadata for it (AOT), then taken as a refusal
        var origin = exception.TargetSite?.DeclaringType?.Namespace;
        return origin is not null && !origin.StartsWith(OurNamespace, StringComparison.Ordinal);
    }
}