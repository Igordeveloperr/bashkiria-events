"""Read-only mappings of the catalog through Alembic 0005; migrations belong to backend/db."""

from datetime import date, datetime, time
from decimal import Decimal
from typing import Any
from uuid import UUID

from sqlalchemy import BigInteger, DateTime, ForeignKey, Numeric, Text
from sqlalchemy.dialects.postgresql import ARRAY, JSONB
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column, relationship


class Base(DeclarativeBase):
    pass


class Event(Base):
    __tablename__ = "parser_events"

    id: Mapped[UUID] = mapped_column(primary_key=True)
    dedup_key: Mapped[str] = mapped_column(Text)
    title: Mapped[str] = mapped_column(Text)
    normalized_title: Mapped[str] = mapped_column(Text)
    category: Mapped[str] = mapped_column(Text)
    tags: Mapped[list[str]] = mapped_column(ARRAY(Text))
    start_date: Mapped[date | None]
    start_time: Mapped[time | None]
    end_date: Mapped[date | None]
    end_time: Mapped[time | None]
    date_start: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    date_end: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    duration_min: Mapped[int | None]
    city: Mapped[str] = mapped_column(Text)
    city_id: Mapped[UUID | None]
    venue_id: Mapped[UUID | None]
    source_type: Mapped[str] = mapped_column(Text, server_default="Page")
    venue: Mapped[str] = mapped_column(Text)
    address: Mapped[str] = mapped_column(Text)
    latitude: Mapped[Decimal | None] = mapped_column(Numeric(10, 7))
    longitude: Mapped[Decimal | None] = mapped_column(Numeric(10, 7))
    description: Mapped[str] = mapped_column(Text)
    price_min: Mapped[Decimal | None] = mapped_column(Numeric(12, 2))
    price_max: Mapped[Decimal | None] = mapped_column(Numeric(12, 2))
    is_free: Mapped[bool | None]
    age_limit: Mapped[int | None]
    language: Mapped[str | None] = mapped_column(Text)
    org_link: Mapped[str | None] = mapped_column(Text)
    image_url: Mapped[str | None] = mapped_column(Text)
    status: Mapped[str] = mapped_column(Text)
    source_status: Mapped[str] = mapped_column(Text)
    source_type: Mapped[str] = mapped_column(Text, server_default="Page")
    city_id: Mapped[UUID | None]
    venue_id: Mapped[UUID | None]
    confidence: Mapped[Decimal] = mapped_column(Numeric(4, 3))
    issues_json: Mapped[Any] = mapped_column(JSONB)
    data_hash: Mapped[str] = mapped_column(Text)
    source_priority: Mapped[int]
    verified_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
    reviewed_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    review_note: Mapped[str | None] = mapped_column(Text)
    version: Mapped[int] = mapped_column(BigInteger)
    manually_suppressed: Mapped[bool]
    sources: Mapped[list["EventSource"]] = relationship(
        lazy="raise", order_by="(EventSource.source_id, EventSource.id)"
    )


class EventSource(Base):
    __tablename__ = "parser_event_sources"

    id: Mapped[UUID] = mapped_column(primary_key=True)
    identity_key: Mapped[str] = mapped_column(Text)
    source_id: Mapped[str] = mapped_column(Text)
    source_type: Mapped[str] = mapped_column(Text, server_default="Page")
    external_id: Mapped[str] = mapped_column(Text)
    source_url: Mapped[str] = mapped_column(Text)
    data_hash: Mapped[str] = mapped_column(Text)
    event_id: Mapped[UUID] = mapped_column(ForeignKey("parser_events.id"))
    # Raw documents aren't loaded by this service.
    raw_document_id: Mapped[UUID]
    candidate_json: Mapped[Any] = mapped_column(JSONB)
    checked_at: Mapped[datetime] = mapped_column(DateTime(timezone=True))
