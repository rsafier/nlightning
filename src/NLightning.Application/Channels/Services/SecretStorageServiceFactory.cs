namespace NLightning.Application.Channels.Services;

using Domain.Protocol.Interfaces;
using Infrastructure.Protocol.Services;

/// <summary>
/// Creates empty shachain stores (<see cref="SecretStorageService"/>) for the peer's per-commitment secrets.
/// </summary>
/// <remarks>Registered by <c>AddApplicationServices</c> with <c>TryAdd</c>, because nothing else registers
/// <see cref="ISecretStorageServiceFactory"/> yet; an Infrastructure registration would take precedence if added
/// first.</remarks>
public sealed class SecretStorageServiceFactory : ISecretStorageServiceFactory
{
    /// <inheritdoc />
    public ISecretStorageService CreatePerCommitmentStorage() => new SecretStorageService();
}