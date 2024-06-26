namespace NLightning.Bolts.BOLT1.Interfaces;

using BOLT8.Interfaces;

/// <summary>
/// Interface for a message service factory.
/// </summary>
/// <remarks>
/// This interface is used to create a message service in test environments.
/// </remarks>
public interface IMessageServiceFactory
{
    /// <summary>
    /// Creates a message service.
    /// </summary>
    IMessageService CreateMessageService(ITransportService transportService);
}