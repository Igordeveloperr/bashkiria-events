# Parser service

ASP.NET Core service for collecting cultural event source pages. Normalized
candidates are not yet saved or published. Migrations belong exclusively to
`../db` (Alembic); this service never creates or migrates the database schema.

## Local startup

From the repository root, start PostgreSQL and apply migrations:

```powershell
docker compose up -d postgres
docker compose run --rm --build migrate
```

Provide a Npgsql connection string in the environment before starting the parser:

```powershell
$env:ConnectionStrings__ParserDatabase = 'Host=localhost;Port=5432;Database=bashkiria;Username=bashkiria;Password=bashkiria;Timeout=5;Command Timeout=10'
dotnet run --project backend/parser/parser --launch-profile http
```

The credentials above match Compose's public development defaults. If you changed
`POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` or `POSTGRES_PORT`, update the
connection string accordingly. Actual credentials must stay outside tracked files.
`.env.example` documents the setting; ASP.NET Core does not automatically load
`.env` files. Environment variables or .NET User Secrets can provide the setting.

## Raw responses

Every completed source download is saved in `parser_raw_documents` before HTTP
success checking or HTML parsing. The row contains a UUID, source ID, URL, UTC
fetch timestamp, HTTP status, decoded response text and its SHA-256 (UTF-8 bytes
of that text). This is decoded content, not the original compressed network bytes.
Even 4xx/5xx response bodies are saved. Network failures without a completed body
are reported as download errors and do not produce invented documents.

Each fetch produces a new row, including unchanged content: the hash is not unique.
Retrying the same document UUID is idempotent. Data is appended; the parser has no
automatic deletion or retention cleanup at this stage.

A parser error does not remove its source row. Database failures stop processing
and return HTTP 503; collection does not continue without durable source data.
The pure POST HTML preview and `/health` do not require PostgreSQL. `/health` is a
liveness check, not an assertion that the database is ready.

## Development endpoints

All preview endpoints are available only in Development:

- `GET /parser/preview/gkz/links`: download listing, save it, return unique card links.
- `GET /parser/preview/gkz`: download and save one fixed card, return candidates.
- `GET /parser/preview/gkz/collect`: save listing and up to three sequential cards.
- `POST /parser/preview`: parse supplied HTML without downloading or saving it.

Collection returns `listingDocumentId`; downloaded candidates contain
`rawDocumentId`. These UUIDs identify the stored source used for parsing. Source
schedule conflicts remain marked for review; raw persistence does not publish them.
GET previews currently perform collection as a development convenience, not as a
production job API.

## Verification

```powershell
dotnet test backend/parser/parser.slnx
```

Tests cover saved schedule fixtures and HTTP/storage ordering with simulated
responses and storage failures. Database migration integration tests are described
in `../db/README.md` and use disposable schemas.
