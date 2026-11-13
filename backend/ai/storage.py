"""PostgreSQL chat storage; every public read is scoped to its owner."""

from contextlib import contextmanager
from dataclasses import dataclass
from datetime import datetime
from typing import Any
from uuid import UUID, uuid4

import psycopg
from psycopg.rows import dict_row
from psycopg.types.json import Jsonb


class SessionBusyError(RuntimeError):
    """Another worker is answering in this conversation; retry later."""


class RequestConflictError(ValueError):
    """A request ID was reused with different content."""


@dataclass(frozen=True)
class Message:
    id: UUID
    session_id: UUID
    run_id: UUID
    seq: int
    role: str
    content: str
    metadata: dict[str, Any]
    created_at: datetime


def validate_identity(user_id: str, session_key: str) -> None:
    for name, value in (("user_id", user_id), ("session_key", session_key)):
        if not isinstance(value, str) or not value.strip() or len(value) > 200:
            raise ValueError(f"{name}: требуется строка длиной 1–200 символов")


class ChatStore:
    def __init__(self, database_url: str, **connection_options):
        self._database_url = database_url
        self._connection_options = connection_options

    def _connect(self):
        return psycopg.connect(
            self._database_url,
            autocommit=True,
            row_factory=dict_row,
            connect_timeout=5,
            **self._connection_options,
        )

    @contextmanager
    def conversation(self, user_id: str, session_key: str):
        """Hold a connection-level lock, with no open transaction during the LLM call.

        A dedicated connection is required: transaction-pooling PgBouncer must
        not be used for this path. A closed connection releases its lock.
        """
        validate_identity(user_id, session_key)
        with self._connect() as connection:
            with connection.transaction():
                connection.execute(
                    """INSERT INTO chat_sessions (id, user_id, session_key)
                       VALUES (%s, %s, %s)
                       ON CONFLICT (user_id, session_key) DO NOTHING""",
                    (uuid4(), user_id, session_key),
                )
                session = connection.execute(
                    "SELECT * FROM chat_sessions WHERE user_id = %s AND session_key = %s",
                    (user_id, session_key),
                ).fetchone()
            lock_id = int.from_bytes(session["id"].bytes[:8], "big", signed=True)
            acquired = connection.execute(
                "SELECT pg_try_advisory_lock(%s) AS acquired", (lock_id,)
            ).fetchone()["acquired"]
            if not acquired:
                raise SessionBusyError("В этом диалоге уже формируется ответ; повтори позже")
            # Closing this dedicated connection releases the lock even on errors.
            conversation = Conversation(connection, session["id"])
            conversation.recover_abandoned_runs()
            yield conversation

    def history(
        self,
        user_id: str,
        session_key: str,
        *,
        limit: int = 50,
        before_seq: int | None = None,
    ) -> list[Message]:
        validate_identity(user_id, session_key)
        if not 1 <= limit <= 200:
            raise ValueError("limit должен быть от 1 до 200")
        with self._connect() as connection:
            rows = connection.execute(
                """SELECT m.* FROM chat_messages m
                   JOIN chat_sessions s ON s.id = m.session_id
                   WHERE s.user_id = %s AND s.session_key = %s
                     AND (%s::bigint IS NULL OR m.seq < %s)
                   ORDER BY m.seq DESC LIMIT %s""",
                (user_id, session_key, before_seq, before_seq, limit),
            ).fetchall()
        return [Message(**row) for row in reversed(rows)]

    def sessions(self, user_id: str, *, limit: int = 50) -> list[dict]:
        validate_identity(user_id, "unused")
        if not 1 <= limit <= 200:
            raise ValueError("limit должен быть от 1 до 200")
        with self._connect() as connection:
            return connection.execute(
                """SELECT id, session_key, title, created_at, updated_at
                   FROM chat_sessions WHERE user_id = %s
                   ORDER BY updated_at DESC, id LIMIT %s""",
                (user_id, limit),
            ).fetchall()

    def run(self, user_id: str, session_key: str, request_id: UUID) -> dict | None:
        validate_identity(user_id, session_key)
        with self._connect() as connection:
            return connection.execute(
                """SELECT r.* FROM chat_runs r
                   JOIN chat_sessions s ON s.id = r.session_id
                   WHERE s.user_id = %s AND s.session_key = %s AND r.request_id = %s""",
                (user_id, session_key, request_id),
            ).fetchone()


class Conversation:
    """Operations on a conversation whose advisory lock is held by this connection."""

    def __init__(self, connection, session_id: UUID):
        self._connection = connection
        self.session_id = session_id

    def recover_abandoned_runs(self) -> None:
        # A live worker would still own the lock, so any running row is abandoned.
        self._connection.execute(
            """UPDATE chat_runs SET status = 'failed', finished_at = now(),
                   error = 'Worker disconnected before saving the response'
               WHERE session_id = %s AND status = 'running'""",
            (self.session_id,),
        )

    def begin_run(self, request_id: UUID, content: str) -> tuple[dict, bool]:
        with self._connection.transaction():
            existing = self._connection.execute(
                """SELECT r.*, m.content FROM chat_runs r
                   JOIN chat_messages m ON m.run_id = r.id AND m.role = 'user'
                   WHERE r.session_id = %s AND r.request_id = %s""",
                (self.session_id, request_id),
            ).fetchone()
            if existing:
                if existing["content"] != content:
                    raise RequestConflictError("request_id уже использован с другим текстом")
                return existing, False
            run = self._connection.execute(
                """INSERT INTO chat_runs (id, session_id, request_id, status)
                   VALUES (%s, %s, %s, 'running') RETURNING *""",
                (uuid4(), self.session_id, request_id),
            ).fetchone()
            self._insert_message(run["id"], "user", content, {})
            return run, True

    def _insert_message(self, run_id: UUID, role: str, content: str, metadata: dict):
        seq = self._connection.execute(
            """UPDATE chat_sessions SET next_seq = next_seq + 1, updated_at = now()
               WHERE id = %s RETURNING next_seq - 1 AS seq""",
            (self.session_id,),
        ).fetchone()["seq"]
        return self._connection.execute(
            """INSERT INTO chat_messages
                   (id, session_id, run_id, seq, role, content, metadata)
               VALUES (%s, %s, %s, %s, %s, %s, %s) RETURNING *""",
            (uuid4(), self.session_id, run_id, seq, role, content, Jsonb(metadata)),
        ).fetchone()

    def context(self, history_turns: int) -> list[Message]:
        if history_turns < 0:
            raise ValueError("history_turns должен быть >= 0")
        rows = self._connection.execute(
            """WITH recent_runs AS (
                   SELECT r.id FROM chat_runs r
                   JOIN chat_messages u ON u.run_id = r.id AND u.role = 'user'
                   WHERE u.session_id = %s AND r.status = 'completed'
                   ORDER BY u.seq DESC LIMIT %s
               )
               SELECT m.* FROM chat_messages m
               JOIN recent_runs r ON r.id = m.run_id ORDER BY m.seq""",
            (self.session_id, history_turns),
        ).fetchall()
        return [Message(**row) for row in rows]

    def answer(self, run_id: UUID) -> Message:
        row = self._connection.execute(
            """SELECT * FROM chat_messages
               WHERE session_id = %s AND run_id = %s AND role = 'assistant'""",
            (self.session_id, run_id),
        ).fetchone()
        if row is None:
            raise RuntimeError("У завершённого запуска отсутствует ответ")
        return Message(**row)

    def complete_run(
        self, run_id: UUID, content: str, metadata: dict, trace_id: str | None
    ) -> Message:
        with self._connection.transaction():
            row = self._insert_message(run_id, "assistant", content, metadata)
            self._connection.execute(
                """UPDATE chat_runs SET status = 'completed', trace_id = %s,
                       finished_at = now() WHERE id = %s AND session_id = %s""",
                (trace_id, run_id, self.session_id),
            )
        return Message(**row)

    def fail_run(self, run_id: UUID, error: str, trace_id: str | None) -> None:
        self._connection.execute(
            """UPDATE chat_runs SET status = 'failed', error = %s, trace_id = %s,
                   finished_at = now() WHERE id = %s AND session_id = %s
                   AND status = 'running'""",
            (error, trace_id, run_id, self.session_id),
        )
