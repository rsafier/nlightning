# Second's captaind (the Bark ASP server) for the cluster harness's Bark proof (NL-1148, Docker/Bark/BarkAspFlowTests).
# Also carries the bark wallet CLI (`bark`) of the same commit for the wallet-driven proofs (wave C).
# Built from source at the commit the vendored captaind.template.toml comes from; a cold build takes 10-20 min:
#   docker build -t nltg-captaind:latest test/Docker/captaind
# Native architecture (the upstream release image is amd64 only). The harness supplies its own PostgreSQL and bitcoind.
FROM rust:1-bookworm AS build
ARG BARK_COMMIT=2c5f0fcb
RUN apt-get update && apt-get install -y --no-install-recommends protobuf-compiler libpq-dev clang git \
    && rm -rf /var/lib/apt/lists/*
RUN git clone https://github.com/ark-bitcoin/bark.git /src && cd /src && git checkout ${BARK_COMMIT}
WORKDIR /src
RUN cargo build --release --locked --bin captaind && cp target/release/captaind /captaind
# The bark wallet CLI of the same commit (the paying and receiving client of the Bark proofs, NL-1148 wave C): its own
# layer so the captaind layer above stays cached; bundled SQLite, no swagger UI download
RUN cargo build --release --locked -p bark-cli --bin bark --no-default-features \
      --features "tls-native-roots sqlite-bundled" && cp target/release/bark /bark

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends libpq5 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /captaind /usr/local/bin/captaind
COPY --from=build /bark /usr/local/bin/bark
COPY start.sh /usr/local/bin/start.sh
RUN chmod a+x /usr/local/bin/start.sh
EXPOSE 3535
CMD ["/usr/local/bin/start.sh"]
