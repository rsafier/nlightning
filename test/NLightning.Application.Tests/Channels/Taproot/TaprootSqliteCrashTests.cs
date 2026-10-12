namespace NLightning.Application.Tests.Channels.Taproot;

/// <summary>
/// Plan D-T4's gate on SQLite (NL-956): <see cref="TaprootCrashProof"/> on the nodes' SQLite files (production
/// repositories and migrations). The Postgres run is <c>NLightning.Integration.Tests.Docker.TaprootPostgresCrashTests</c>
/// in the cluster's <c>postgres</c> suite (NL-960).
/// </summary>
public class TaprootSqliteCrashTests
{
    [Theory]
    [InlineData("Alice")]
    [InlineData("Bob")]
    public async Task Given_ADatabaseCrashAtEverySave_When_TheNodeRestarts_Then_NoNonceIsReusedAndTheChannelGoesOn(
        string crashing)
    {
        // Arrange, Act and Assert: one run per save of the crashing node
        await TaprootCrashProof.RunAsync(crashing);
    }
}