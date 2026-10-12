using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Integration.Tests.TestUtils;

using Docker.Interop.Cln;
using Docker.Utils;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Serialization.Interfaces;
using Infrastructure.Crypto.Interfaces;
using Infrastructure.Protocol.Factories;
using Infrastructure.Transport.Factories;

/// <summary>
/// Container-free checks of the Proof M6 helpers (<c>ClnOnionMessageTests</c>): the <see cref="RawOnionMessageRecorder"/>
/// wire parsing, CLN's feature-bit reader and our proof <c>invoice_error</c>. Kept out of the Docker namespace so the
/// CI run (<c>FullyQualifiedName!~Docker</c>) covers them.
/// </summary>
public sealed class OnionMessageProofHelperTests
{
    private const string PathKeyHex = "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619";

    [Fact]
    public void Given_ARecordedOnionMessage_When_Parsed_Then_PathKeyAndPacketAreItsFields()
    {
        // Arrange: u16 513, path_key, u16 len = 70, 70 packet bytes
        var pathKey = Convert.FromHexString(PathKeyHex);
        var packet = Enumerable.Range(0, 70).Select(i => (byte)i).ToArray();
        var wire = new byte[] { 0x02, 0x01 }.Concat(pathKey).Concat(new byte[] { 0x00, 70 }).Concat(packet).ToArray();
        var recorder = new RawOnionMessageRecorder();

        // Act
        recorder.Record(wire, outbound: false);
        recorder.Record([0x00, 0x01, 0x00], outbound: true);
        recorder.Record([0x01, 0x00], outbound: true);

        // Assert
        var recorded = Assert.Single(recorder.ReceivedOnionMessages);
        Assert.Equal(pathKey, recorded.PathKey);
        Assert.Equal(packet, recorded.OnionMessagePacket);
        Assert.Single(recorder.SentWarningsAndErrors);
        Assert.Empty(recorder.SentOnionMessages);
    }

    [Fact]
    public async Task Given_AnInstalledRecorder_When_TheNodeSerializerIsUsedOffTheWire_Then_NothingIsRecorded()
    {
        // Arrange: the node's own serializer writes an error (type 17), as ChannelManager does for a stored error
        var nodeSerializer = new Mock<IMessageSerializer>();
        nodeSerializer.Setup(x => x.SerializeAsync(It.IsAny<IMessage>(), It.IsAny<Stream>()))
                      .Returns<IMessage, Stream>((_, stream) => stream.WriteAsync(new byte[] { 0x00, 0x11, 0x00 })
                                                                       .AsTask());
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(nodeSerializer.Object);
        services.AddSingleton(Mock.Of<IEcdh>());
        services.AddSingleton(Options.Create(new NodeOptions()));
        var recorder = new RawOnionMessageRecorder();

        // Act
        recorder.Install(services);
        await using var provider = services.BuildServiceProvider();
        using var stream = new MemoryStream();
        await provider.GetRequiredService<IMessageSerializer>().SerializeAsync(Mock.Of<IMessage>(), stream);

        // Assert: the node-wide serializer is untouched; only the transport's message services record
        Assert.Same(nodeSerializer.Object, provider.GetRequiredService<IMessageSerializer>());
        Assert.IsType<MessageServiceFactory>(provider.GetRequiredService<IMessageServiceFactory>());
        Assert.IsType<TransportServiceFactory>(provider.GetRequiredService<ITransportServiceFactory>());
        Assert.Empty(recorder.Traffic);
    }

    [Theory]
    [InlineData("8000000000", 39, true)]
    [InlineData("4000000000", 38, true)]
    [InlineData("4000000000", 39, false)]
    [InlineData("02", 1, true)]
    [InlineData("02", 39, false)]
    public void Given_ClnFeatureHex_When_ReadingABit_Then_BigEndianBitOrder(string hex, int bit, bool expected)
    {
        // Act / Assert
        Assert.Equal(expected, ClnOnionMessageTests.IsBitSet(hex, bit));
    }

    [Fact]
    public void Given_OurInvoiceError_When_Checked_Then_ItCarriesTheProofText()
    {
        // Arrange
        var error = ClnOnionMessageTests.ProofInvoiceError();

        // Act / Assert
        Assert.Equal(ClnOnionMessageTests.InvoiceErrorErrorType, error[0]);
        Assert.Equal(ClnOnionMessageTests.ProofErrorText.Length, error[1]);
        Assert.True(ClnOnionMessageTests.ContainsProofError(Convert.ToHexStringLower(error)));
        Assert.True(ClnOnionMessageTests.ContainsProofError($"{{\"error\":\"{ClnOnionMessageTests.ProofErrorText}\"}}"));
    }
}