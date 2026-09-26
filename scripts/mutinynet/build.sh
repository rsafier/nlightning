#!/usr/bin/env bash
# Builds the daemon and the CLI used by the Mutinynet scripts.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/env.sh"
for project in NLightning.Daemon NLightning.Client; do
    dotnet build "$repo_root/src/$project" -c "$NLTG_BUILD" -f "$NLTG_FRAMEWORK" -p:MSBuildWarningsAsMessages=MSB4121
done
