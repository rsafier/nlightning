using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Channels.Backup;

using Domain.Protocol.Interfaces;
using Interfaces;

/// <summary>
/// Reserves restored channel indexes through the key-manager boundary, including remote signers.
/// Managers without reconciliation support advance public index reservations instead.
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

        try
        {
            if (_keyManager.EnsureLastUsedChannelIndexAtLeast(highestUsedIndex))
                _logger.LogWarning("The key file's last used channel index was below the restored channels' highest "
                                 + "({Index}); raised it", highestUsedIndex);
            return Task.FromResult(highestUsedIndex);
        }
        catch (NotSupportedException)
        {
            // Compatibility for local key managers that only support advancing reservations.
        }

        uint index;
        var advanced = 0u;
        lock (_gate)
        {
            index = _keyManager.ReserveChannelKeyIndex();
            advanced++;
            while (index < highestUsedIndex)
            {
                var previous = index;
                index = _keyManager.ReserveChannelKeyIndex();
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