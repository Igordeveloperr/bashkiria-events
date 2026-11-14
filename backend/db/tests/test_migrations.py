"""Exercise real upgrades and legacy adoption in disposable PostgreSQL schemas."""

import hashlib
import os
import subprocess
import sys
import unittest
from pathlib import Path
from uuid import uuid4

import psycopg
from alembic import command
from alembic.config import Config
from psycopg import sql
from psycopg.conninfo import make_conninfo
from sqlalchemy import create_engine
from sqlalchemy.pool import NullPool

DB_DIR = Path(__file__).resolve().parents[1]
INI = DB_DIR / "alembic.ini"
LEGACY = DB_DIR / "legacy" / "001_chat_history.sql"
HEAD_REVISION = "0004_event_search"


class OfflineMigrationTest(unittest.TestCase):
    def test_offline_upgrade_requires_no_database_or_model_credentials(self):
        result = subprocess.run(
            [
                sys.executable,
                "-m",
                "alembic",
                "-c",
                str(INI),
                "upgrade",
                "head",
                "--sql",
            ],
            capture_output=True,
            text=True,
            timeout=20,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
<<<<<<< HEAD
        for name in (
            "chat_sessions",
            "chat_runs",
            "chat_messages",
            "parser_raw_documents",
            "parser_events",
            "parser_event_sources",
            "parser_collection_runs",
            "parser_event_audit",
            "alembic_version",
        ):
=======
        for name in ("chat_sessions", "chat_runs", "chat_messages", "parser_raw_documents", "parser_events", "parser_event_sources", "parser_collection_runs", "parser_event_audit", "parser_cities", "parser_venues", "parser_city_aliases", "parser_venue_aliases", "alembic_version"):
>>>>>>> origin/main
            self.assertIn(f"CREATE TABLE {name}", result.stdout)
        self.assertIn("CREATE EXTENSION IF NOT EXISTS pg_trgm", result.stdout)
        self.assertIn("CREATE INDEX parser_events_search_idx", result.stdout)


@unittest.skipUnless(
    os.getenv("TEST_DATABASE_URL"), "Set TEST_DATABASE_URL for PostgreSQL tests"
)
class MigrationTest(unittest.TestCase):
    def setUp(self):
        self.base_url = os.environ["TEST_DATABASE_URL"]
        self.schema = "migration_test_" + uuid4().hex
        with psycopg.connect(self.base_url, autocommit=True) as connection:
            connection.execute(
                sql.SQL("CREATE SCHEMA {}").format(sql.Identifier(self.schema))
            )
        self.addCleanup(self.drop_schema)
        self.dsn = make_conninfo(self.base_url, options=f"-csearch_path={self.schema}")

    def drop_schema(self):
        with psycopg.connect(self.base_url, autocommit=True) as connection:
            connection.execute(
                sql.SQL("DROP SCHEMA {} CASCADE").format(sql.Identifier(self.schema))
            )

    def migrate(self, *, downgrade=False, revision=None):
        config = Config(str(INI))
        engine = create_engine(
            "postgresql+psycopg://",
            creator=lambda: psycopg.connect(self.dsn),
            poolclass=NullPool,
        )
        try:
            with engine.begin() as connection:
                config.attributes["connection"] = connection
                if downgrade:
                    command.downgrade(config, revision or "base")
                else:
                    command.upgrade(config, "head")
        finally:
            engine.dispose()

    def current(self):
        with psycopg.connect(self.dsn) as connection:
            if not connection.execute(
                "SELECT to_regclass('alembic_version')"
            ).fetchone()[0]:
                return None
            result = connection.execute(
                "SELECT version_num FROM alembic_version"
            ).fetchone()
            return result[0] if result else None

    def legacy_database(self, *, checksum=None, marker=True):
        source = LEGACY.read_text(encoding="utf-8")
        checksum = checksum or hashlib.sha256(source.encode("utf-8")).hexdigest()
        self.session_id = uuid4()
        with psycopg.connect(self.dsn) as connection:
            connection.execute(source)
            if marker:
                connection.execute("""CREATE TABLE ai_schema_migrations (
                        name text PRIMARY KEY, checksum text NOT NULL,
                        applied_at timestamptz NOT NULL DEFAULT now())""")
                connection.execute(
                    "INSERT INTO ai_schema_migrations (name, checksum) VALUES (%s, %s)",
                    (LEGACY.name, checksum),
                )
            connection.execute(
                """INSERT INTO chat_sessions (id, user_id, session_key, next_seq)
                   VALUES (%s, 'legacy-user', 'legacy-chat', 3)""",
                (self.session_id,),
            )
            run_id = uuid4()
            connection.execute(
                """INSERT INTO chat_runs (id, session_id, request_id, status, finished_at)
                   VALUES (%s, %s, %s, 'completed', now())""",
                (run_id, self.session_id, uuid4()),
            )
            for seq, role, content in (
                (1, "user", "original question"),
                (2, "assistant", "original answer"),
            ):
                connection.execute(
                    """INSERT INTO chat_messages (id, session_id, run_id, seq, role, content)
                       VALUES (%s, %s, %s, %s, %s, %s)""",
                    (uuid4(), self.session_id, run_id, seq, role, content),
                )

    def assert_legacy_data_intact(self):
        with psycopg.connect(self.dsn) as connection:
            result = connection.execute(
                "SELECT content FROM chat_messages ORDER BY seq"
            ).fetchall()
            self.assertEqual(result, [("original question",), ("original answer",)])
            result = connection.execute(
                "SELECT id, next_seq FROM chat_sessions"
            ).fetchone()
            self.assertEqual(result, (self.session_id, 3))

    def test_fresh_upgrade_and_repeat(self):
        self.migrate()
<<<<<<< HEAD
        self.assertEqual(self.current(), HEAD_REVISION)
        self.migrate()
        self.assertEqual(self.current(), HEAD_REVISION)
=======
        self.assertEqual(self.current(), "0005_parser_references")
        self.migrate()
        self.assertEqual(self.current(), "0005_parser_references")
>>>>>>> origin/main

    def test_downgrade_and_upgrade(self):
        self.migrate()
        self.migrate(downgrade=True)
        self.assertIsNone(self.current())
        with psycopg.connect(self.dsn) as connection:
<<<<<<< HEAD
            for name in (
                "chat_sessions",
                "chat_runs",
                "chat_messages",
                "parser_raw_documents",
                "parser_events",
                "parser_event_sources",
                "parser_collection_runs",
                "parser_event_audit",
            ):
                self.assertIsNone(
                    connection.execute("SELECT to_regclass(%s)", (name,)).fetchone()[0]
                )
        self.migrate()
        self.assertEqual(self.current(), HEAD_REVISION)
=======
            for name in ("chat_sessions", "chat_runs", "chat_messages", "parser_raw_documents", "parser_events", "parser_event_sources", "parser_collection_runs", "parser_event_audit", "parser_cities", "parser_venues", "parser_city_aliases", "parser_venue_aliases"):
                self.assertIsNone(connection.execute("SELECT to_regclass(%s)", (name,)).fetchone()[0])
        self.migrate()
        self.assertEqual(self.current(), "0005_parser_references")
>>>>>>> origin/main

    def test_legacy_adoption_preserves_messages_and_ids(self):
        self.legacy_database()
        self.migrate()
<<<<<<< HEAD
        self.assertEqual(self.current(), HEAD_REVISION)
=======
        self.assertEqual(self.current(), "0005_parser_references")
>>>>>>> origin/main
        self.assert_legacy_data_intact()
        self.migrate()
        self.assert_legacy_data_intact()

    def test_unknown_legacy_checksum_is_rejected_without_changes(self):
        self.legacy_database(checksum="unknown-checksum")
        with self.assertRaisesRegex(RuntimeError, "checksum"):
            self.migrate()
        self.assertIsNone(self.current())
        self.assert_legacy_data_intact()

    def test_incomplete_legacy_schema_is_rejected_without_changes(self):
        self.legacy_database()
        with psycopg.connect(self.dsn) as connection:
            connection.execute("DROP INDEX chat_messages_user_seq_idx")
        with self.assertRaisesRegex(RuntimeError, "differs"):
            self.migrate()
        self.assertIsNone(self.current())
        self.assert_legacy_data_intact()

    def test_existing_untracked_schema_is_not_silently_stamped(self):
        self.legacy_database(marker=False)
        with self.assertRaisesRegex(RuntimeError, "refusing to stamp"):
            self.migrate()
        self.assertIsNone(self.current())
        self.assert_legacy_data_intact()

    def test_two_migration_processes_are_serialized(self):
        environment = dict(
            os.environ,
            DATABASE_URL=self.base_url,
            PGOPTIONS=f"-csearch_path={self.schema}",
        )
        args = [sys.executable, "-m", "alembic", "-c", str(INI), "upgrade", "head"]
        processes = [
            subprocess.Popen(
                args,
                env=environment,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
            )
            for _ in range(2)
        ]
        try:
            for process in processes:
                output, error = process.communicate(timeout=30)
                self.assertEqual(process.returncode, 0, output + error)
        finally:
            for process in processes:
                if process.poll() is None:
                    process.kill()
                    process.communicate()
<<<<<<< HEAD
        self.assertEqual(self.current(), HEAD_REVISION)
=======
        self.assertEqual(self.current(), "0005_parser_references")
>>>>>>> origin/main

    def test_raw_documents_preserve_history_and_validate_hash(self):
        self.migrate()
        content = "<h1>Тестовая карточка</h1>"
        digest = hashlib.sha256(content.encode("utf-8")).hexdigest()
        insert = """INSERT INTO parser_raw_documents
                    (id, source_id, url, fetched_at, http_status, content, content_hash)
                    VALUES (%s, 'gkz', 'https://example.org/events/fixture/', now(), %s, %s, %s)"""
        with psycopg.connect(self.dsn) as connection:
            # Same content can be fetched again; even unsuccessful HTTP responses are preserved.
            for status in (200, 404):
                connection.execute(insert, (uuid4(), status, content, digest))
            rows = connection.execute(
                "SELECT http_status, content, content_hash, fetched_at FROM parser_raw_documents ORDER BY http_status"
            ).fetchall()
            self.assertEqual([row[0] for row in rows], [200, 404])
            for row in rows:
                self.assertEqual(row[1:3], (content, digest))
                self.assertIsNotNone(row[3].tzinfo)

        with self.assertRaises(psycopg.errors.CheckViolation):
            with psycopg.connect(self.dsn) as connection:
                connection.execute(insert, (uuid4(), 200, content, "invalid-hash"))

    def test_raw_documents_downgrade_preserves_chat_data(self):
        self.legacy_database()
        self.migrate()
        self.migrate(downgrade=True, revision="0001_chat_history")
        self.assertEqual(self.current(), "0001_chat_history")
        self.assert_legacy_data_intact()
        with psycopg.connect(self.dsn) as connection:
            self.assertIsNone(
                connection.execute(
                    "SELECT to_regclass('parser_raw_documents')"
                ).fetchone()[0]
            )
        self.migrate()
<<<<<<< HEAD
        self.assertEqual(self.current(), HEAD_REVISION)

    def test_search_indexes_and_downgrade_preserve_events(self):
        self.migrate()
        event_id = uuid4()
        with psycopg.connect(self.dsn) as connection:
            connection.execute(
                """INSERT INTO parser_events
                   (id, dedup_key, title, normalized_title, category, city, venue, address,
                    description, status, source_status, data_hash, tags, confidence, issues_json,
                    source_priority, verified_at, created_at, updated_at, version, manually_suppressed)
                   VALUES (%s, 'fixture', 'Fixture', 'fixture', 'Other', '', '', '', '', 'Draft',
                           'Unknown', 'fixture', '{}', 0.5, '[]', 1, now(), now(), now(), 1, false)""",
                (event_id,),
            )
            indexes = {
                row[0]
                for row in connection.execute(
                    "SELECT indexname FROM pg_indexes WHERE schemaname = %s",
                    (self.schema,),
                )
            }
            self.assertTrue(
                {
                    "parser_events_search_idx",
                    "parser_events_title_trgm_idx",
                    "parser_events_venue_date_idx",
                    "parser_events_city_category_idx",
                    "parser_event_sources_source_idx",
                }
                <= indexes
            )
        self.migrate(downgrade=True, revision="0003_parser_pipeline")
        self.assertEqual(self.current(), "0003_parser_pipeline")
        with psycopg.connect(self.dsn) as connection:
            self.assertEqual(
                connection.execute("SELECT id, title FROM parser_events").fetchall(),
                [(event_id, "Fixture")],
            )
            self.assertIsNone(
                connection.execute(
                    "SELECT to_regclass('parser_events_search_idx')"
                ).fetchone()[0]
            )
        self.migrate()
        self.assertEqual(self.current(), HEAD_REVISION)
=======
        self.assertEqual(self.current(), "0005_parser_references")

    def test_ingestion_upgrade_preserves_existing_page_documents(self):
        config = Config(str(INI))
        engine = create_engine("postgresql+psycopg://", creator=lambda: psycopg.connect(self.dsn), poolclass=NullPool)
        try:
            with engine.begin() as connection:
                config.attributes["connection"] = connection
                command.upgrade(config, "0003_parser_pipeline")
        finally:
            engine.dispose()
        document_id = uuid4()
        with psycopg.connect(self.dsn) as connection:
            connection.execute("""INSERT INTO parser_raw_documents
                (id, source_id, url, fetched_at, http_status, content, content_hash)
                VALUES (%s,'gkz','https://example.org/event',now(),200,'original',%s)""",
                (document_id, hashlib.sha256(b"original").hexdigest()))
        self.migrate()
        with psycopg.connect(self.dsn) as connection:
            row = connection.execute("SELECT content,http_status,source_type,content_type FROM parser_raw_documents WHERE id=%s", (document_id,)).fetchone()
            self.assertEqual(row, ("original", 200, "Page", "text/html"))

    def test_import_origin_is_explicit_and_cannot_be_silently_downgraded(self):
        self.migrate()
        document_id = uuid4()
        digest = hashlib.sha256(b"[]").hexdigest()
        with psycopg.connect(self.dsn) as connection:
            connection.execute("""INSERT INTO parser_raw_documents
                (id,source_id,url,fetched_at,http_status,content,content_hash,source_type,content_type)
                VALUES (%s,'import:fixture','urn:bashkiria-events:import:fixture',now(),NULL,'[]',%s,'Import','application/json')""",
                (document_id, digest))
        with self.assertRaisesRegex(Exception, "Cannot downgrade ingestion"):
            self.migrate(downgrade=True, revision="0003_parser_pipeline")
        self.assertEqual(self.current(), "0005_parser_references")
        with psycopg.connect(self.dsn) as connection:
            self.assertEqual(connection.execute("SELECT source_type,http_status FROM parser_raw_documents WHERE id=%s", (document_id,)).fetchone(), ("Import", None))
        with self.assertRaises(psycopg.errors.CheckViolation):
            with psycopg.connect(self.dsn) as connection:
                connection.execute("""INSERT INTO parser_raw_documents
                    (id,source_id,url,fetched_at,http_status,content,content_hash,source_type,content_type)
                    VALUES (%s,'gkz','https://example.org/event',now(),NULL,'[]',%s,'Page','text/html')""", (uuid4(), digest))
>>>>>>> origin/main
