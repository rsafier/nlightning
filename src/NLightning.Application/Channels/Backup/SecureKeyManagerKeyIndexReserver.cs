using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Channels.Backup;

using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Managers;
using Interfaces;

/// <summary>
/// <see cref="IChannelKeyIndexReserver"/> over <see cref="ISecureKeyManager"/>: the index is advanced with
/// <see cref="ISecureKeyManager.GetNextChannelKey"/> (the only way the port offers; one index is always used up, which
/// is harmless), then the key file is written and awaited when the manager is the file-backed
/// <see cref="SecureKeyManager"/>.
/// </summary>
/// <remarks>
/// <see cref="ISecureKeyManager.GetNextChannelKey"/> also starts its own unawaited key file writes; each writes the
/// index current when it runs, and the awaited write here starts after a short pause so it lands after them in the
/// usual case. A key manager API that sets the index and persists it in one serialized write would close that gap
/// (seam for the key-file owner).
/// </remarks>
public sealed class SecureKeyManagerKeyIndexReserver : IChannelKeyIndexReserver
{
    private static readonly TimeSpan s_settleDelay = TimeSpan.FromMilliseconds(200);

    private readonly ISecureKeyManager _keyManager;
    private readonly ILogger<SecureKeyManagerKeyIndexReserver> _logger;
    private readonly Lock _gate = new();

    public SecureKeyManagerKeyIndexReserver(ISecureKeyManager keyManager,
                                            ILogger<SecureKeyManagerKeyIndexReserver>? logger = null)
    {
        _keyManager = keyManager;
        _logger = logger ?? NullLogger<SecureKeyManagerKeyIndexReserver>.Instance;
    }

    /// <inheritdoc />
    public async Task<uint> ReserveThroughAsync(uint highestUsedIndex, CancellationToken cancellationToken)
    {
        uint index;
        var advanced = 0u;
        lock (_gate)
        {
            do
            {
                _keyManager.GetNextChannelKey(out index);
                advanced++;
            } while (index < highestUsedIndex);
        }

        if (advanced > 1)
            _logger.LogWarning("The key file's last used channel index was below the restored channels' highest ({Index})"
                             + "; advanced it to {Last}", highestUsedIndex, index);

        if (_keyManager is SecureKeyManager fileBacked)
        {
            await Task.Delay(s_settleDelay, cancellationToken);
            await fileBacked.UpdateLastUsedChannelIndexOnFile();
        }

        return index;
    }
}