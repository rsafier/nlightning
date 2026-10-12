# CDK's wallet CLI (cdk-cli) for the Cashu mint proof (Cashu plan C2, NL-993) on the cluster harness: build it once as
#   docker build -t nltg-cdk-cli:0.18.1 test/Docker/cdk-cli
# (about 10 min); the cluster runs it with pull policy Never (ImageVersions.CdkCli) as an idle pod the fixture
# (test/NLightning.Integration.Tests/Fixtures/Cashu/CashuMintFixture.cs) execs cdk-cli in. CDK publishes no cdk-cli image. A bump of
# CDK_VERSION goes with the mintd image tag and the vendored proto (src/NLightning.Cashu.PaymentProcessor/Protos).
#
# Behind a TLS-intercepting proxy, pass its CA as a build secret: --secret id=ca,src=/path/ca.pem
FROM rust:1-bookworm AS build
ARG CDK_VERSION=0.18.1
RUN --mount=type=secret,id=ca,required=false \
    if [ -f /run/secrets/ca ]; then export CARGO_HTTP_CAINFO=/run/secrets/ca; fi; \
    cargo install cdk-cli --version "${CDK_VERSION}" --locked --root /out

FROM debian:bookworm-slim
COPY --from=build /out/bin/cdk-cli /usr/local/bin/cdk-cli
ENTRYPOINT ["/usr/local/bin/cdk-cli"]
