using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Channels.Backup;

using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Managers;
using Interfaces;

/// <summary>
/// <see cref="IChannelKeyIndexReserver"/> over <see cref="ISecureKeyManager"/>. The file-backed
/// <see cref="SecureKeyManager"/> raises its last used index with
/// <see cref="SecureKeyManager.EnsureLastUsedChannelIndexAtLeast"/> (never lowers it; one serialized, awaited write, and
/// no index used up when the file is already past the restored channels). Any other key manager is advanced with
/// <see cref="ISecureKeyManager.GetNextChannelKey"/> (the only way the port offers; one index is always used up, which
/// is harmless) until it passes the highest restored index.
/// </summary>
public sealed class SecureKeyManagerKeyIndexReserver : IChannelKeyIndexReserver
{
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
    public Task<uint> ReserveThroughAsync(uint highestUsedIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_keyManager is SecureKeyManager fileBacked)
        {
            if (fileBacked.EnsureLastUsedChannelIndexAtLeast(highestUsedIndex))
                _logger.LogWarning("The key file's last used channel index was below the restored channels' highest "
                                 + "({Index}); raised it", highestUsedIndex);
            return Task.FromResult(highestUsedIndex);
        }

        uint index;
        var advanced = 0u;
        lock (_gate)
        {
            _keyManager.GetNextChannelKey(out index);
            advanced++;
            while (index < highestUsedIndex)
            {
                var previous = index;
                _keyManager.GetNextChannelKey(out index);
                advanced++;

                // A key manager that does not advance would loop forever (and hand out a restored channel's index)
                if (index <= previous)
                    throw new InvalidOperationException(
                        $"The key manager's channel index did not advance past {previous} while reserving up to "
                      + $"{highestUsedIndex}");
            }
        }

        if (advanced > 1)
            _logger.LogWarning("The key manager's last used channel index was below the restored channels' highest "
                             + "({Index}); advanced it to {Last}", highestUsedIndex, index);

        return Task.FromResult(index);
    }
}