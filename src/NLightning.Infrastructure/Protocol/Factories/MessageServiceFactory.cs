using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Protocol.Factories;

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

    public MessageServiceFactory(IMessageSerializer messageSerializer, ILoggerFactory loggerFactory,
                                 IOnionMessageDropCounter? onionMessageDrops = null)
    {
        _messageSerializer = messageSerializer;
        _loggerFactory = loggerFactory;
        _onionMessageDrops = onionMessageDrops;
    }

    /// <inheritdoc />
    public IMessageService CreateMessageService(ITransportService transportService)
    {
        return new MessageService(_loggerFactory.CreateLogger<IMessageService>(), _messageSerializer, transportService,
                                  _onionMessageDrops);
    }
}