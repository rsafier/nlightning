using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Protocol.Factories;

using Domain.Node.Fencing;
using Domain.Protocol.Interfaces;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Serialization.Interfaces;
using Domain.Transport;
using Services;

/// <summary>
/// Factory for creating a message service.
/// </summary>
/// <remarks>
/// This class is used to create a message service in test environments.
/// </remarks>
public sealed class MessageServiceFactory : IMessageServiceFactory
{
    private readonly IMessageSerializer _messageSerializer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IOnionMessageDropCounter? _onionMessageDrops;
    private readonly INodeWriteFence? _writeFence;

    /// <param name="messageSerializer">The message serializer.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="onionMessageDrops">The onion message drop counter, or null.</param>
    /// <param name="writeFence">The optional node write fence every message service asks before a send (NL-1343);
    /// none registered (the default): nothing is checked.</param>
    public MessageServiceFactory(IMessageSerializer messageSerializer, ILoggerFactory loggerFactory,
                                 IOnionMessageDropCounter? onionMessageDrops = null,
                                 INodeWriteFence? writeFence = null)
    {
        _messageSerializer = messageSerializer;
        _loggerFactory = loggerFactory;
        _onionMessageDrops = onionMessageDrops;
        _writeFence = writeFence;
    }

    /// <inheritdoc />
    public IMessageService CreateMessageService(ITransportService transportService)
    {
        return new MessageService(_loggerFactory.CreateLogger<IMessageService>(), _messageSerializer, transportService,
                                  _onionMessageDrops, _writeFence);
    }
}