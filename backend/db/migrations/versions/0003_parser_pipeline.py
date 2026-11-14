"""Add normalized parser events, provenance, audit and durable collection jobs.

Revision ID: 0003_parser_pipeline
Revises: 0002_parser_raw_documents
"""

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects.postgresql import JSONB, ARRAY

revision = "0003_parser_pipeline"
down_revision = "0002_parser_raw_documents"
branch_labels = None
depends_on = None


def upgrade():
    op.create_table(
        "parser_events",
        sa.Column("id", sa.Uuid(), primary_key=True),
        *[sa.Column(name, sa.Text(), nullable=False) for name in (
            "dedup_key", "title", "normalized_title", "category", "city", "venue", "address",
            "description", "status", "source_status", "data_hash")],
        sa.Column("tags", ARRAY(sa.Text()), nullable=False),
        sa.Column("start_date", sa.Date()), sa.Column("start_time", sa.Time()),
        sa.Column("end_date", sa.Date()), sa.Column("end_time", sa.Time()),
        sa.Column("date_start", sa.DateTime(timezone=True)),
        sa.Column("date_end", sa.DateTime(timezone=True)),
        sa.Column("duration_min", sa.Integer()),
        sa.Column("latitude", sa.Numeric(10, 7)), sa.Column("longitude", sa.Numeric(10, 7)),
        sa.Column("price_min", sa.Numeric(12, 2)), sa.Column("price_max", sa.Numeric(12, 2)),
        sa.Column("is_free", sa.Boolean()), sa.Column("age_limit", sa.Integer()),
        sa.Column("language", sa.Text()), sa.Column("org_link", sa.Text()), sa.Column("image_url", sa.Text()),
        sa.Column("confidence", sa.Numeric(4, 3), nullable=False),
        sa.Column("issues_json", JSONB(), nullable=False),
        sa.Column("source_priority", sa.Integer(), nullable=False),
        *[sa.Column(name, sa.DateTime(timezone=True), nullable=False) for name in (
            "verified_at", "created_at", "updated_at")],
        sa.Column("reviewed_at", sa.DateTime(timezone=True)), sa.Column("review_note", sa.Text()),
        sa.Column("version", sa.BigInteger(), nullable=False),
        sa.Column("manually_suppressed", sa.Boolean(), nullable=False),
        sa.CheckConstraint("status IN ('Draft','Published','Cancelled')", name="parser_event_status"),
        sa.CheckConstraint("source_status IN ('Unknown','Scheduled','Cancelled','Postponed','Rescheduled','SoldOut')", name="parser_source_status"),
        sa.CheckConstraint("category IN ('Other','Concert','Theatre','Exhibition','Festival','Cinema','Excursion','Workshop','Lecture')", name="parser_event_category"),
        sa.CheckConstraint("confidence BETWEEN 0 AND 1", name="parser_event_confidence"),
        sa.CheckConstraint("price_min IS NULL OR price_min >= 0", name="parser_event_min_price"),
        sa.CheckConstraint("price_max IS NULL OR price_max >= COALESCE(price_min, 0)", name="parser_event_max_price"),
        sa.CheckConstraint("age_limit IS NULL OR age_limit IN (0,6,12,16,18)", name="parser_event_age"),
        sa.CheckConstraint("end_date IS NULL OR start_date IS NULL OR end_date >= start_date", name="parser_event_dates"),
        sa.CheckConstraint("status <> 'Published' OR (start_date IS NOT NULL AND length(btrim(title)) > 0 AND length(btrim(city)) > 0 AND length(btrim(venue)) > 0)", name="parser_event_public_fields"),
    )
    op.create_index("parser_events_dedup_key", "parser_events", ["dedup_key"], unique=True)
    op.create_index("parser_events_status_date_idx", "parser_events", ["status", "start_date"])

    op.create_table(
        "parser_event_sources",
        sa.Column("id", sa.Uuid(), primary_key=True),
        *[sa.Column(name, sa.Text(), nullable=False) for name in (
            "identity_key", "source_id", "external_id", "source_url", "data_hash")],
        sa.Column("event_id", sa.Uuid(), sa.ForeignKey("parser_events.id", ondelete="RESTRICT"), nullable=False),
        sa.Column("raw_document_id", sa.Uuid(), sa.ForeignKey("parser_raw_documents.id", ondelete="RESTRICT"), nullable=False),
        sa.Column("candidate_json", JSONB(), nullable=False),
        sa.Column("checked_at", sa.DateTime(timezone=True), nullable=False),
    )
    op.create_index("parser_event_sources_identity_key", "parser_event_sources", ["identity_key"], unique=True)
    op.create_index("parser_event_sources_event_idx", "parser_event_sources", ["event_id"])
    op.create_index("parser_event_sources_raw_idx", "parser_event_sources", ["raw_document_id"])

    op.create_table(
        "parser_event_audit",
        sa.Column("id", sa.Uuid(), primary_key=True),
        sa.Column("event_id", sa.Uuid(), sa.ForeignKey("parser_events.id", ondelete="RESTRICT"), nullable=False),
        sa.Column("action", sa.Text(), nullable=False), sa.Column("actor", sa.Text(), nullable=False),
        sa.Column("snapshot_json", JSONB(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
    )
    op.create_index("parser_event_audit_event_idx", "parser_event_audit", ["event_id", "created_at"])

    op.create_table(
        "parser_collection_runs",
        sa.Column("id", sa.Uuid(), primary_key=True),
        sa.Column("source_id", sa.Text(), nullable=False), sa.Column("status", sa.Text(), nullable=False),
        sa.Column("max_pages", sa.Integer(), nullable=False), sa.Column("attempts", sa.Integer(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("started_at", sa.DateTime(timezone=True)), sa.Column("finished_at", sa.DateTime(timezone=True)),
        sa.Column("lease_owner", sa.Uuid()), sa.Column("lease_expires_at", sa.DateTime(timezone=True)),
        sa.Column("error", sa.Text()), sa.Column("report_json", JSONB()), sa.Column("schedule_key", sa.Text()),
        sa.CheckConstraint("status IN ('Queued','Running','Completed','CompletedWithErrors','Failed','Cancelled')", name="parser_run_status"),
        sa.CheckConstraint("max_pages BETWEEN 1 AND 500", name="parser_run_page_limit"),
        sa.CheckConstraint("attempts >= 0", name="parser_run_attempts"),
    )
    op.create_index("parser_runs_one_active_source", "parser_collection_runs", ["source_id"], unique=True,
                    postgresql_where=sa.text("status IN ('Queued', 'Running')"))
    op.create_index("parser_runs_queue_idx", "parser_collection_runs", ["status", "created_at"])
    op.create_index("parser_runs_schedule_key", "parser_collection_runs", ["schedule_key"], unique=True)


def downgrade():
    for table in ("parser_collection_runs", "parser_event_audit", "parser_event_sources", "parser_events"):
        op.drop_table(table)
