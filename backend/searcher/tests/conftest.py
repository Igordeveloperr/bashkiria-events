import os
from datetime import datetime, timedelta
from decimal import Decimal
from pathlib import Path
from uuid import uuid4
from zoneinfo import ZoneInfo

import pytest
from alembic import command
from alembic.config import Config
from fastapi.testclient import TestClient
from pydantic import SecretStr
from sqlalchemy import create_engine, text
from sqlalchemy.orm import Session

from searcher.config import Settings
from searcher.main import create_app
from searcher.models import Event, EventSource
from searcher.queries import normalize_title

ZONE = ZoneInfo("Asia/Yekaterinburg")
START = datetime(2035, 1, 10, 18, tzinfo=ZONE)
DB_DIR = Path(__file__).resolve().parents[2] / "db"


@pytest.fixture(scope="session")
def migrated_database():
    raw_url = os.getenv("TEST_DATABASE_URL")
    if not raw_url:
        pytest.skip("Set TEST_DATABASE_URL for PostgreSQL integration tests")
    base_settings = Settings(_env_file=None, database_url=SecretStr(raw_url))
    base_url = base_settings.sqlalchemy_url()
    admin = create_engine(base_url, connect_args={"connect_timeout": 5})
    schema = "searcher_test_" + uuid4().hex
    with admin.begin() as connection:
        connection.execute(text(f'CREATE SCHEMA "{schema}"'))
    url = base_url.update_query_dict({"options": f"-csearch_path={schema},public"})
    engine = create_engine(url, connect_args={"connect_timeout": 5})
    settings = Settings(
        _env_file=None,
        database_url=SecretStr(url.render_as_string(hide_password=False)),
    )
    try:
        config = Config(str(DB_DIR / "alembic.ini"))
        with engine.begin() as connection:
            # Don't adopt the application's public.alembic_version in a test schema.
            connection.execute(text(f'SET LOCAL search_path TO "{schema}"'))
            config.attributes["connection"] = connection
            command.upgrade(config, "head")
        yield engine, settings
    finally:
        engine.dispose()
        # Only the randomly named schema created above is removed.
        with admin.begin() as connection:
            connection.execute(text(f'DROP SCHEMA "{schema}" CASCADE'))
        admin.dispose()


@pytest.fixture
def database(migrated_database):
    engine, settings = migrated_database
    yield engine, settings
    with engine.begin() as connection:
        connection.execute(
            text(
                "TRUNCATE parser_event_sources, parser_event_audit, parser_events, parser_raw_documents CASCADE"
            )
        )


@pytest.fixture
def client(database):
    with TestClient(create_app(database[1])) as client:
        yield client


@pytest.fixture
def event_factory(database):
    def create(
        *,
        start=START,
        end=START + timedelta(hours=2),
        source_count=1,
        source_id="gkz",
        **overrides,
    ):
        event_id = uuid4()
        fields = dict(
            id=event_id,
            dedup_key=event_id.hex,
            title="Джазовый вечер",
            normalized_title="джазовый вечер",
            category="Concert",
            tags=["джаз", "музыка"],
            start_date=start.date() if start else None,
            start_time=start.timetz().replace(tzinfo=None) if start else None,
            end_date=end.date() if end else None,
            end_time=end.timetz().replace(tzinfo=None) if end else None,
            date_start=start,
            date_end=end,
            duration_min=120 if start and end else None,
            city="Уфа",
            venue="ГКЗ Башкортостан",
            address="ул. Ленина, 50",
            latitude=Decimal("54.7351"),
            longitude=Decimal("55.9587"),
            description="Музыканты исполняют джазовые композиции.",
            price_min=Decimal("500.00"),
            price_max=Decimal("1500.00"),
            is_free=False,
            age_limit=6,
            language="Русский",
            org_link="https://example.org/tickets",
            image_url="https://example.org/image.jpg",
            status="Published",
            source_status="Scheduled",
            confidence=Decimal("0.950"),
            issues_json=[{"code": "fixture", "message": "Полный JSON"}],
            data_hash="a" * 64,
            source_priority=100,
            verified_at=START - timedelta(days=1),
            created_at=START - timedelta(days=2),
            updated_at=START - timedelta(days=1),
            reviewed_at=START - timedelta(days=1),
            review_note="Проверено",
            version=3,
            manually_suppressed=False,
        )
        fields.update(overrides)
        if "title" in overrides and "normalized_title" not in overrides:
            fields["normalized_title"] = normalize_title(fields["title"])
        with Session(database[0]) as session:
            session.add(Event(**fields))
            session.flush()
            for index in range(source_count):
                raw_id = uuid4()
                session.execute(
                    text(
                        "INSERT INTO parser_raw_documents (id, source_id, url, fetched_at, http_status, content, content_hash) "
                        "VALUES (:id, :source, 'https://example.org/event', :at, 200, '<html/>', :hash)"
                    ),
                    {"id": raw_id, "source": source_id, "at": START, "hash": "b" * 64},
                )
                session.add(
                    EventSource(
                        id=uuid4(),
                        identity_key=uuid4().hex,
                        source_id=source_id,
                        external_id=f"{event_id}-{index}",
                        source_url=f"https://example.org/events/{event_id}/{index}",
                        data_hash="c" * 64,
                        event_id=event_id,
                        raw_document_id=raw_id,
                        checked_at=START,
                        candidate_json={
                            "title": fields["title"],
                            "extra": {"preserved": [1, 2, 3]},
                        },
                    )
                )
            session.commit()
        return str(event_id)

    return create
