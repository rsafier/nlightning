# ldk-server (LDK Node over gRPC) for the NLightning interop tests (NL-180). ldk-server has no tags, releases or
# official image, so this builds a pinned commit of github.com/lightningdevkit/ldk-server on pinned Rust and Debian
# images (the upstream Dockerfile's build, without its dependency-cache layer). The cluster harness runs it as
# nltg-ldk-server:dc02b76c with pull policy Never (ImageVersions.Ldk; the only backend of LdkFixture since NL-866) and
# never builds it: build it once with
#   docker build -t nltg-ldk-server:dc02b76c test/Docker/ldk_server
# (OrbStack's cluster shares the Docker image store). A cold build takes 10-20 min (release profile with LTO).
# A bump is a deliberate change of LDK_SERVER_COMMIT and the fixture's image tag.
FROM rust:1.85-bookworm@sha256:e51d0265072d2d9d5d320f6a44dde6b9ef13653b035098febd68cce8fa7c0bc4 AS builder
ARG LDK_SERVER_COMMIT=dc02b76c7d338161aeafbcd95a0be32e7dce2caa
ENV GIT_HASH=${LDK_SERVER_COMMIT}
WORKDIR /src
RUN git init -q . \
 && git remote add origin https://github.com/lightningdevkit/ldk-server \
 && git fetch -q --depth 1 origin ${LDK_SERVER_COMMIT} \
 && git checkout -q FETCH_HEAD \
 && test "$(git rev-parse HEAD)" = "${LDK_SERVER_COMMIT}"
RUN cargo build --release --locked -p ldk-server -p ldk-server-cli

FROM debian:bookworm-slim@sha256:3783cc01769c7b2b1b83a5c5ad96c815348e28ed7da68e2e3687004faa906251
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
COPY --from=builder /src/target/release/ldk-server /usr/local/bin/ldk-server
COPY --from=builder /src/target/release/ldk-server-cli /usr/local/bin/ldk-server-cli
ENV LDK_SERVER_NODE_GRPC_SERVICE_ADDRESS=0.0.0.0:3536
RUN mkdir -p /data
EXPOSE 9735 3536
ENTRYPOINT ["ldk-server"]
