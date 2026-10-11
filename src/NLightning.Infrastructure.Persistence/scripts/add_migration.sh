#!/bin/bash

MigrationName=$1

# The projects multi-target (net10.0, plus net11.0 when built with SDK 11), so dotnet ef needs one framework.
# EF Core is on 10.x, which serves net10.0; keep the migrations tooling on it.
Framework=${EF_FRAMEWORK:-net10.0}

# Each provider is both the project and the startup project of its dotnet ef calls (NL-233): the tool then
# builds and runs that provider's own fresh output (Debug, its default configuration) instead of the base
# project's bin, where a stale provider assembly once survived a model change. The design-time factory
# (NLightningContextFactory) is found through the provider's reference to the base project.
PostgresProject='../NLightning.Infrastructure.Persistence.Postgres'
SqliteProject='../NLightning.Infrastructure.Persistence.Sqlite'
SqlServerProject='../NLightning.Infrastructure.Persistence.SqlServer'

# The compiled models first (NL-708): they follow the edited model, and stale ones may not even compile any more,
# which would fail the builds below
echo "Regenerating the compiled models..."
"$(dirname "${BASH_SOURCE[0]}")/optimize_model.sh" || { echo "optimize_model.sh failed; no migration added." >&2; exit 1; }

echo "Building projects first (Debug, the configuration dotnet ef runs)..."
dotnet build -c Debug ../NLightning.Infrastructure.Persistence.Postgres --framework "$Framework"
dotnet build -c Debug ../NLightning.Infrastructure.Persistence.Sqlite --framework "$Framework"
dotnet build -c Debug ../NLightning.Infrastructure.Persistence.SqlServer --framework "$Framework"

echo "Postgres"
export NLIGHTNING_POSTGRES=${NLIGHTNING_POSTGRES:-'User ID=superuser;Password=superuser;Server=localhost;Port=15432;Database=nlightning;'}
unset NLIGHTNING_SQLITE
unset NLIGHTNING_SQLSERVER
dotnet ef database update --framework "$Framework" --project "$PostgresProject" --startup-project "$PostgresProject"
dotnet ef migrations add $MigrationName --framework "$Framework" \
  --project "$PostgresProject" --startup-project "$PostgresProject"
dotnet ef database update --framework "$Framework" --project "$PostgresProject" --startup-project "$PostgresProject"

echo "Sqlite"
unset NLIGHTNING_POSTGRES
export NLIGHTNING_SQLITE=${NLIGHTNING_SQLITE:-'Data Source=./nltg.db;Cache=Shared'}
dotnet ef database update --framework "$Framework" --project "$SqliteProject" --startup-project "$SqliteProject"
dotnet ef migrations add $MigrationName --framework "$Framework" \
  --project "$SqliteProject" --startup-project "$SqliteProject"
dotnet ef database update --framework "$Framework" --project "$SqliteProject" --startup-project "$SqliteProject"

echo "SqlServer"
unset NLIGHTNING_POSTGRES
unset NLIGHTNING_SQLITE
export NLIGHTNING_SQLSERVER=${NLIGHTNING_SQLSERVER:-'Server=localhost;Database=nlightning;User Id=sa;Password=Superuser1234*;Encrypt=false;'}
dotnet ef database update --framework "$Framework" --project "$SqlServerProject" --startup-project "$SqlServerProject"
dotnet ef migrations add $MigrationName --framework "$Framework" \
  --project "$SqlServerProject" --startup-project "$SqlServerProject"
dotnet ef database update --framework "$Framework" --project "$SqlServerProject" --startup-project "$SqlServerProject"

# Clean up
unset NLIGHTNING_POSTGRES
unset NLIGHTNING_SQLITE
unset NLIGHTNING_SQLSERVER