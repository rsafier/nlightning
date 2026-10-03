# LND for the NLightning integration tests (the LND regtest network, NL-768). The cluster harness runs it as
# custom_lnd:0.21.4-beta with pull policy Never (ImageVersions.Lnd; the only backend since NL-820) and never builds it:
# build it once with
#   docker build -t custom_lnd:0.21.4-beta test/Docker/custom_lnd
# (OrbStack's cluster shares the Docker image store). Bump LND_VERSION, LndImageTag and ImageVersions.Lnd together.
FROM debian:stable-slim

ARG LND_VERSION=0.21.4-beta
ENV PATH=/opt/lnd:$PATH

RUN apt-get update -y \
  && apt-get install -y curl gosu wait-for-it \
  && apt-get clean \
  && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*

RUN SYS_ARCH="$(dpkg --print-architecture)" \
  && curl -SLO https://github.com/lightningnetwork/lnd/releases/download/v${LND_VERSION}/lnd-linux-${SYS_ARCH}-v${LND_VERSION}.tar.gz \
  && tar -xzf *.tar.gz \
  && mkdir /opt/lnd \
  && mv ./lnd-linux-${SYS_ARCH}-v${LND_VERSION}/* /opt/lnd \
  && rm *.tar.gz

RUN curl -SLO https://raw.githubusercontent.com/lightningnetwork/lnd/master/contrib/lncli.bash-completion \
  && mkdir -p /etc/bash_completion.d \
  && mv lncli.bash-completion /etc/bash_completion.d/ \
  && curl -SLO https://raw.githubusercontent.com/scop/bash-completion/master/bash_completion \
  && mv bash_completion /usr/share/bash-completion/

COPY docker-entrypoint.sh /entrypoint.sh
COPY bashrc /home/lnd/.bashrc

RUN chmod a+x /entrypoint.sh

VOLUME ["/home/lnd/.lnd"]

EXPOSE 9735 8080 10000

ENTRYPOINT ["/entrypoint.sh"]

CMD ["lnd"]