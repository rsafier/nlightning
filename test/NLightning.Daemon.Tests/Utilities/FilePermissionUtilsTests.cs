using System.Runtime.Versioning;
using Serilog;

namespace NLightning.Daemon.Tests.Utilities;

using Daemon.Utilities;

public class FilePermissionUtilsTests : IDisposable
{
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _directory;

    public FilePermissionUtilsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "nltg-perm-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Given_ANewDirectory_When_CreateOwnerOnlyDirectory_Then_ItAndItsNewParentsAre0700()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");

        // Arrange
        var parent = Path.Combine(_directory, ".nltg");
        var path = Path.Combine(parent, "regtest");

        // Act
        FilePermissionUtils.CreateOwnerOnlyDirectory(path);

        // Assert
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(ownerOnly, File.GetUnixFileMode(path));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(parent));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void Given_ANewFile_When_WriteNewOwnerOnlyFile_Then_ItIs0600WithTheContents()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");

        // Arrange
        var path = Path.Combine(_directory, "appsettings.json");

        // Act
        FilePermissionUtils.WriteNewOwnerOnlyFile(path, "{}");

        // Assert
        Assert.Equal(OwnerOnlyFile, File.GetUnixFileMode(path));
        Assert.Equal("{}", File.ReadAllText(path));
    }

    [Fact]
    public void Given_AnExistingFile_When_WriteNewOwnerOnlyFile_Then_ThrowsAndKeepsIt()
    {
        // Arrange
        var path = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(path, "keep");

        // Act & Assert
        Assert.Throws<IOException>(() => FilePermissionUtils.WriteNewOwnerOnlyFile(path, "{}"));
        Assert.Equal("keep", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite, false)]
    [InlineData(UnixFileMode.UserRead, false)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead, true)]
    [UnsupportedOSPlatform("windows")]
    public void Given_AFileMode_When_WarnIfAccessibleByOthers_Then_WarnsOnlyForGroupOrOtherBits(UnixFileMode mode,
        bool expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");

        // Arrange
        var path = Path.Combine(_directory, "secret");
        File.WriteAllText(path, "x");
        File.SetUnixFileMode(path, mode);
        var logger = new Mock<ILogger>();

        // Act
        var warned = FilePermissionUtils.WarnIfAccessibleByOthers(path, "The file", logger.Object);

        // Assert
        Assert.Equal(expected, warned);
        logger.Verify(x => x.Warning(It.IsAny<string>(), It.IsAny<object[]>()), expected ? Times.Once() : Times.Never());
    }

    [Fact]
    public void Given_AMissingPath_When_IsAccessibleByOthers_Then_False()
    {
        // Act & Assert
        Assert.False(FilePermissionUtils.IsAccessibleByOthers(Path.Combine(_directory, "missing")));
    }

    [Fact]
    public void Given_AConnectionStrings_When_GettingTheDatabaseFilePath_Then_OnlyAPlainPathIsReturned()
    {
        // NL-439: only a file on this machine can carry a permission problem to warn about
        Assert.Equal("/var/lib/nltg/nltg.db",
                     FilePermissionUtils.GetSqliteDatabaseFilePath("Sqlite", "Data Source=/var/lib/nltg/nltg.db"));
        Assert.Equal("nltg.db", FilePermissionUtils.GetSqliteDatabaseFilePath("Sqlite", "Data Source=nltg.db;Cache=Shared"));
        Assert.Null(FilePermissionUtils.GetSqliteDatabaseFilePath("Sqlite", "Data Source=:memory:"));
        Assert.Null(FilePermissionUtils.GetSqliteDatabaseFilePath("Sqlite", "Data Source=file:/tmp/nltg.db"));
        Assert.Null(FilePermissionUtils.GetSqliteDatabaseFilePath("Postgresql", "Data Source=nltg.db"));
        Assert.Null(FilePermissionUtils.GetSqliteDatabaseFilePath(null, "Data Source=nltg.db"));
        Assert.Null(FilePermissionUtils.GetSqliteDatabaseFilePath("Sqlite", null));
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite, false)]
    [InlineData(UnixFileMode.UserRead, false)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupExecute, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute,
                true)]
    [UnsupportedOSPlatform("windows")]
    public void Given_ADatabaseFileMode_When_WarnIfDatabaseAccessibleByOthers_Then_WarnsOnlyForGroupOrOtherBits(
        UnixFileMode mode, bool expected)
    {
        // NL-439: the database may sit outside the configuration directory, whose warning does not cover it
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes only");

        // Arrange
        var path = Path.Combine(_directory, "nltg.db");
        File.WriteAllText(path, "x");
        File.SetUnixFileMode(path, mode);
        var logger = new Mock<ILogger>();

        // Act
        var warned = FilePermissionUtils.WarnIfDatabaseAccessibleByOthers("Sqlite", $"Data Source={path}",
                                                                         logger.Object);

        // Assert
        Assert.Equal(expected, warned);
        logger.Verify(x => x.Warning(It.IsAny<string>(), It.IsAny<object[]>()), expected ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData("Sqlite", "Data Source=nltg.db;Cache=Shared")]
    [InlineData("Sqlite", "Data Source=:memory:")]
    [InlineData("Sqlite", "Data Source=file:nltg.db")]
    [InlineData("Postgres", "Data Source=nltg.db")]
    [UnsupportedOSPlatform("windows")]
    public void Given_NoDatabaseFileToWarnAbout_When_WarnIfDatabaseAccessibleByOthers_Then_DoesNotWarn(
        string provider, string connectionString)
    {
        // Arrange: nltg.db does not exist (not yet created, in-memory, a file: URI, or another provider)
        var logger = new Mock<ILogger>();

        // Act
        var warned = FilePermissionUtils.WarnIfDatabaseAccessibleByOthers(provider, connectionString, logger.Object);

        // Assert
        Assert.False(warned);
        logger.Verify(x => x.Warning(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never());
    }
}