"""Run Alembic with PostgreSQL credentials, independently of the AI service."""

import os
from logging.config import fileConfig
from pathlib import Path

from alembic import context
from dotenv import dotenv_values
from sqlalchemy import create_engine, pool, text
from sqlalchemy.engine import URL, make_url

config = context.config
if config.config_file_name is not None:
    fileConfig(config.config_file_name, disable_existing_loggers=False)


def database_url():
    raw = os.getenv("DATABASE_URL", "").strip()
    if raw:
        url = make_url(raw)
    elif os.getenv("PGHOST"):
        url = URL.create(
            "postgresql+psycopg",
            username=os.getenv("PGUSER"),
            password=os.getenv("PGPASSWORD"),
            host=os.environ["PGHOST"],
            port=int(os.getenv("PGPORT", "5432")),
            database=os.getenv("PGDATABASE"),
        )
    else:
        # Local CLI supports both the root .env and the existing AI .env.
        db_dir = Path(config.config_file_name).resolve().parent
        for path in (db_dir.parent.parent / ".env", db_dir.parent / "ai" / ".env"):
            raw = dotenv_values(path).get("DATABASE_URL", "")
            if raw:
                break
        if not raw:
            raise RuntimeError("Set DATABASE_URL or PGHOST/PGUSER/PGPASSWORD/PGDATABASE")
        url = make_url(raw)
    if url.drivername in {"postgres", "postgresql"}:
        url = url.set(drivername="postgresql+psycopg")
    if url.drivername != "postgresql+psycopg":
        raise ValueError("Migrations require a PostgreSQL URL using psycopg")
    return url


def run_with_connection(connection):
    # Migrations are explicit SQLAlchemy operations; no ORM metadata is imported.
    context.configure(connection=connection, target_metadata=None)
    with context.begin_transaction():
        # One transaction for both the schema version and all DDL in this run.
        connection.execute(text("SELECT pg_advisory_xact_lock(817451093032)"))
        context.run_migrations()


if context.is_offline_mode():
    context.configure(
        dialect_name="postgresql", target_metadata=None,
        literal_binds=True, dialect_opts={"paramstyle": "named"},
    )
    with context.begin_transaction():
        context.run_migrations()
elif config.attributes.get("connection") is not None:
    run_with_connection(config.attributes["connection"])
else:
    engine = create_engine(database_url(), poolclass=pool.NullPool, connect_args={"connect_timeout": 5})
    try:
        with engine.connect() as connection:
            run_with_connection(connection)
    finally:
        engine.dispose()
