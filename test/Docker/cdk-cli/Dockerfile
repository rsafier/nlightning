# CDK's wallet CLI (cdk-cli) for the Cashu mint proof (Cashu plan C2, NL-903): the fixture
# (test/NLightning.Integration.Tests/Fixtures/Cashu/CashuMintFixture.cs) builds it as nltg-cdk-cli:<CdkVersion> when the
# tag is missing and runs it with --network host against cdk-mintd. CDK publishes no cdk-cli image. A bump of
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
