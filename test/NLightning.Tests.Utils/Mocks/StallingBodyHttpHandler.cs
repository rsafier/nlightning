using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;

namespace NLightning.Tests.Utils.Mocks;

/// <summary>
/// An HTTP server that answers 200 with its headers at once and then stalls the body: the first
/// <see cref="Prefix"/> bytes are served and every later read waits until the reader's token is cancelled (a stalled
/// server or Tor circuit; NL-732). <see cref="BodyReadCancelled"/> tells whether the reader gave up on the body.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class StallingBodyHttpHandler : HttpMessageHandler
{
    /// <summary>The bytes served before the stall.</summary>
    public string Prefix { get; init; } = "{";

    /// <summary>True once a read of the stalled body was cancelled.</summary>
    public bool BodyReadCancelled { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                           CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream(this, Encoding.UTF8.GetBytes(Prefix)))
        });

    private sealed class StallingStream(StallingBodyHttpHandler owner, byte[] prefix) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
                                                       CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                owner.BodyReadCancelled = true;
                throw;
            }

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                                            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
