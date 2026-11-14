"""Distinguish page, file import and manual provenance without fabricated HTTP URLs.

Revision ID: 0004_parser_ingestion
Revises: 0003_parser_pipeline
"""
from alembic import op
import sqlalchemy as sa

revision = "0004_parser_ingestion"
down_revision = "0003_parser_pipeline"
branch_labels = None
depends_on = None


def upgrade():
    for table in ("parser_raw_documents", "parser_events", "parser_event_sources"):
        op.add_column(table, sa.Column("source_type", sa.Text(), nullable=False, server_default="Page"))
        op.create_check_constraint(f"{table}_source_type", table, "source_type IN ('Page','Import','Manual')")
    op.add_column("parser_raw_documents", sa.Column("content_type", sa.Text(), nullable=False, server_default="text/html"))
    op.alter_column("parser_raw_documents", "http_status", nullable=True)
    op.drop_constraint("parser_raw_http_url", "parser_raw_documents", type_="check")
    op.drop_constraint("parser_raw_status_range", "parser_raw_documents", type_="check")
    op.create_check_constraint("parser_raw_origin", "parser_raw_documents",
        "(source_type = 'Page' AND (url LIKE 'https://%' OR url LIKE 'http://%') "
        "AND http_status IS NOT NULL AND http_status BETWEEN 100 AND 599) OR "
        "(source_type IN ('Import','Manual') AND url LIKE 'urn:bashkiria-events:%' AND http_status IS NULL)")


def downgrade():
    # Never erase imported/manual provenance or forge a HTTP response to permit rollback.
    op.execute("""DO $$ BEGIN
      IF EXISTS (SELECT 1 FROM parser_raw_documents WHERE source_type <> 'Page')
        OR EXISTS (SELECT 1 FROM parser_events WHERE source_type <> 'Page')
        OR EXISTS (SELECT 1 FROM parser_event_sources WHERE source_type <> 'Page') THEN
        RAISE EXCEPTION 'Cannot downgrade ingestion schema while imported or manual data exists';
      END IF;
    END $$""")
    op.drop_constraint("parser_raw_origin", "parser_raw_documents", type_="check")
    op.alter_column("parser_raw_documents", "http_status", nullable=False)
    op.create_check_constraint("parser_raw_http_url", "parser_raw_documents", "url LIKE 'https://%' OR url LIKE 'http://%'")
    op.create_check_constraint("parser_raw_status_range", "parser_raw_documents", "http_status BETWEEN 100 AND 599")
    op.drop_column("parser_raw_documents", "content_type")
    for table in ("parser_event_sources", "parser_events", "parser_raw_documents"):
        op.drop_constraint(f"{table}_source_type", table, type_="check")
        op.drop_column(table, "source_type")
