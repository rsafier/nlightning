FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /runner
COPY runner/ /runner/
COPY lnc-bin/ /lnc-bin/
ENTRYPOINT ["dotnet", "/runner/NLightning.Integration.Tests.dll"]
CMD ["-explicit", "only", "-class", "NLightning.Integration.Tests.Cluster.Live.LncClusterTests"]
