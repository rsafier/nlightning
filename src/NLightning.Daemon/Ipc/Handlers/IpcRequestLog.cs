using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;

/// <summary>
/// How an IPC handler logs a request it could not serve (NL-883): a refused or invalid operator request is one Warning
/// line without a stack trace (the operator already gets the message back); a fault of ours is an Error with the
/// exception and its stack.
/// </summary>
internal static class IpcRequestLog
{
    /// <summary>
    /// Logs a <see cref="ClientException"/>: <see cref="ErrorCodes.ServerError"/> is a fault (Error with the stack),
    /// every other code a refusal (one Warning line).
    /// </summary>
    public static void LogClientException(ILogger logger, ClientCommand command, ClientException exception)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.ErrorCode == ErrorCodes.ServerError)
            logger.LogError(exception, "{Command} failed: {Message}", command, exception.Message);
        else
            LogRefused(logger, command, exception.Message);
    }

    /// <summary>
    /// Logs a refused or invalid operator request as one Warning line, without the exception.
    /// </summary>
    public static void LogRefused(ILogger logger, ClientCommand command, string reason)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogWarning("{Command} refused: {Message}", command, reason);
    }
}