# C Tor for the NLightning Tor interop proof (NL-572): the client (SOCKS5 port), the control port our node registers
# its onion service on, and the peer's onion service (HiddenServiceDir, the peer shares this container's network
# namespace and listens on 127.0.0.1:9735). Needs access to the public Tor network. The fixture
# (test/NLightning.Integration.Tests/Fixtures/Tor/TorInteropFixture.cs, the one Docker fixture left) builds it as nltg-tor:<TorImageTag> when the tag is
# missing; a bump is a deliberate change of the base image and the tag.
FROM alpine:3.22
RUN apk add --no-cache tor
COPY entrypoint.sh /entrypoint.sh
EXPOSE 9050 9051
ENTRYPOINT ["/bin/sh", "/entrypoint.sh"]
