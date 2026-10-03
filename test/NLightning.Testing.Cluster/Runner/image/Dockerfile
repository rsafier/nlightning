# The in-cluster test runner (docs/agents/TEST_HARNESS_PLAN.md R5; LNUnit PR #10's phase 3, without the in-image
# build): the .NET SDK image plus the already built test assemblies. build.sh stages bin/<config>/net10.0 of
# NLightning.Testing.Cluster.Tests as the build context, so the image builds in seconds and never runs a restore.
FROM mcr.microsoft.com/dotnet/sdk:10.0

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

WORKDIR /runner
COPY . /runner/

# TestRunnerJob sets the command and the xunit v3 arguments; this default lists the tests.
ENTRYPOINT ["dotnet", "/runner/NLightning.Testing.Cluster.Tests.dll"]
CMD ["-list", "classes"]
