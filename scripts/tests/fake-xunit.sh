#!/usr/bin/env bash
# A stand-in for the xunit v3 test assembly, for scripts/tests/run-cluster-tests.sh only (NLTG_RUN_CLUSTER_FAKE_TESTS).
# Takes the runner's arguments (-xml <path>, -class <c>, -parallel <p>, ...), writes an xunit v3-shaped result file
# with two classes per suite (Fake.<suite>.A and .B; only the -class one on a rerun) and prints the harness's log lines.
# Behaviour per suite from FAKE_BEHAVIOR_<suite>: pass (default), flaky:<A|B> (fails on the suite's first run only),
# broken:<A|B> (fails every time), manyfail (both classes fail), fixture:<A|B> (the class's fixture throws on the
# suite's first run only: every test of it fails with xunit v3's fixture message), empty (no test matched: total 0,
# exit 0), hang (sleeps until killed), stubborn (ignores TERM and runs until KILLed), crash (exit 3, no XML).
# Records every run in $FAKE_DIR/args.log and checks the runner's admission: while it runs, the run namespaces of all
# fakes (FAKE_WEIGHTS="suite=n ...") must stay within FAKE_BUDGET and the fakes within FAKE_JOBS, else it appends to
# $FAKE_DIR/violations.
set -euo pipefail

xml=""
classes=()
args=("$@")
while [[ $# -gt 0 ]]; do
  case "$1" in
    -xml) xml="$2"; shift 2 ;;
    -class) classes+=("$2"); shift 2 ;;
    *) shift ;;
  esac
done

id="${NLTG_TEST_RUN_ID:?}"
# <batch>-<suite>[-r<n>]
base="${id%-r[0-9]*}"
suite="${base##*-}"
rerun=0
[[ "$base" != "$id" ]] && rerun=1
weight=1
for pair in ${FAKE_WEIGHTS:-}; do
  [[ "${pair%%=*}" == "$suite" ]] && weight="${pair#*=}"
done

echo "$id backend=${NLTG_TEST_BACKEND:-} wait=${NLTG_WAIT_NAMESPACE_DELETION:-} ${args[*]}" >> "$FAKE_DIR/args.log"
mkdir -p "$FAKE_DIR/running"
marker="$FAKE_DIR/running/$id.$weight"
touch "$marker"
trap 'rm -f "$marker"' EXIT
total_weight=0
count=0
for f in "$FAKE_DIR"/running/*; do
  [[ -e "$f" ]] || continue
  total_weight=$((total_weight + ${f##*.}))
  count=$((count + 1))
done
if (( total_weight > ${FAKE_BUDGET:-6} || count > ${FAKE_JOBS:-6} )); then
  echo "$id: $count fakes, $total_weight namespaces" >> "$FAKE_DIR/violations"
fi

echo "[nltg-cluster] run $id: namespace nltg-spike-$id created"
echo "[fixture] Fake fixture (Cluster) ready in 1.5 s"

behavior_var="FAKE_BEHAVIOR_$suite"
behavior="${!behavior_var:-pass}"
case "$behavior" in
  hang) rm -f "$marker"; trap - EXIT; exec sleep 600 ;;
  stubborn) rm -f "$marker"; trap - EXIT; trap '' TERM; while :; do sleep 1; done ;;
  crash) exit 3 ;;
  empty)
    echo '<?xml version="1.0" encoding="utf-8"?><assemblies schema-version="3"><assembly name="fake.dll" total="0" passed="0" failed="0" skipped="0" not-run="0" errors="0"></assembly></assemblies>' > "$xml"
    exit 0 ;;
esac
sleep "${FAKE_SLEEP:-3}"

if (( ${#classes[@]} == 0 )); then classes=("Fake.$suite.A" "Fake.$suite.B"); fi
failing=()
case "$behavior" in
  flaky:*) (( rerun )) || failing=("Fake.$suite.${behavior#flaky:}") ;;
  broken:*) failing=("Fake.$suite.${behavior#broken:}") ;;
  manyfail) failing=("Fake.$suite.A" "Fake.$suite.B") ;;
  fixture:*) (( rerun )) || failing=("Fake.$suite.${behavior#fixture:}") ;;
esac

tests=""
total=0
failed=0
for class in "${classes[@]}"; do
  for method in First Second; do
    total=$((total + 1))
    result=Pass
    failure=""
    for f in ${failing[@]+"${failing[@]}"}; do
      if [[ "$f" == "$class" ]]; then
        result=Fail
        if [[ "$behavior" == fixture:* ]]; then
          failure="<failure exception-type=\"Xunit.Sdk.TestPipelineException\"><message>Collection fixture type 'Fake.Fixture' threw in InitializeAsync
---- System.TimeoutException : no slot</message></failure>"
        else
          failure="<failure exception-type=\"Xunit.Sdk.TrueException\"><message>$method went wrong
second line</message></failure>"
        fi
      fi
    done
    [[ "$result" == Fail ]] && failed=$((failed + 1))
    tests+="<test name=\"$class.$method\" type=\"$class\" method=\"$method\" result=\"$result\">$failure</test>"
  done
done
cat > "$xml" << EOF
<?xml version="1.0" encoding="utf-8"?><assemblies schema-version="3"><assembly name="fake.dll" total="$total" passed="$((total - failed))" failed="$failed" skipped="0" not-run="0" errors="0"><collection name="fake">$tests</collection></assembly></assemblies>
EOF
(( failed == 0 ))