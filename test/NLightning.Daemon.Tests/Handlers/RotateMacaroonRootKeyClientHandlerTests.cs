using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using LndGrpc.Macaroons;

/// <summary>NL-1389: <c>nltg rotatemacaroonrootkey</c> replaces root key 0 and bakes the default macaroons again.
/// </summary>
public sealed class RotateMacaroonRootKeyClientHandlerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nltg-rotate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task Given_ARootKeyStore_When_Rotated_Then_TheOldAdminMacaroonIsReplaced()
    {
        // Arrange
        Directory.CreateDirectory(_directory);
        var oldKey = LndMacaroonFiles.EnsureCreated(_directory);
        var oldAdmin = File.ReadAllBytes(Path.Combine(_directory, LndMacaroonFiles.AdminFileName));
        var store = new LndRootKeyStore(_directory);
        await using var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var handler = new RotateMacaroonRootKeyClientHandler(services,
                                                             NullLogger<RotateMacaroonRootKeyClientHandler>.Instance);

        // Act
        var response = await handler.HandleAsync(new RotateMacaroonRootKeyClientRequest(),
                                                  TestContext.Current.CancellationToken);

        // Assert
        Assert.True(response.RebakedDefaults);
        Assert.False(response.InDatabase);
        Assert.NotEqual(oldKey, store.TryGet(0));
        Assert.NotEqual(oldAdmin, File.ReadAllBytes(Path.Combine(_directory, LndMacaroonFiles.AdminFileName)));
    }

    [Fact]
    public async Task Given_NoLndGrpcServer_When_Rotated_Then_ItIsRefused()
    {
        // Arrange
        await using var services = new ServiceCollection().BuildServiceProvider();
        var handler = new RotateMacaroonRootKeyClientHandler(services,
                                                             NullLogger<RotateMacaroonRootKeyClientHandler>.Instance);

        // Act & Assert
        await Assert.ThrowsAsync<ClientException>(() => handler.HandleAsync(new RotateMacaroonRootKeyClientRequest(),
                                                                            TestContext.Current.CancellationToken));
    }
}