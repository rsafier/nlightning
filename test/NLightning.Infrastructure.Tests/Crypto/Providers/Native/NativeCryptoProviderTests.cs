#if CRYPTO_NATIVE
using System.Runtime.InteropServices;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Tests.Crypto.Providers.Native;

using Infrastructure.Crypto.Providers.Native;

public class NativeCryptoProviderTests
{
    [Fact]
    public void
        GivenValidInputs_WhenAeadChacha20Poly1305IetfEncryptCalled_ThenCallsNativeCryptoAeadChacha20Poly1305IetfEncrypt()
    {
        // Arrange
        var cryptoProvider = new NativeCryptoProvider();
        Span<byte> cipher = new byte[AeadChacha20Poly1305IetfVector.Message.Length + 16];

        // Act
        cryptoProvider
           .AeadChaCha20Poly1305IetfEncrypt(AeadChacha20Poly1305IetfVector.Key,
                                            AeadChacha20Poly1305IetfVector.PublicNonce, null,
                                            AeadChacha20Poly1305IetfVector.AuthenticationData,
                                            AeadChacha20Poly1305IetfVector.Message, cipher, out var clenP);

        // Assert 
        Assert.Equal(AeadChacha20Poly1305IetfVector.Cipher.Length, clenP);
        Assert.Equal(AeadChacha20Poly1305IetfVector.Cipher, cipher.ToArray());
    }


    [Fact]
    public void
        GivenValidInputs_WhenAeadChacha20Poly1305IetfDecryptCalled_ThenCallsNativeCryptoAeadChacha20Poly1305IetfDecrypt()
    {
        // Arrange
        var cryptoProvider = new NativeCryptoProvider();
        Span<byte> message = new byte[AeadChacha20Poly1305IetfVector.Message.Length];

        // Act
        cryptoProvider
           .AeadChaCha20Poly1305IetfDecrypt(AeadChacha20Poly1305IetfVector.Key,
                                            AeadChacha20Poly1305IetfVector.PublicNonce, null,
                                            AeadChacha20Poly1305IetfVector.AuthenticationData,
                                            AeadChacha20Poly1305IetfVector.Cipher, message, out var clenP);

        // Assert 
        Assert.Equal(AeadChacha20Poly1305IetfVector.Message.Length, clenP);
        Assert.Equal(AeadChacha20Poly1305IetfVector.Message, message.ToArray());
    }

    [Fact]
    public void
        GivenValidInputs_WhenAeadXChacha20Poly1305IetfEncryptCalled_ThenCallsNativeCryptoAeadChacha20Poly1305IetfEncrypt()
    {
        // Arrange
        var cryptoProvider = new NativeCryptoProvider();
        Span<byte> cipher = stackalloc byte[AeadXChacha20Poly1305IetfVector.Message.Length + 16];

        // Act
        cryptoProvider
           .AeadXChaCha20Poly1305IetfEncrypt(AeadXChacha20Poly1305IetfVector.Key,
                                             AeadXChacha20Poly1305IetfVector.PublicNonce,
                                             AeadXChacha20Poly1305IetfVector.AuthenticationData,
                                             AeadXChacha20Poly1305IetfVector.Message, cipher, out var clenP);

        // Assert 
        Assert.Equal(AeadXChacha20Poly1305IetfVector.Cipher.Length, clenP);
        Assert.Equal(AeadXChacha20Poly1305IetfVector.Cipher, cipher.ToArray());
    }

    [Fact]
    public void
        GivenValidInputs_WhenAeadXChacha20Poly1305IetfDecryptCalled_ThenCallsNativeCryptoAeadChacha20Poly1305IetfDecrypt()
    {
        // Arrange
        var cryptoProvider = new NativeCryptoProvider();
        Span<byte> message = new byte[AeadXChacha20Poly1305IetfVector.Message.Length];

        // Act
        cryptoProvider
           .AeadXChaCha20Poly1305IetfDecrypt(AeadXChacha20Poly1305IetfVector.Key,
                                             AeadXChacha20Poly1305IetfVector.PublicNonce,
                                             AeadXChacha20Poly1305IetfVector.AuthenticationData,
                                             AeadXChacha20Poly1305IetfVector.Cipher, message, out var clenP);

        // Assert 
        Assert.Equal(AeadXChacha20Poly1305IetfVector.Message.Length, clenP);
        Assert.Equal(AeadXChacha20Poly1305IetfVector.Message, message.ToArray());
    }

    [Fact]
    public void Given_Rfc8439A1Vector5_When_StreamChaCha20IetfXorCalled_Then_OutputMatchesKeystream()
    {
        // Arrange
        // RFC 8439 A.1 test vector #5: key = 0^32, nonce = 0^11 || 0x02, block counter = 0
        using var cryptoProvider = new NativeCryptoProvider();
        var key = new byte[32];
        var nonce = Convert.FromHexString("000000000000000000000002");
        var expected = Convert.FromHexString(
            "c2c64d378cd536374ae204b9ef933fcd1a8b2288b3dfa49672ab765b54ee27c7" +
            "8a970e0e955c14f3a88e741b97c286f75f8fc299e8148362fa198a39531bed6d");
        var output = new byte[64];

        // Act
        var result = cryptoProvider.StreamChaCha20IetfXor(key, nonce, new byte[64], output);

        // Assert
        Assert.Equal(0, result);
        Assert.Equal(expected, output);
    }

    [Fact]
    public void Given_InvalidNonceLength_When_StreamChaCha20IetfXorCalled_Then_ThrowsArgumentException()
    {
        // Arrange
        using var cryptoProvider = new NativeCryptoProvider();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => cryptoProvider.StreamChaCha20IetfXor(new byte[32], new byte[8],
                                                                                     new byte[64], new byte[64]));
    }

    [Fact]
    public void Given_AllocatedMemory_When_MemoryLockAndUnlock_Then_SucceedsAndMemoryStaysUsable()
    {
        // Arrange (NL-163: mlock(2)/munlock(2) on Unix, VirtualLock on Windows)
        using var cryptoProvider = new NativeCryptoProvider();
        var ptr = cryptoProvider.MemoryAlloc(64);
        try
        {
            // Act
            var lockResult = cryptoProvider.MemoryLock(ptr, 64);
            Marshal.WriteByte(ptr, 0xAB);

            // Assert
            Assert.Equal(0, lockResult);
            Assert.Equal(0xAB, Marshal.ReadByte(ptr));

            cryptoProvider.MemoryUnlock(ptr, 64);
            Marshal.WriteByte(ptr, 0xCD);
            Assert.Equal(0xCD, Marshal.ReadByte(ptr));
        }
        finally
        {
            cryptoProvider.MemoryFree(ptr);
        }
    }

    [Fact]
    public void Given_UnalignedMultiPageRange_When_MemoryLockAndUnlock_Then_PagesAreCovered()
    {
        // Arrange: an offset inside its first page and a length crossing a page boundary exercise the page rounding
        // of the Unix mlock path (Windows locks the byte range directly and must behave the same)
        using var cryptoProvider = new NativeCryptoProvider();
        var pageSize = Environment.SystemPageSize;
        var ptr = cryptoProvider.MemoryAlloc((ulong)(3 * pageSize));
        var offset = ptr + pageSize / 2;
        try
        {
            // Act
            var lockResult = cryptoProvider.MemoryLock(offset, (ulong)pageSize);

            // Assert
            Assert.Equal(0, lockResult);
            Marshal.WriteByte(offset, 0x12);
            Marshal.WriteByte(offset + pageSize, 0x34);
            Assert.Equal(0x12, Marshal.ReadByte(offset));
            Assert.Equal(0x34, Marshal.ReadByte(offset + pageSize));

            cryptoProvider.MemoryUnlock(offset, (ulong)pageSize);
            Marshal.WriteByte(offset + pageSize, 0x56);
            Assert.Equal(0x56, Marshal.ReadByte(offset + pageSize));
        }
        finally
        {
            cryptoProvider.MemoryFree(ptr);
        }
    }

    [Fact]
    public void Given_ZeroLength_When_MemoryLockOrUnlock_Then_IsANoop()
    {
        // Arrange
        using var cryptoProvider = new NativeCryptoProvider();
        var ptr = cryptoProvider.MemoryAlloc(16);
        try
        {
            // Act & Assert
            Assert.Equal(0, cryptoProvider.MemoryLock(ptr, 0));
            cryptoProvider.MemoryUnlock(ptr, 0);
        }
        finally
        {
            cryptoProvider.MemoryFree(ptr);
        }
    }
}
#endif