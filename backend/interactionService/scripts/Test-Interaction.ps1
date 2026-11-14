[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$schema = 'interaction_test_' + [guid]::NewGuid().ToString('N')
$previousConnection = $env:INTERACTION_TEST_CONNECTION_STRING
$testExit = 1
Push-Location $repoRoot
try {
    docker compose up -d postgres
    if ($LASTEXITCODE -ne 0) { throw 'Could not start PostgreSQL.' }
    docker compose build migrate
    if ($LASTEXITCODE -ne 0) { throw 'Could not build migration image.' }

    $setup = @'
import os, re, psycopg
from psycopg import sql
from sqlalchemy import create_engine
from sqlalchemy.pool import NullPool
from alembic.config import Config
from alembic import command
schema = os.environ['INTERACTION_TEST_SCHEMA']
assert re.fullmatch('interaction_test_[a-f0-9]{32}', schema)
args = dict(host=os.environ['PGHOST'], port=os.environ['PGPORT'], user=os.environ['PGUSER'], password=os.environ['PGPASSWORD'], dbname=os.environ['PGDATABASE'])
with psycopg.connect(**args, autocommit=True) as connection:
    connection.execute(sql.SQL('CREATE SCHEMA {}').format(sql.Identifier(schema)))
engine = create_engine('postgresql+psycopg://', creator=lambda: psycopg.connect(**args, options='-csearch_path=' + schema), poolclass=NullPool)
config = Config('alembic.ini')
with engine.connect() as connection:
    config.attributes['connection'] = connection
    command.upgrade(config, 'head')
engine.dispose()
print('Disposable test schema migrated')
'@
    docker compose run --rm -e "INTERACTION_TEST_SCHEMA=$schema" migrate python -c $setup
    if ($LASTEXITCODE -ne 0) { throw 'Test schema migration failed.' }

    $compose = docker compose config --format json | ConvertFrom-Json
    $database = $compose.services.postgres.environment
    $connection = [System.Data.Common.DbConnectionStringBuilder]::new()
    $connection['Host'] = 'localhost'
    $connection['Port'] = [string]$compose.services.postgres.ports[0].published
    $connection['Database'] = [string]$database.POSTGRES_DB
    $connection['Username'] = [string]$database.POSTGRES_USER
    $connection['Password'] = [string]$database.POSTGRES_PASSWORD
    $connection['Search Path'] = $schema
    $env:INTERACTION_TEST_CONNECTION_STRING = $connection.ConnectionString

    dotnet test backend/interactionService/interactionService.slnx -p:UseAppHost=false
    $testExit = $LASTEXITCODE
} finally {
    $cleanup = @'
import os, re, psycopg
from psycopg import sql
schema = os.environ['INTERACTION_TEST_SCHEMA']
assert re.fullmatch('interaction_test_[a-f0-9]{32}', schema)
with psycopg.connect(host=os.environ['PGHOST'], port=os.environ['PGPORT'], user=os.environ['PGUSER'], password=os.environ['PGPASSWORD'], dbname=os.environ['PGDATABASE'], autocommit=True) as connection:
    connection.execute(sql.SQL('DROP SCHEMA IF EXISTS {} CASCADE').format(sql.Identifier(schema)))
print('Disposable test schema removed')
'@
    docker compose run --rm -e "INTERACTION_TEST_SCHEMA=$schema" migrate python -c $cleanup
    $env:INTERACTION_TEST_CONNECTION_STRING = $previousConnection
    Pop-Location
}
if ($testExit -ne 0) { throw 'Interaction tests failed.' }
