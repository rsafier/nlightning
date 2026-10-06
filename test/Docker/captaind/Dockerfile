# Second's captaind (the Bark ASP server) for the cluster harness's Bark proof (NL-1148, Docker/Bark/BarkAspFlowTests).
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

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends libpq5 ca-certificates \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /captaind /usr/local/bin/captaind
COPY start.sh /usr/local/bin/start.sh
RUN chmod a+x /usr/local/bin/start.sh
EXPOSE 3535
CMD ["/usr/local/bin/start.sh"]
