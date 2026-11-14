"""Index event search and venue schedules; enable trigram similarity.

Revision ID: 0004_event_search
Revises: 0003_parser_pipeline
"""

from alembic import op
import sqlalchemy as sa

revision = "0004_event_search"
down_revision = "0003_parser_pipeline"
branch_labels = None
depends_on = None


def upgrade():
    # Shared extension: keep it in public, including when testing in a temporary schema.
    op.execute("CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public")
    op.create_index("parser_events_search_idx", "parser_events", [sa.text(
        "to_tsvector('russian'::regconfig, replace(lower("
        "title || ' ' || description || ' ' || city || ' ' || venue || ' ' || address || ' ' || coalesce(language, '')"
        "), 'ё', 'е'))"
    )], postgresql_using="gin")
    op.create_index("parser_events_title_trgm_idx", "parser_events", ["normalized_title"],
                    postgresql_using="gin", postgresql_ops={"normalized_title": "public.gin_trgm_ops"})
    op.create_index("parser_events_venue_date_idx", "parser_events", [
        "status", sa.text("replace(lower(btrim(venue)), 'ё', 'е')"), "start_date", "start_time", "id"
    ])
    op.create_index("parser_events_city_category_idx", "parser_events", [
        "status", sa.text("replace(lower(btrim(city)), 'ё', 'е')"), "category", "start_date"
    ])
    op.create_index("parser_event_sources_source_idx", "parser_event_sources", [
        sa.text("replace(lower(btrim(source_id)), 'ё', 'е')"), "event_id"
    ])


def downgrade():
    op.drop_index("parser_event_sources_source_idx", table_name="parser_event_sources")
    for name in ("city_category", "venue_date", "title_trgm", "search"):
        op.drop_index(f"parser_events_{name}_idx", table_name="parser_events")
    # pg_trgm may be used by other services; don't drop a shared extension.
