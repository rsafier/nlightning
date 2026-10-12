namespace NLightning.Daemon.Handlers;

using Domain.Client.Constants;
using Domain.Client.Exceptions;

/// <summary>
/// Argument checks shared by the client command handlers. They throw <see cref="ClientException"/> with
/// <see cref="ErrorCodes.InvalidOperation"/>, which the IPC handlers pass to the client as is.
/// </summary>
internal static class ClientRequestGuards
{
    /// <summary>
    /// The largest page a list command returns.
    /// </summary>
    public const int MaxPageSize = 1_000;

    /// <summary>
    /// Checks a <c>skip</c>/<c>take</c> page request.
    /// </summary>
    public static void ThrowIfInvalidPage(int skip, int take)
    {
        if (skip < 0)
            throw new ClientException(ErrorCodes.InvalidOperation, $"skip must not be negative (was {skip}).");
        if (take is <= 0 or > MaxPageSize)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"take must be between 1 and {MaxPageSize} (was {take}).");
    }
}