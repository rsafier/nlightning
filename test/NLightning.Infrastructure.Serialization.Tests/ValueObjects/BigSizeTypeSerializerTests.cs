using System.Runtime.Serialization;
using NLightning.Domain.Protocol.ValueObjects;

namespace NLightning.Infrastructure.Serialization.Tests.ValueObjects;

using Infrastructure.Serialization.ValueObjects;

public class BigSizeTypeSerializerTests
{
    [Fact]
    public async Task Given_VectorInputs_When_DeserializeBigSize_Then_ResultIsKnown()
    {
        var testVectors = ReadTestVectors("Vectors/BigSize.txt").Where(x => x.Error == null);
        var bigSizeSerializer = new BigSizeTypeSerializer();

        foreach (var testVector in testVectors)
        {
            // Given
            using var memoryStream = new MemoryStream(testVector.Bytes);

            // When
            var bigSizeValue = await bigSizeSerializer.DeserializeAsync(memoryStream);

            // Then
            Assert.Equal(testVector.Value, bigSizeValue.Value);
        }
    }

    [Fact]
    public async Task Given_VectorInputs_When_DeserializeBigSize_Then_ErrorIsThrown()
    {
        // Arrange
        var testVectors = ReadTestVectors("Vectors/BigSize.txt").Where(x => x.Error != null);
        var bigSizeSerializer = new BigSizeTypeSerializer();

        foreach (var testVector in testVectors)
        {
            // Arrange
            using var memoryStream = new MemoryStream(testVector.Bytes);

            // Act
            var exception = await Assert.ThrowsAsync<SerializationException>(Deserialize);

            // Assert
            Assert.Equal(testVector.Error, exception.Message);
            continue;

            Task Deserialize() => bigSizeSerializer.DeserializeAsync(memoryStream);
        }
    }

    [Theory]
    [InlineData("fd00fc")]
    [InlineData("fd0000")]
    [InlineData("fe0000ffff")]
    [InlineData("fe00000000")]
    [InlineData("ff00000000ffffffff")]
    [InlineData("ff0000000000000000")]
    public async Task Given_NonMinimalEncoding_When_DeserializeBigSize_Then_NotCanonicalErrorIsThrown(string hex)
    {
        // Arrange
        var bigSizeSerializer = new BigSizeTypeSerializer();
        using var memoryStream = new MemoryStream(Convert.FromHexString(hex));

        // Act
        var exception = await Assert.ThrowsAsync<SerializationException>(() => bigSizeSerializer.DeserializeAsync(memoryStream));

        // Assert
        Assert.Equal(BigSizeTypeSerializer.NonCanonicalErrorMessage, exception.Message);
    }

    [Fact]
    public async Task Given_EmptyStream_When_DeserializeBigSize_Then_SerializationExceptionIsThrown()
    {
        // Arrange
        var bigSizeSerializer = new BigSizeTypeSerializer();
        using var memoryStream = new MemoryStream();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SerializationException>(
            () => bigSizeSerializer.DeserializeAsync(memoryStream));
        Assert.Equal("BigSize cannot be read from an empty stream.", exception.Message);
    }

    [Theory]
    [InlineData("fd")]                        // the 2-byte continuation is missing entirely
    [InlineData("fd01")]                      // only one of the two continuation bytes
    [InlineData("fe0000ff")]                  // three of the four continuation bytes
    [InlineData("ff0000000000000f")]          // seven of the eight continuation bytes
    public async Task Given_TruncatedStream_When_DeserializeBigSize_Then_SerializationExceptionIsThrown(string hex)
    {
        // Arrange
        var bigSizeSerializer = new BigSizeTypeSerializer();
        using var memoryStream = new MemoryStream(Convert.FromHexString(hex));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SerializationException>(
            () => bigSizeSerializer.DeserializeAsync(memoryStream));
        Assert.Equal("BigSize cannot be read from a stream with insufficient data.", exception.Message);
    }

    [Fact]
    public async Task Given_NonSeekableStream_When_DeserializeBigSize_Then_ResultIsKnown()
    {
        // Arrange: a BigSize of 65535 (fdffff) on a stream that cannot report Position/Length
        var bigSizeSerializer = new BigSizeTypeSerializer();
        using var memoryStream = new NonSeekableStream(Convert.FromHexString("fdffff"));

        // Act
        var bigSizeValue = await bigSizeSerializer.DeserializeAsync(memoryStream);

        // Then
        Assert.Equal(65535UL, bigSizeValue.Value);
    }

    [Fact]
    public async Task Given_TruncatedNonSeekableStream_When_DeserializeBigSize_Then_SerializationExceptionIsThrown()
    {
        // Arrange
        var bigSizeSerializer = new BigSizeTypeSerializer();
        using var memoryStream = new NonSeekableStream(Convert.FromHexString("fd01"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<SerializationException>(
            () => bigSizeSerializer.DeserializeAsync(memoryStream));
        Assert.Equal("BigSize cannot be read from a stream with insufficient data.", exception.Message);
    }

    [Fact]
    public void Given_VectorFile_When_Read_Then_ContainsAllBolt1AppendixAVectors()
    {
        // Act
        var testVectors = ReadTestVectors("Vectors/BigSize.txt");

        // Assert
        Assert.Equal(18, testVectors.Count);
        Assert.Equal(3, testVectors.Count(x => x.Error == BigSizeTypeSerializer.NonCanonicalErrorMessage));
    }

    [Fact]
    public async Task Given_VectorInputs_When_SerializeBigSize_Then_ResultIsKnown()
    {
        // Arrange
        var testVectors = ReadTestVectors("Vectors/BigSize.txt").Where(x => x.Error == null);
        var bigSizeSerializer = new BigSizeTypeSerializer();

        foreach (var testVector in testVectors)
        {
            // Arrange
            using var memoryStream = new MemoryStream();

            // Act
            await bigSizeSerializer.SerializeAsync(new BigSize(testVector.Value), memoryStream);

            // Assert
            Assert.Equal(testVector.Bytes, memoryStream.ToArray());
        }
    }

    private class TestVector
    {
        public ulong Value { get; set; }
        public byte[] Bytes { get; set; } = [];
        public string? Error { get; set; }
    }

    /// <summary>
    /// A read-only stream that cannot report its position or length (CanSeek is false), like a network stream.
    /// </summary>
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static List<TestVector> ReadTestVectors(string filePath)
    {
        var testVectors = new List<TestVector>();
        TestVector? currentVector = null;

        foreach (var line in File.ReadLines(filePath))
        {
            if (line.StartsWith("Value: "))
            {
                currentVector = new TestVector
                {
                    Value = ulong.Parse(line[7..])
                };
            }
            else if (line.StartsWith("Bytes: "))
            {
                if (currentVector == null)
                {
                    throw new InvalidOperationException("Bytes line without Value line");
                }

                currentVector.Bytes = Convert.FromHexString(line[7..]);
            }
            else if (line.StartsWith("Error: "))
            {
                if (currentVector == null)
                {
                    throw new InvalidOperationException("Bytes line without Value line");
                }

                if (currentVector.Bytes == null)
                {
                    throw new InvalidOperationException("Error line without Bytes line");
                }

                var error = line[7..];
                if (!string.IsNullOrEmpty(error))
                {
                    currentVector.Error = error;
                }

                testVectors.Add(currentVector);
            }
        }

        return testVectors;
    }
}