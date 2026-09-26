using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Tests.Crypto.Hashes;

using Domain.Crypto.Hashes;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// <see cref="ThreadLocalSha256"/>, the shared <see cref="ISha256"/> of the container (NL-247).
/// </summary>
public class ThreadLocalSha256Tests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Given_TwoThreadsInterleaving_When_HashingWithOneSharedInstance_Then_EachGetsTheHashOfItsOwnData()
    {
        // Arrange - thread A appends, then thread B appends and reads, then thread A reads. A single shared Sha256
        // would give B the hash of "a" + "b" and A the hash of nothing.
        using var sha256 = new ThreadLocalSha256();
        using var aAppended = new ManualResetEventSlim();
        using var bDone = new ManualResetEventSlim();
        var hashA = new byte[32];
        var hashB = new byte[32];

        // Act
        var threadA = new Thread(() =>
        {
            sha256.AppendData("a"u8);
            aAppended.Set();
            bDone.Wait(s_timeout);
            sha256.GetHashAndReset(hashA);
        });
        var threadB = new Thread(() =>
        {
            aAppended.Wait(s_timeout);
            sha256.AppendData("b"u8);
            sha256.GetHashAndReset(hashB);
            bDone.Set();
        });
        threadA.Start();
        threadB.Start();
        threadA.Join(s_timeout);
        threadB.Join(s_timeout);

        // Assert
        Assert.Equal(SHA256.HashData("a"u8), hashA);
        Assert.Equal(SHA256.HashData("b"u8), hashB);
    }

    [Fact]
    public async Task Given_ManyConcurrentCallers_When_HashingWithOneSharedInstance_Then_EveryHashIsCorrect()
    {
        // Arrange
        using var sha256 = new ThreadLocalSha256();
        var inputs = Enumerable.Range(0, 2_000).Select(i => Encoding.UTF8.GetBytes($"message {i}")).ToArray();
        var results = new byte[inputs.Length][];

        // Act
        await Parallel.ForAsync(0, inputs.Length, TestContext.Current.CancellationToken, (i, _) =>
        {
            var hash = new byte[32];
            sha256.AppendData(inputs[i].AsSpan(0, 4));
            Thread.Yield();
            sha256.AppendData(inputs[i].AsSpan(4));
            sha256.GetHashAndReset(hash);
            results[i] = hash;
            return ValueTask.CompletedTask;
        });

        // Assert
        for (var i = 0; i < inputs.Length; i++)
            Assert.Equal(SHA256.HashData(inputs[i]), results[i]);
    }

    [Fact]
    public void Given_OneThread_When_HashingTwice_Then_TheStateIsResetBetweenHashes()
    {
        // Arrange
        using var sha256 = new ThreadLocalSha256();
        var first = new byte[32];
        var second = new byte[32];

        // Act
        sha256.AppendData("first"u8);
        sha256.GetHashAndReset(first);
        sha256.AppendData("second"u8);
        sha256.GetHashAndReset(second);

        // Assert
        Assert.Equal(SHA256.HashData("first"u8), first);
        Assert.Equal(SHA256.HashData("second"u8), second);
    }

    [Fact]
    public void Given_ADisposedInstance_When_Hashing_Then_ObjectDisposedExceptionIsThrown()
    {
        // Arrange
        var sha256 = new ThreadLocalSha256();
        sha256.AppendData("x"u8);
        sha256.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => sha256.AppendData("y"u8));
        sha256.Dispose(); // twice is harmless
    }

    [Fact]
    public void Given_InfrastructureServices_When_ResolvingISha256_Then_TheThreadSafeImplementationIsShared()
    {
        // Arrange
        using var provider = new ServiceCollection().AddInfrastructureServices().BuildServiceProvider();

        // Act
        var sha256 = provider.GetRequiredService<ISha256>();

        // Assert
        Assert.IsType<ThreadLocalSha256>(sha256);
        Assert.Same(sha256, provider.GetRequiredService<ISha256>());
    }
}