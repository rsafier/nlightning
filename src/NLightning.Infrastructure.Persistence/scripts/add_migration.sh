#!/bin/bash

MigrationName=$1

# The projects multi-target (net10.0, plus net11.0 when built with SDK 11), so dotnet ef needs one framework.
# EF Core is on 10.x, which serves net10.0; keep the migrations tooling on it.
Framework=${EF_FRAMEWORK:-net10.0}

echo "Building projects first..."
dotnet build ../NLightning.Infrastructure.Persistence.Postgres --framework "$Framework"
dotnet build ../NLightning.Infrastructure.Persistence.Sqlite --framework "$Framework"
dotnet build ../NLightning.Infrastructure.Persistence.SqlServer --framework "$Framework"

echo "Postgres"
export NLIGHTNING_POSTGRES=${NLIGHTNING_POSTGRES:-'User ID=superuser;Password=superuser;Server=localhost;Port=15432;Database=nlightning;'}
unset NLIGHTNING_SQLITE
unset NLIGHTNING_SQLSERVER
dotnet ef database update --framework "$Framework"
dotnet ef migrations add $MigrationName --framework "$Framework" \
  --project ../NLightning.Infrastructure.Persistence.Postgres
dotnet ef database update --framework "$Framework"

echo "Sqlite"
unset NLIGHTNING_POSTGRES
export NLIGHTNING_SQLITE=${NLIGHTNING_SQLITE:-'Data Source=./nltg.db;Cache=Shared'}
dotnet ef database update --framework "$Framework"
dotnet ef migrations add $MigrationName --framework "$Framework" \
  --project ../NLightning.Infrastructure.Persistence.Sqlite
dotnet ef database update --framework "$Framework"

echo "SqlServer"
unset NLIGHTNING_POSTGRES
unset NLIGHTNING_SQLITE
export NLIGHTNING_SQLSERVER=${NLIGHTNING_SQLSERVER:-'Server=localhost;Database=nlightning;User Id=sa;Password=Superuser1234*;Encrypt=false;'}
dotnet ef database update --framework "$Framework"
dotnet ef migrations add $MigrationName --framework "$Framework" \
  --project ../NLightning.Infrastructure.Persistence.SqlServer
dotnet ef database update --framework "$Framework"

# Clean up
unset NLIGHTNING_POSTGRES
unset NLIGHTNING_SQLITE
unset NLIGHTNING_SQLSERVER