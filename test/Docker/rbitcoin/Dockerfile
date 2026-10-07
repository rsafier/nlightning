# rbitcoin (https://github.com/reardencode/rbitcoin), a from-scratch Rust full node with a Bitcoin Core-compatible
# JSON-RPC subset, for the cluster harness's rbitcoin contract test (NL-1095). Built from source at a pinned master
# commit: v0.7.0 (the last tag) has no --rpc-cookie-file, so its TCP RPC takes only a Bearer token and NBitcoin's Basic
# auth cannot talk to it (9a05aa3b added Core cookie/Basic auth after the tag). Upstream ships no linux/arm64 binary and
# no image. A cold build takes a few minutes:
#   docker build -t nltg-spike-rbitcoin:9dd7ef99 test/Docker/rbitcoin
# Native architecture (avoids the x86 binary under emulation on OrbStack). Toolchain as the commit's rust-toolchain.toml (1.95).
FROM rust:1.95-bookworm AS build
ARG RBITCOIN_COMMIT=9dd7ef99
# Keep the cold proof build within the shared worker CPU/RAM budget.
ARG CARGO_BUILD_JOBS=2
RUN apt-get update && apt-get install -y --no-install-recommends clang git \
    && rm -rf /var/lib/apt/lists/*
RUN git clone https://github.com/reardencode/rbitcoin.git /src && cd /src && git checkout ${RBITCOIN_COMMIT}
WORKDIR /src
RUN cargo build --release --locked --jobs ${CARGO_BUILD_JOBS} -p rbitcoin-node -p rbitcoin-cli \
    && cp target/release/rbitcoin-node /rbitcoin-node && cp target/release/rbitcoin-cli /rbitcoin-cli

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /rbitcoin-node /usr/local/bin/rbitcoin-node
COPY --from=build /rbitcoin-cli /usr/local/bin/rbitcoin-cli
EXPOSE 18443 18444
ENTRYPOINT ["/usr/local/bin/rbitcoin-node"]
