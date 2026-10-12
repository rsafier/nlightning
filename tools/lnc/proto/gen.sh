#!/usr/bin/env bash
# Regenerates the Go message types of the Lightning Terminal protos the bridge
# serves (litrpc) and calls (autopilotserverrpc). The .proto files are copied
# unchanged from lightninglabs/lightning-terminal (MIT, see UPSTREAM_LICENSE);
# only messages are generated: the bridge dispatches methods itself.
# Needs protoc and protoc-gen-go v1.33.0 (the protobuf version in go.mod):
#   GOBIN=/some/bin go install google.golang.org/protobuf/cmd/protoc-gen-go@v1.33.0
set -euo pipefail
cd "$(dirname "$0")"
module=github.com/rsafier/nlightning/tools/lnc
lit=$module/internal/litrpc
ap=$module/internal/autopilotserverrpc
protoc -I litrpc \
  --go_out=.. --go_opt=module=$module \
  --go_opt=Mlit-sessions.proto=$lit --go_opt=Mlit-autopilot.proto=$lit --go_opt=Mfirewall.proto=$lit \
  litrpc/lit-sessions.proto litrpc/lit-autopilot.proto litrpc/firewall.proto
protoc -I autopilotserverrpc \
  --go_out=.. --go_opt=module=$module \
  --go_opt=Mautopilotserver.proto=$ap \
  autopilotserverrpc/autopilotserver.proto
