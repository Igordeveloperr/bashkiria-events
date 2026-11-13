"""Create chat history, or adopt the verified legacy SQL migration.

Revision ID: 0001_chat_history
Revises:
"""

import hashlib
from pathlib import Path

from alembic import context, op
from alembic.autogenerate import compare_metadata
from alembic.migration import MigrationContext
import sqlalchemy as sa
from sqlalchemy.dialects.postgresql import JSONB

revision = "0001_chat_history"
down_revision = None
branch_labels = None
depends_on = None


def initial_schema():
    # A frozen schema snapshot: future changes belong in new revisions.
    metadata = sa.MetaData()
    sessions = sa.Table(
        "chat_sessions", metadata,
        sa.Column("id", sa.Uuid(), primary_key=True),
        sa.Column("user_id", sa.Text(), nullable=False),
        sa.Column("session_key", sa.Text(), nullable=False),
        sa.Column("title", sa.Text()),
        sa.Column("next_seq", sa.BigInteger(), nullable=False, server_default=sa.text("1")),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.text("now()")),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.text("now()")),
        sa.CheckConstraint("length(btrim(user_id)) > 0"),
        sa.CheckConstraint("length(btrim(session_key)) > 0"),
        sa.CheckConstraint("next_seq > 0"),
        sa.UniqueConstraint("user_id", "session_key"),
    )
    sa.Index("chat_sessions_user_updated_idx", sessions.c.user_id, sessions.c.updated_at.desc(), sessions.c.id)
    runs = sa.Table(
        "chat_runs", metadata,
        sa.Column("id", sa.Uuid(), primary_key=True),
        sa.Column("session_id", sa.Uuid(), sa.ForeignKey("chat_sessions.id", ondelete="CASCADE"), nullable=False),
        sa.Column("request_id", sa.Uuid(), nullable=False),
        sa.Column("status", sa.Text(), nullable=False),
        sa.Column("trace_id", sa.Text()),
        sa.Column("error", sa.Text()),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.text("now()")),
        sa.Column("finished_at", sa.DateTime(timezone=True)),
        sa.UniqueConstraint("session_id", "request_id"),
        sa.UniqueConstraint("id", "session_id"),
        sa.CheckConstraint("status IN ('running', 'completed', 'failed')"),
        sa.CheckConstraint("(status = 'running') = (finished_at IS NULL)"),
        sa.CheckConstraint("(status = 'failed') = (error IS NOT NULL)"),
    )
    sa.Index("chat_runs_one_active_idx", runs.c.session_id, unique=True, postgresql_where=sa.text("status = 'running'"))
    sa.Index("chat_runs_session_idx", runs.c.session_id)
    messages = sa.Table(
        "chat_messages", metadata,
        sa.Column("id", sa.Uuid(), primary_key=True),
        sa.Column("session_id", sa.Uuid(), sa.ForeignKey("chat_sessions.id", ondelete="CASCADE"), nullable=False),
        sa.Column("run_id", sa.Uuid(), nullable=False),
        sa.Column("seq", sa.BigInteger(), nullable=False),
        sa.Column("role", sa.Text(), nullable=False),
        sa.Column("content", sa.Text(), nullable=False),
        sa.Column("metadata", JSONB(), nullable=False, server_default=sa.text("'{}'::jsonb")),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False, server_default=sa.text("now()")),
        sa.ForeignKeyConstraint(["run_id", "session_id"], ["chat_runs.id", "chat_runs.session_id"], ondelete="CASCADE"),
        sa.UniqueConstraint("session_id", "seq"),
        sa.UniqueConstraint("run_id", "role"),
        sa.CheckConstraint("seq > 0"),
        sa.CheckConstraint("role IN ('user', 'assistant')"),
    )
    sa.Index("chat_messages_user_seq_idx", messages.c.session_id, messages.c.seq.desc(), postgresql_where=sa.text("role = 'user'"))
    return metadata


def adopt_legacy(bind, metadata):
    inspector = sa.inspect(bind)
    tables = set(inspector.get_table_names())
    if not (tables & metadata.tables.keys()) and "ai_schema_migrations" not in tables:
        return False
    if not metadata.tables.keys() <= tables or "ai_schema_migrations" not in tables:
        raise RuntimeError("Existing chat schema has no complete legacy migration; refusing to stamp it")
    source = Path(__file__).resolve().parents[2] / "legacy" / "001_chat_history.sql"
    checksum = hashlib.sha256(source.read_text(encoding="utf-8").encode("utf-8")).hexdigest()
    records = bind.execute(sa.text("SELECT name, checksum FROM ai_schema_migrations")).all()
    if records != [(source.name, checksum)]:
        raise RuntimeError("Legacy migration checksum does not match; database was not changed")

    def include_object(obj, name, type_, reflected, compare_to):
        return type_ != "table" or name in metadata.tables

    comparison = MigrationContext.configure(bind, opts={
        "include_object": include_object, "compare_server_default": True,
    })
    if compare_metadata(comparison, metadata):
        raise RuntimeError("Legacy schema differs from its migration; refusing to stamp it")
    for table in metadata.tables.values():
        checks = [c for c in table.constraints if isinstance(c, sa.CheckConstraint)]
        if len(inspector.get_check_constraints(table.name)) != len(checks):
            raise RuntimeError("Legacy schema check constraints are incomplete")
    return True


def upgrade():
    metadata = initial_schema()
    bind = op.get_bind()
    if not context.is_offline_mode() and adopt_legacy(bind, metadata):
        return
    metadata.create_all(bind, checkfirst=False)


def downgrade():
    initial_schema().drop_all(op.get_bind(), checkfirst=False)
    op.execute("DROP TABLE IF EXISTS ai_schema_migrations")
