namespace NLightning.Daemon.Tests.Client;

using NLightning.Client.Handlers;

/// <summary>
/// NL-679 (SECURITY_REVIEW SR-26): <c>accounting export --output</c> writes the node's money history through a new,
/// owner-only temporary file that is never a followed symlink, then renames it over the target.
/// </summary>
public sealed class AccountingExportFileTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("nltg-export-").FullName;

    [Fact]
    public async Task Given_AnExport_When_Written_Then_TheFileHasTheTextAndNoTemporaryFileIsLeft()
    {
        // Arrange
        var target = Path.Combine(_directory, "books.beancount");

        // Act
        var entries = await AccountingBooksCommands.WriteExportFileAsync(target, WriteAsync("2026-10-02 * \"x\"\n", 7),
                                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(7, entries);
        Assert.Equal("2026-10-02 * \"x\"\n", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal([target], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Given_AUmaskThatLetsOthersRead_When_Written_Then_TheFileIsOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix permissions");

        // Arrange: an existing world-readable target is replaced by an owner-only file
        var target = Path.Combine(_directory, "books.csv");
        await File.WriteAllTextAsync(target, "old", TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, (UnixFileMode)0b110_100_100);

        // Act
        await AccountingBooksCommands.WriteExportFileAsync(target, WriteAsync("new", 1),
                                                           TestContext.Current.CancellationToken);

        // Assert
        var mode = OperatingSystem.IsWindows() ? UnixFileMode.None : File.GetUnixFileMode(target);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        Assert.Equal("new", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_PlantedSymlinks_When_Written_Then_NoLinkTargetIsTouched()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symlinks need privileges on Windows");

        // Arrange: another user planted "<file>.part" (the old fixed name) and the target itself as links to a victim
        var victim = Path.Combine(_directory, "victim");
        await File.WriteAllTextAsync(victim, "precious", TestContext.Current.CancellationToken);
        var target = Path.Combine(_directory, "books.ledger");
        File.CreateSymbolicLink(target + ".part", victim);
        File.CreateSymbolicLink(target, victim);

        // Act
        await AccountingBooksCommands.WriteExportFileAsync(target, WriteAsync("history", 1),
                                                           TestContext.Current.CancellationToken);

        // Assert: the victim kept its content, the target is now a regular file of ours (the rename replaced the link)
        Assert.Equal("precious", await File.ReadAllTextAsync(victim, TestContext.Current.CancellationToken));
        Assert.Null(new FileInfo(target).LinkTarget);
        Assert.Equal("history", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal(victim, new FileInfo(target + ".part").LinkTarget);
    }

    [Fact]
    public async Task Given_AFailingExport_When_Written_Then_TheTargetIsKeptAndTheTemporaryFileRemoved()
    {
        // Arrange
        var target = Path.Combine(_directory, "books.csv");
        await File.WriteAllTextAsync(target, "previous", TestContext.Current.CancellationToken);

        // Act
        await Assert.ThrowsAsync<IOException>(() => AccountingBooksCommands.WriteExportFileAsync(
                                                  target, async writer =>
                                                  {
                                                      await writer.WriteAsync("partial");
                                                      throw new IOException("the daemon went away");
                                                  }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("previous", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal([target], Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is harmless
        }
    }

    private static Func<TextWriter, Task<int>> WriteAsync(string text, int entries) => async writer =>
    {
        await writer.WriteAsync(text);
        return entries;
    };
}