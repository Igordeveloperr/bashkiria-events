"""Store immutable source responses before parsing.

Revision ID: 0002_parser_raw_documents
Revises: 0001_chat_history
"""

from alembic import op
import sqlalchemy as sa

revision = "0002_parser_raw_documents"
down_revision = "0001_chat_history"
branch_labels = None
depends_on = None


def upgrade():
    op.create_table(
        "parser_raw_documents",
        sa.Column("id", sa.Uuid(), primary_key=True),
        sa.Column("source_id", sa.Text(), nullable=False),
        sa.Column("url", sa.Text(), nullable=False),
        sa.Column("fetched_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("http_status", sa.Integer(), nullable=False),
        sa.Column("content", sa.Text(), nullable=False),
        sa.Column("content_hash", sa.Text(), nullable=False),
        sa.CheckConstraint("length(btrim(source_id)) > 0", name="parser_raw_source_not_empty"),
        sa.CheckConstraint("url LIKE 'https://%' OR url LIKE 'http://%'", name="parser_raw_http_url"),
        sa.CheckConstraint("http_status BETWEEN 100 AND 599", name="parser_raw_status_range"),
        sa.CheckConstraint("content_hash ~ '^[0-9a-f]{64}$'", name="parser_raw_sha256_format"),
    )
    # Repeated responses are history, not duplicates: content_hash is intentionally not unique.
    op.create_index(
        "parser_raw_source_url_fetched_idx", "parser_raw_documents",
        ["source_id", "url", sa.text("fetched_at DESC")],
    )


def downgrade():
    op.drop_table("parser_raw_documents")
