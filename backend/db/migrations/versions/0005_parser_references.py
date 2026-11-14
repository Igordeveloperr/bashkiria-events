"""City and venue dictionaries with city-scoped, address-aware aliases.

Revision ID: 0005_parser_references
Revises: 0004_parser_ingestion
"""
from datetime import datetime, timezone
import re
import unicodedata
from uuid import UUID, uuid5

from alembic import op
import sqlalchemy as sa

revision = "0005_parser_references"
down_revision = "0004_parser_ingestion"
branch_labels = None
depends_on = None

NAMESPACE = UUID("a452e241-09e5-45a7-a074-19481f9f2481")


def key(value):
    value = unicodedata.normalize("NFKC", value).lower().replace("ё", "е")
    return re.sub(r"[^\w]+", " ", value, flags=re.UNICODE).replace("_", " ").strip()


def address_key(value):
    return key(re.sub(r"\b(?:улица|ул)\.?\s*", "ул ", value.lower()))


def upgrade():
    for table, parent in (("parser_cities", None), ("parser_venues", "parser_cities")):
        columns = [sa.Column("id", sa.Uuid(), primary_key=True), sa.Column("name", sa.Text(), nullable=False),
                   sa.Column("normalized_name", sa.Text(), nullable=False),
                   sa.Column("created_at", sa.DateTime(timezone=True), nullable=False)]
        if parent:
            columns += [sa.Column("city_id", sa.Uuid(), sa.ForeignKey("parser_cities.id", ondelete="RESTRICT"), nullable=False),
                        sa.Column("address", sa.Text(), nullable=False), sa.Column("normalized_address", sa.Text(), nullable=False),
                        sa.UniqueConstraint("id", "city_id", name="parser_venues_id_city_key")]
        op.create_table(table, *columns)
    op.create_index("parser_cities_name_key", "parser_cities", ["normalized_name"], unique=True)
    op.create_index("parser_venues_identity_key", "parser_venues", ["city_id", "normalized_name", "normalized_address"], unique=True)
    op.create_table("parser_city_aliases",
        sa.Column("id", sa.Uuid(), primary_key=True),
        sa.Column("city_id", sa.Uuid(), sa.ForeignKey("parser_cities.id", ondelete="RESTRICT"), nullable=False),
        sa.Column("name", sa.Text(), nullable=False), sa.Column("normalized_name", sa.Text(), nullable=False),
        sa.Column("created_at", sa.DateTime(timezone=True), nullable=False))
    op.create_index("parser_city_aliases_name_key", "parser_city_aliases", ["normalized_name"], unique=True)
    op.create_table("parser_venue_aliases",
        sa.Column("id", sa.Uuid(), primary_key=True), sa.Column("venue_id", sa.Uuid(), nullable=False),
        sa.Column("city_id", sa.Uuid(), nullable=False), sa.Column("name", sa.Text(), nullable=False),
        sa.Column("address", sa.Text(), nullable=False), sa.Column("normalized_name", sa.Text(), nullable=False),
        sa.Column("normalized_address", sa.Text(), nullable=False), sa.Column("created_at", sa.DateTime(timezone=True), nullable=False),
        sa.ForeignKeyConstraint(["venue_id", "city_id"], ["parser_venues.id", "parser_venues.city_id"], ondelete="RESTRICT"))
    op.create_index("parser_venue_aliases_identity_key", "parser_venue_aliases", ["city_id", "normalized_name", "normalized_address"], unique=True)
    op.add_column("parser_events", sa.Column("city_id", sa.Uuid(), nullable=True))
    op.add_column("parser_events", sa.Column("venue_id", sa.Uuid(), nullable=True))
    op.create_foreign_key("parser_events_city_fk", "parser_events", "parser_cities", ["city_id"], ["id"], ondelete="RESTRICT")
    op.create_foreign_key("parser_events_venue_city_fk", "parser_events", "parser_venues", ["venue_id", "city_id"], ["id", "city_id"], ondelete="RESTRICT")
    op.create_check_constraint("parser_events_venue_requires_city", "parser_events", "venue_id IS NULL OR city_id IS NOT NULL")
    op.create_index("parser_events_reference_idx", "parser_events", ["city_id", "venue_id"])
    seed()


def seed():
    now = datetime(2026, 11, 14, tzinfo=timezone.utc)
    cities = ["Уфа", "Стерлитамак", "Салават", "Нефтекамск", "Октябрьский", "Белорецк", "Туймазы",
              "Ишимбай", "Сибай", "Кумертау", "Белебей", "Дюртюли", "Мелеуз", "Благовещенск", "Янаул",
              "Давлеканово", "Агидель", "Баймак", "Бирск", "Учалы", "Межгорье"]
    city_table = sa.table("parser_cities", sa.column("id", sa.Uuid()), sa.column("name", sa.Text()),
        sa.column("normalized_name", sa.Text()), sa.column("created_at", sa.DateTime(timezone=True)))
    city_alias_table = sa.table("parser_city_aliases", sa.column("id", sa.Uuid()), sa.column("city_id", sa.Uuid()),
        sa.column("name", sa.Text()), sa.column("normalized_name", sa.Text()), sa.column("created_at", sa.DateTime(timezone=True)))
    op.bulk_insert(city_table, [dict(id=uuid5(NAMESPACE, "city:" + name), name=name, normalized_name=key(name), created_at=now) for name in cities])
    op.bulk_insert(city_alias_table, [dict(id=uuid5(NAMESPACE, "city-alias:" + name), city_id=uuid5(NAMESPACE, "city:" + name),
        name=name, normalized_name=key(name), created_at=now) for name in cities])
    ufa = uuid5(NAMESPACE, "city:Уфа")
    venues = [
        ("gkz", "ГКЗ «Башкортостан»", "ул. Ленина, 50", ["Государственный концертный зал Башкортостан"]),
        ("bashgf", "Башкирская государственная филармония имени Хусаина Ахметова", "ул. Гоголя, 58",
         ["Башкирская государственная филармония", "Башкирская государственная филармония им. Х. Ахметова"]),
        ("bashopera", "Башкирский государственный театр оперы и балета", "ул. Ленина, 5/1",
         ["Башкирский государственный академический театр оперы и балета"]),
    ]
    venue_table = sa.table("parser_venues", sa.column("id", sa.Uuid()), sa.column("city_id", sa.Uuid()),
        sa.column("name", sa.Text()), sa.column("address", sa.Text()), sa.column("normalized_name", sa.Text()),
        sa.column("normalized_address", sa.Text()), sa.column("created_at", sa.DateTime(timezone=True)))
    alias_table = sa.table("parser_venue_aliases", sa.column("id", sa.Uuid()), sa.column("venue_id", sa.Uuid()), sa.column("city_id", sa.Uuid()),
        sa.column("name", sa.Text()), sa.column("address", sa.Text()), sa.column("normalized_name", sa.Text()),
        sa.column("normalized_address", sa.Text()), sa.column("created_at", sa.DateTime(timezone=True)))
    for code, name, address, aliases in venues:
        venue_id = uuid5(NAMESPACE, "venue:" + code)
        op.bulk_insert(venue_table, [dict(id=venue_id, city_id=ufa, name=name, address=address,
            normalized_name=key(name), normalized_address=address_key(address), created_at=now)])
        op.bulk_insert(alias_table, [dict(id=uuid5(NAMESPACE, "venue-alias:" + code + ":" + alias), venue_id=venue_id, city_id=ufa,
            name=alias, address="", normalized_name=key(alias), normalized_address="", created_at=now) for alias in [name, *aliases]])


def downgrade():
    op.drop_index("parser_events_reference_idx", table_name="parser_events")
    op.drop_constraint("parser_events_venue_requires_city", "parser_events", type_="check")
    op.drop_constraint("parser_events_venue_city_fk", "parser_events", type_="foreignkey")
    op.drop_constraint("parser_events_city_fk", "parser_events", type_="foreignkey")
    op.drop_column("parser_events", "venue_id")
    op.drop_column("parser_events", "city_id")
    for table in ("parser_venue_aliases", "parser_city_aliases", "parser_venues", "parser_cities"):
        op.drop_table(table)
