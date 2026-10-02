#!/bin/bash
# Regenerates the EF Core compiled models of NLightningDbContext, one per provider (NL-708), into
#   CompiledModels/{Sqlite,Postgres,SqlServer}/ of NLightning.Infrastructure.Persistence.
#
# A NativeAOT build cannot build an EF Core model at run time, so it uses these (CompiledModelCatalog, wired in
# DependencyInjection). The JIT build builds its model from OnModelCreating unless Database:UseCompiledModel is true.
# CompiledModelTests (Integration.Tests, non-Docker) fails when a compiled model differs from the runtime model, so
# run this script after every model change; add_migration.sh runs it first, so a migration never leaves them stale.
#
#   scripts/optimize_model.sh                 regenerate the three compiled models
#   scripts/optimize_model.sh --check-queries also try to precompile the repositories' queries for SQLite (into a
#                                             throwaway folder) and summarize why EF cannot (the NL-708 residue)
#
# No database is needed: building a model only needs the provider, so the connection strings are placeholders.
# The startup project is NLightning.Infrastructure.Persistence.Design (the design-time factory and the services the
# generator needs for our value objects); NLightningContextFactory picks the provider from the NLIGHTNING_* variable.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

check_queries=false
[[ "${1:-}" == "--check-queries" ]] && check_queries=true

# The projects multi-target (net10.0, plus net11.0 when built with SDK 11), so dotnet ef needs one framework.
Framework=${EF_FRAMEWORK:-net10.0}
DesignProject='../NLightning.Infrastructure.Persistence.Design'
unset NLIGHTNING_POSTGRES NLIGHTNING_SQLITE NLIGHTNING_SQLSERVER

# The old models go first (a removed entity's files would otherwise stay), and the context is loaded from a build
# without them (NltgNoCompiledModels): after a model change they may no longer compile
for provider in Sqlite Postgres SqlServer; do
  rm -f CompiledModels/"$provider"/*.cs
done
echo "Building the design-time project without compiled models (Debug, $Framework)..."
dotnet build -c Debug "$DesignProject" --framework "$Framework" -p:NltgNoCompiledModels=true \
  -p:MSBuildWarningsAsMessages=MSB4121 -nologo -v quiet

generate() {
  local provider="$1" variable="$2" connection="$3"
  echo "Compiled model: $provider"
  env "$variable=$connection" dotnet ef dbcontext optimize --no-build --framework "$Framework" \
    --project . --startup-project "$DesignProject" \
    --output-dir "CompiledModels/$provider" --namespace "NLightning.Infrastructure.Persistence.CompiledModels.$provider" \
    --nativeaot
  # The assembly attribute makes EF pick this model for every NLightningDbContext, whatever its provider; with three
  # models in one assembly each is chosen explicitly instead (CompiledModelCatalog, UseModel)
  rm -f "CompiledModels/$provider/NLightningDbContextAssemblyAttributes.cs"
}

generate Sqlite NLIGHTNING_SQLITE 'Data Source=:memory:'
generate Postgres NLIGHTNING_POSTGRES 'Host=localhost;Database=nlightning'
generate SqlServer NLIGHTNING_SQLSERVER 'Server=localhost;Database=nlightning'

# The generator writes a BOM and CRLF in places; the repository wants plain UTF-8 and LF (.editorconfig)
find CompiledModels -name '*.cs' -exec perl -0777 -pi -e 's/\A\xEF\xBB\xBF//; s/\r\n/\n/g' {} +

# EF writes a new random modelId every time; when nothing else changed, keep the committed one so a regeneration
# without a model change leaves the tree clean
for provider in Sqlite Postgres SqlServer; do
  dir="CompiledModels/$provider"
  git rev-parse --is-inside-work-tree > /dev/null 2>&1 || break
  changed="$(git status --porcelain -- "$dir")"
  if [[ "$(wc -l <<< "$changed")" -eq 1 && "$changed" == " M "*"/NLightningDbContextModelBuilder.cs" ]] \
     && git diff --quiet -I 'modelId: new Guid' -- "$dir"; then
    git checkout -q -- "$dir/NLightningDbContextModelBuilder.cs"
  fi
done

echo "Building with the new compiled models..."
dotnet build -c Debug . --framework "$Framework" -p:MSBuildWarningsAsMessages=MSB4121 -nologo -v quiet

if $check_queries; then
  # Query precompilation (dotnet ef dbcontext optimize --precompile-queries) is all or nothing: one query it cannot
  # translate and nothing is written. It also writes provider-specific interceptors into the repositories' assembly,
  # so one build could only ever carry one provider's. See NL-708 for the residue this prints.
  out="$(mktemp -d "${TMPDIR:-/tmp}/nltg-precompiled-queries.XXXXXX")"
  log="$out/precompile.log"
  echo "Precompiling the repositories' queries for SQLite into $out (EF 10: experimental)..."
  if env NLIGHTNING_SQLITE='Data Source=:memory:' dotnet ef dbcontext optimize --framework "$Framework" \
       --project ../NLightning.Infrastructure.Repositories --startup-project "$DesignProject" \
       --context NLightningDbContext --output-dir "$out/generated" --no-scaffold --precompile-queries --nativeaot \
       > "$log" 2>&1; then
    echo "Every query precompiled: $(find "$out/generated" -name '*.cs' | wc -l) files in $out/generated"
  else
    # EF lists each failing query more than once; count them by their source text
    python3 - "$log" <<'PY'
import collections, re, sys
text = open(sys.argv[1]).read()
errors = re.findall(r'QueryPrecompilationError \{ SyntaxNode = (.*?), Exception = ([A-Za-z.]+): ([^\n]*)', text, re.S)
unique = {(re.sub(r'\s+', ' ', node), kind, message) for node, kind, message in errors}
reasons = collections.Counter(
    re.sub(r"(ParameterSymbol: |identifier name ).*", r"\1...", f"{kind}: {message}").rstrip(' }') for _, kind, message in unique)
print(f"Query precompilation failed: {len(unique)} distinct queries refused")
for reason, count in reasons.most_common():
    print(f"  {count:4} {reason}")
PY
    echo "Log: $log"
    exit 1
  fi
fi
