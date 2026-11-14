"""Real PostgreSQL integration tests in an isolated, automatically removed schema."""

import os
from pathlib import Path
import unittest
from unittest.mock import patch
from uuid import uuid4

from chat import ChatRunFailedError, ChatService
from main import build_graph
from storage import ChatStore, Conversation, RequestConflictError, SessionBusyError
from test_history import RecordingModel

import psycopg
from psycopg import sql
from psycopg.conninfo import make_conninfo
from alembic import command
from alembic.config import Config as AlembicConfig
from sqlalchemy import create_engine
from sqlalchemy.pool import NullPool


def migrate(database_url):
    config = AlembicConfig(str(Path(__file__).resolve().parents[2] / "db" / "alembic.ini"))
    engine = create_engine(
        "postgresql+psycopg://", creator=lambda: psycopg.connect(database_url), poolclass=NullPool
    )
    try:
        with engine.begin() as connection:
            config.attributes["connection"] = connection
            command.upgrade(config, "head")
    finally:
        engine.dispose()


@unittest.skipUnless(os.getenv("TEST_DATABASE_URL"), "Set TEST_DATABASE_URL for PostgreSQL tests")
class PostgresChatTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.base_url = os.environ["TEST_DATABASE_URL"]
        cls.schema = "chat_test_" + uuid4().hex
        with psycopg.connect(cls.base_url, autocommit=True) as connection:
            connection.execute(sql.SQL("CREATE SCHEMA {}").format(sql.Identifier(cls.schema)))
        cls.addClassCleanup(cls.drop_schema)
        cls.database_url = make_conninfo(cls.base_url, options=f"-csearch_path={cls.schema}")
        migrate(cls.database_url)

    @classmethod
    def drop_schema(cls):
        with psycopg.connect(cls.base_url, autocommit=True) as connection:
            connection.execute(
                sql.SQL("DROP SCHEMA {} CASCADE").format(sql.Identifier(cls.schema))
            )

    def setUp(self):
        self.user = "user-" + uuid4().hex
        self.key = "chat-001"
        self.application_name = "chat-test-" + uuid4().hex
        self.store = ChatStore(self.database_url, application_name=self.application_name)
        self.model = RecordingModel()
        self.service = ChatService(self.store, build_graph(self.model), history_turns=2)

    def send(self, content, **kwargs):
        return self.service.send_message(
            user_id=kwargs.pop("user_id", self.user),
            session_key=kwargs.pop("session_key", self.key),
            request_id=kwargs.pop("request_id", uuid4()), content=content, **kwargs,
        )

    def test_migration_is_repeatable(self):
        migrate(self.database_url)
        with psycopg.connect(self.database_url) as connection:
            version = connection.execute("SELECT version_num FROM alembic_version").fetchone()
        self.assertEqual(version, ("0001_chat_history",))

    def test_history_survives_new_service_and_window_does_not_delete_messages(self):
        for i in range(8):
            self.send(f"question-{i}")
        reloaded_model = RecordingModel()
        self.service = ChatService(
            ChatStore(self.database_url), build_graph(reloaded_model), history_turns=2
        )
        reply = self.send("question-8")
        self.assertEqual(
            [m.content for m in reloaded_model.calls[0]][1:],
            ["question-6", "Ответ: question-6", "question-7", "Ответ: question-7", "question-8"],
        )
        history = self.store.history(self.user, self.key)
        self.assertEqual(len(history), 18)
        self.assertEqual([m.seq for m in history], list(range(1, 19)))
        self.assertEqual(history[-1].content, reply.content)
        self.assertEqual(history[-1].metadata["usage_metadata"]["total_tokens"], 13)
        recent = self.store.history(self.user, self.key, limit=4)
        previous = self.store.history(self.user, self.key, limit=4, before_seq=recent[0].seq)
        self.assertEqual([m.seq for m in recent], [15, 16, 17, 18])
        self.assertEqual([m.seq for m in previous], [11, 12, 13, 14])

    def test_window_zero_loads_only_current_message(self):
        self.send("first")
        self.service.history_turns = 0
        self.send("second")
        self.assertEqual([m.content for m in self.model.calls[-1]][1:], ["second"])
        self.assertEqual(len(self.store.history(self.user, self.key)), 4)

    def test_request_replay_does_not_generate_or_insert_again(self):
        request_id = uuid4()
        reply = self.send("hello", request_id=request_id)
        replay = self.send("hello", request_id=request_id)
        self.assertTrue(replay.cached)
        self.assertEqual(reply.run_id, replay.run_id)
        self.assertEqual(reply.content, replay.content)
        self.assertEqual(len(self.model.calls), 1)
        self.assertEqual(len(self.store.history(self.user, self.key)), 2)
        with self.assertRaises(RequestConflictError):
            self.send("different text", request_id=request_id)
        self.assertEqual(len(self.store.history(self.user, self.key)), 2)

    def test_same_frontend_key_is_isolated_by_user(self):
        request_id = uuid4()
        first = self.send("first owner", request_id=request_id)
        second_user = self.user + "-other"
        second = self.send("second owner", user_id=second_user, request_id=request_id)
        self.assertNotEqual(first.session_id, second.session_id)
        self.assertEqual([m.content for m in self.model.calls[-1]][1:], ["second owner"])
        self.assertEqual(self.store.sessions(self.user)[0]["id"], first.session_id)
        self.assertEqual(len(self.store.sessions(self.user)), 1)
        self.assertEqual(self.store.history(self.user + "-unknown", self.key), [])
        self.assertIsNone(self.store.run(self.user + "-unknown", self.key, request_id))
        self.assertEqual(self.store.history(self.user, self.key)[0].content, "first owner")

    def test_http_round_trip_persists_history_replays_and_isolates_frontend_ids(self):
        from api import create_app
        from fastapi.testclient import TestClient

        payload = {
            "user_id": self.user, "session_id": self.key,
            "message": "first question", "request_id": str(uuid4()),
        }
        with TestClient(create_app(self.service, cors_origins=[])) as client:
            first = client.post("/api/chat", json=payload)
            self.assertEqual(first.status_code, 200)
            replay = client.post("/api/chat", json=payload)
            self.assertEqual(replay.status_code, 200)
            self.assertTrue(replay.json()["cached"])
            self.assertEqual(replay.json()["run_id"], first.json()["run_id"])
            self.assertEqual(len(self.model.calls), 1)
            conflict = client.post("/api/chat", json={**payload, "message": "changed"})
            self.assertEqual(conflict.status_code, 409)
            self.assertEqual(conflict.json()["detail"]["code"], "request_conflict")

            second = client.post("/api/chat", json={**payload, "request_id": str(uuid4()), "message": "next question"})
            self.assertEqual(second.status_code, 200)
            self.assertEqual(second.json()["session_id"], self.key)
            self.assertEqual([m.content for m in self.model.calls[-1]][1:], [
                "first question", "Ответ: first question", "next question",
            ])

            other = client.post("/api/chat", json={**payload, "user_id": self.user + "-other"})
            self.assertEqual(other.status_code, 200)
            self.assertFalse(other.json()["cached"])
            self.assertEqual([m.content for m in self.model.calls[-1]][1:], ["first question"])
            self.assertEqual(len(self.store.history(self.user, self.key)), 4)

    def test_failed_request_is_not_reexecuted_or_used_as_context(self):
        request_id = uuid4()
        self.model.error = RuntimeError("Provider unavailable")
        with self.assertRaisesRegex(ChatRunFailedError, "Provider unavailable"):
            self.send("failed message", request_id=request_id)
        run = self.store.run(self.user, self.key, request_id)
        self.assertEqual(run["status"], "failed")
        self.assertIsNotNone(run["finished_at"])
        self.model.error = None
        with self.assertRaises(ChatRunFailedError):
            self.send("failed message", request_id=request_id)
        self.assertEqual(len(self.model.calls), 1)
        self.send("new message")
        self.assertEqual([m.content for m in self.model.calls[-1]][1:], ["new message"])
        self.assertEqual(len(self.store.history(self.user, self.key)), 3)

    def test_lock_excludes_parallel_generation_but_allows_another_session(self):
        with self.store.conversation(self.user, self.key):
            with self.assertRaises(SessionBusyError):
                self.send("parallel")
            other = self.send("another dialog", session_key="other")
            self.assertEqual(other.content, "Ответ: another dialog")
        self.send("after release")
        self.assertEqual(len(self.store.history(self.user, self.key)), 2)

    def test_model_call_runs_outside_database_transaction(self):
        def check_transaction():
            with psycopg.connect(self.database_url) as connection:
                activity = connection.execute(
                    "SELECT state, xact_start FROM pg_stat_activity WHERE application_name = %s",
                    (self.application_name,),
                ).fetchall()
            self.assertEqual(activity, [("idle", None)])
            with self.assertRaises(SessionBusyError):
                self.send("overlapping message")
        self.model.on_invoke = check_transaction
        self.send("first")
        self.assertEqual(len(self.model.calls), 1)

    def test_disconnected_worker_becomes_failed_and_does_not_block_session(self):
        request_id = uuid4()
        with self.assertRaisesRegex(RuntimeError, "simulated crash"):
            with self.store.conversation(self.user, self.key) as conversation:
                conversation.begin_run(request_id, "interrupted")
                raise RuntimeError("simulated crash")
        self.assertEqual(self.store.run(self.user, self.key, request_id)["status"], "running")
        with self.assertRaisesRegex(ChatRunFailedError, "Worker disconnected"):
            self.send("interrupted", request_id=request_id)
        self.assertEqual(self.store.run(self.user, self.key, request_id)["status"], "failed")
        self.assertEqual(len(self.model.calls), 0)
        self.send("after crash")
        self.assertEqual([m.content for m in self.model.calls[-1]][1:], ["after crash"])

    def test_response_and_completed_status_are_atomic(self):
        insert = Conversation._insert_message

        def fail_after_insert(conversation, run_id, role, content, metadata):
            row = insert(conversation, run_id, role, content, metadata)
            if role == "assistant":
                raise RuntimeError("simulated save failure")
            return row

        request_id = uuid4()
        with patch.object(Conversation, "_insert_message", fail_after_insert):
            with self.assertRaisesRegex(ChatRunFailedError, "simulated save failure"):
                self.send("not committed", request_id=request_id)
        self.assertEqual(self.store.run(self.user, self.key, request_id)["status"], "failed")
        self.assertEqual([m.role for m in self.store.history(self.user, self.key)], ["user"])
        self.send("next")
        self.assertEqual([m.seq for m in self.store.history(self.user, self.key)], [1, 2, 3])

    def test_validation_happens_before_creating_a_run(self):
        for content in ("", "   "):
            with self.subTest(content=content), self.assertRaises(ValueError):
                self.send(content)
        with self.assertRaises(ValueError):
            self.send("hello", request_id="not-a-uuid")
        with self.assertRaises(ValueError):
            self.send("hello", user_id=" ")
        self.assertEqual(self.store.sessions(self.user), [])

    def test_traces_use_internal_session_and_owner_and_replays_add_no_trace(self):
        from gigachat.models import ChatCompletion
        from langchain_gigachat import GigaChat
        from langfuse import Langfuse
        from langfuse.langchain import CallbackHandler
        from opentelemetry.sdk.trace import TracerProvider
        from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter

        key = "pk-lf-test-" + uuid4().hex
        with patch.dict(os.environ, {"LANGFUSE_TRACING_ENABLED": "true"}):
            exporter = InMemorySpanExporter()
            langfuse = Langfuse(
                public_key=key, secret_key="sk-lf-test",
                tracer_provider=TracerProvider(), span_exporter=exporter,
            )
            self.addCleanup(langfuse.shutdown)
            model = GigaChat(credentials="test-model-key", model="GigaChat-2")
            self.addCleanup(model._client.close)
            self.service = ChatService(
                self.store, build_graph(model), history_turns=1,
                callback_factory=lambda: CallbackHandler(public_key=key),
            )
            response = ChatCompletion.model_validate({
                "choices": [{"message": {"role": "assistant", "content": "Hello"},
                             "index": 0, "finish_reason": "stop"}],
                "created": 1, "model": "GigaChat-2", "object": "chat.completion",
                "usage": {"prompt_tokens": 12, "completion_tokens": 3, "total_tokens": 15},
            })
            first_id = uuid4()
            with patch("gigachat.GigaChat.chat", return_value=response) as generate:
                first = self.send("first", request_id=first_id)
                second = self.send("second")
                self.send("first", request_id=first_id)
            langfuse.flush()
            spans = exporter.get_finished_spans()
            roots = [span for span in spans if span.parent is None]
            self.assertEqual(len(roots), 2)
            self.assertEqual(generate.call_count, 2)
            self.assertEqual(
                {f"{span.context.trace_id:032x}" for span in roots},
                {first.trace_id, second.trace_id},
            )
            for root in roots:
                self.assertEqual(root.attributes["session.id"], str(first.session_id))
                self.assertEqual(root.attributes["user.id"], self.user)
                self.assertEqual(root.name, "recommend-events")
            self.assertEqual(
                self.store.run(self.user, self.key, first_id)["trace_id"], first.trace_id
            )
