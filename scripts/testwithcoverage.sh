#!/bin/bash

# Runs every test project under test/ (Docker tests excluded) with coverage.
# Build first with the same configuration: dotnet build -c <config> -p:MSBuildWarningsAsMessages=MSB4121
# Usage: scripts/testwithcoverage.sh [config]   (default: Debug)

# config
config=${1:-"Debug"}

# root of the repository (independent of the caller's working directory)
root_dir=$(cd "$(dirname "$0")/.." && pwd)

# Delete coverage directory
rm -rf "$root_dir"/coverage

# Initialize a flag to capture any test failure
any_fail=0

# Loop over test projects (test/<Name>.Tests/<Name>.Tests.csproj; BlazorTests live one level deeper and are skipped)
for project in "$root_dir"/test/*/*.Tests.csproj
do
  project_dir=$(dirname "$project")
  project_name=$(basename "$project" .csproj)

  settings=()
  if [ -f "$project_dir/coverlet.runsettings" ]; then
    settings=(--settings "$project_dir/coverlet.runsettings")
  fi

  echo "Running tests in $project_name"
  # No fixed TRX LogFileName: with SDK 11 each project also runs on net11.0, and a fixed name would let one
  # framework's TRX overwrite the other's. The default name is unique per run.
  # Add this when running Docker tests
  # export HOST_ADDRESS=$(ip route | awk 'NR==1 {print $3}')
  dotnet test "$project" -c "$config" --filter 'FullyQualifiedName!~Docker' "${settings[@]}" --no-build --verbosity normal -l "console;verbosity=detailed" --collect:"XPlat Code Coverage" --logger trx --results-directory "$root_dir"/coverage

  # Capture the exit code
  exit_code=$?

  # Check if the test run was successful
  if [ $exit_code -ne 0 ]; then
    echo "Tests failed in $project_name"
    any_fail=1
  fi
done

# Exit with an error if any tests failed
if [ $any_fail -ne 0 ]; then
  echo "Some tests failed. Exiting with status 1."
  exit 1
fi

echo "All tests passed successfully."
