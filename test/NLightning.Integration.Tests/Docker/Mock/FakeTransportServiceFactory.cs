using NLightning.Domain.Protocol.Interfaces;

namespace NLightning.Integration.Tests.Docker.Mock;

using Domain.Transport;

internal class FakeTransportServiceFactory : ITransportServiceFactory, ITestTransportServiceFactory
{
    public ITransportService CreateTransportService(bool isInitiator, ReadOnlySpan<byte> s,
                                                    ReadOnlySpan<byte> rs, PeerConnection connection)
    {
        return CreateTransportService(isInitiator, s.ToArray(), rs.ToArray(), connection);
    }

    public ITransportService CreateTransportService(bool isInitiator, ReadOnlySpan<byte> localStaticPublicKey,
                                                    ReadOnlySpan<byte> rs, PeerConnection connection,
                                                    ProtectedStaticEcdh protectedStaticEcdh)
    {
        return CreateTransportService(isInitiator, localStaticPublicKey.ToArray(), rs.ToArray(), connection);
    }

    public virtual ITransportService CreateTransportService(bool isInitiator, byte[] s, byte[] rs,
                                                            PeerConnection connection)
    {
#pragma warning disable CS8603 // Possible null reference return
        return default;
#pragma warning restore CS8603 // Possible null reference return
    }
}