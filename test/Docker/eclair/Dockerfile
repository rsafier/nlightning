# Eclair for the NLightning interop tests (NL-180, NL-551). ACINQ publishes no pinned multi-arch image past 0.8.0
# (acinq/eclair:latest is an amd64 master snapshot), so this builds the v0.14.3 release zip, checked by sha256, on a
# pinned Temurin 21 JRE. The fixture (test/NLightning.Integration.Tests/Fixtures/EclairFixture.cs) builds it as
# nltg-eclair:0.14.3 when the tag is missing; a bump is a deliberate change of all three ARGs and the tag.
FROM eclipse-temurin:21-jre-noble@sha256:7edbe8532195c8222735caca437e56780e1fc7db08a4b4b4af77fe188cccc15f
ARG ECLAIR_VERSION=0.14.3
ARG ECLAIR_COMMIT=1de8b2d
ARG ECLAIR_SHA256=06bd8f1a203b6fd7ba0659a3c20e69e69712591ef24b109caad90c0da4ad4535
RUN apt-get update && apt-get install -y --no-install-recommends bash jq curl unzip && rm -rf /var/lib/apt/lists/*
WORKDIR /app
RUN curl -fsSL -o eclair-node.zip https://github.com/ACINQ/eclair/releases/download/v${ECLAIR_VERSION}/eclair-node-${ECLAIR_VERSION}-${ECLAIR_COMMIT}-bin.zip \
 && echo "${ECLAIR_SHA256}  eclair-node.zip" | sha256sum -c - \
 && unzip -q eclair-node.zip && mv eclair-node-* eclair-node && rm eclair-node.zip \
 && chmod +x eclair-node/bin/eclair-node.sh eclair-node/bin/eclair-cli && ln -s /app/eclair-node/bin/eclair-cli /usr/local/bin/eclair-cli
ENV ECLAIR_DATADIR=/data
RUN mkdir -p /data
EXPOSE 9735 8080
ENTRYPOINT ["/bin/sh","-c","exec eclair-node/bin/eclair-node.sh -Declair.datadir=${ECLAIR_DATADIR}"]
