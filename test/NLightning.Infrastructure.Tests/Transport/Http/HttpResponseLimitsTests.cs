using System.Text;

namespace NLightning.Infrastructure.Tests.Transport.Http;

using Infrastructure.Transport.Http;

/// <summary>The bounded reads of the node's own HTTP answers (NL-678).</summary>
public class HttpResponseLimitsTests
{
    [Fact]
    public async Task Given_ABodyWithinTheCap_When_Read_Then_ItIsReturnedWhole()
    {
        // Arrange
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"fastestFee\":5}"));

        // Act
        var text = await HttpResponseLimits.ReadBoundedStringAsync(content, 16, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("{\"fastestFee\":5}", text);
    }

    [Fact]
    public async Task Given_ADeclaredLengthOverTheCap_When_Read_Then_ItIsRefusedBeforeReading()
    {
        // Arrange
        var stream = new CountingStream(new byte[1000]);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = 1000;

        // Act
        var e = await Assert.ThrowsAsync<HttpResponseTooLargeException>(
            () => HttpResponseLimits.ReadBoundedAsync(content, 999, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(1000, e.DeclaredLength);
        Assert.Equal(0, stream.BytesRead);
    }

    [Fact]
    public async Task Given_AnUndeclaredBodyOverTheCap_When_Read_Then_ItStopsOneBytePastTheCap()
    {
        // Arrange: no Content-Length, a body far larger than the cap
        var stream = new CountingStream(new byte[1_000_000]);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = null;

        // Act
        var e = await Assert.ThrowsAsync<HttpResponseTooLargeException>(
            () => HttpResponseLimits.ReadBoundedAsync(content, 100, TestContext.Current.CancellationToken));

        // Assert
        Assert.Null(e.DeclaredLength);
        Assert.Equal(101, stream.BytesRead);
        Assert.IsAssignableFrom<HttpRequestException>(e);
    }

    [Fact]
    public async Task Given_ABodyOfExactlyTheCap_When_Read_Then_ItIsAccepted()
    {
        // Arrange
        using var content = new StreamContent(new CountingStream(new byte[64]));
        content.Headers.ContentLength = null;

        // Act
        var bytes = await HttpResponseLimits.ReadBoundedAsync(content, 64, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(64, bytes.Length);
    }

    [Fact]
    public async Task Given_AUtf8ByteOrderMark_When_ReadAsText_Then_ItIsSkipped()
    {
        // Arrange
        using var content = new ByteArrayContent([0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}']);

        // Act
        var text = await HttpResponseLimits.ReadBoundedStringAsync(content, 16, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("{}", text);
    }

    /// <summary>A memory stream that counts what was read and hides its length (a network body).</summary>
    private sealed class CountingStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}