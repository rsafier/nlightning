using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Ipc.Handlers;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Client.Enums;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using TestCollections;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

[Collection(SerialTestCollection.Name)]
public class GetAddressCapabilityTests
{
    [Theory]
    [InlineData(true, AddressType.P2Tr, true)]
    [InlineData(false, AddressType.P2Wpkh | AddressType.P2Tr, true)]
    [InlineData(false, AddressType.P2Tr, false)]
    public async Task Given_AP2WpkhOnlyWallet_When_AddressRequested_Then_OnlySupportedTypesAreReturned(
        bool useDefault, AddressType requested, bool succeeds)
    {
        var wallet = new Mock<IBitcoinWalletService>();
        wallet.SetupGet(service => service.SupportedWalletAddressTypes).Returns(AddressType.P2Wpkh);
        wallet.Setup(service => service.GetUnusedAddressAsync(AddressType.P2Wpkh, false))
              .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 0, false, "bcrt1qfixture"));
        var services = new ServiceCollection().AddSingleton(wallet.Object);
        using var provider = services.BuildServiceProvider();
        var handler = new GetAddressIpcHandler(NullLogger<GetAddressIpcHandler>.Instance, provider);
        MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options;
        var response = await handler.HandleAsync(new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.GetAddress,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(new GetAddressIpcRequest
            {
                AddressType = requested,
                UseDefaultAddressType = useDefault
            }, NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken)
        }, TestContext.Current.CancellationToken);

        wallet.Verify(service => service.GetUnusedAddressAsync(AddressType.P2Tr, It.IsAny<bool>()), Times.Never);
        if (succeeds)
        {
            Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
            var address = MessagePackSerializer.Deserialize<GetAddressIpcResponse>(response.Payload,
                NLightningMessagePackOptions.Options, TestContext.Current.CancellationToken);
            Assert.Equal("bcrt1qfixture", address.AddressP2Wpkh);
            Assert.Null(address.AddressP2Tr);
        }
        else
        {
            Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
            wallet.Verify(service => service.GetUnusedAddressAsync(It.IsAny<AddressType>(), It.IsAny<bool>()),
                Times.Never);
        }
    }
}