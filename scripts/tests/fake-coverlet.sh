#!/usr/bin/env bash
# A stand-in for coverlet.console, for scripts/tests/run-cluster-tests.sh only (run-cluster.sh --coverage with
# NLTG_COVERLET): takes coverlet's arguments (<assembly> --target <t> --targetargs <a> --output <file> ...), records the
# assembly in $FAKE_DIR/coverlet.log, runs the target, writes a small Cobertura file and exits as coverlet does (0 when
# the target exited 0, 1 otherwise).
set -uo pipefail

assembly="$1"
shift
target=""
targetargs=""
output=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --target) target="$2"; shift 2 ;;
    --targetargs) targetargs="$2"; shift 2 ;;
    --output) output="$2"; shift 2 ;;
    *) shift ;;
  esac
done

echo "$assembly" >> "$FAKE_DIR/coverlet.log"
eval "$target $targetargs"
code=$?
echo '<?xml version="1.0" encoding="utf-8"?><coverage line-rate="1" branch-rate="1"><packages /></coverage>' > "$output"
if (( code != 0 )); then exit 1; fi
exit 0
