# Already-built real-node subscription proof; no compilation or dependency downloads inside the image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /runner
COPY runner/ /runner/
ENTRYPOINT ["dotnet", "/runner/NLightning.Integration.Tests.dll"]
CMD ["-explicit", "off", "-class", "NLightning.Integration.Tests.Docker.LndGrpcWave3FlowTests"]
