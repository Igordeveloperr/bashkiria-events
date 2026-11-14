"""PostgreSQL expressions shared by search, schedules and similarity ranking."""

import re
import unicodedata
from datetime import time

from sqlalchemy import (
    DateTime,
    Float,
    and_,
    case,
    cast,
    func,
    literal,
    literal_column,
    or_,
    select,
)

from searcher.models import Event, EventSource
from searcher.schemas import TIME_ZONE, EventFilters


def normalize(value: str) -> str:
    return value.strip().lower().replace("ё", "е")


def normalize_title(value: str) -> str:
    return re.sub(
        r"[\W_]+", " ", normalize(unicodedata.normalize("NFKC", value))
    ).strip()


def normalized(column):
    return func.replace(
        func.lower(func.btrim(column)), literal_column("'ё'"), literal_column("'е'")
    )


def search_vector():
    # Keep this expression identical to the GIN index in migration 0004.
    space = literal_column("' '")
    document = (
        Event.title
        + space
        + Event.description
        + space
        + Event.city
        + space
        + Event.venue
        + space
        + Event.address
        + space
        + func.coalesce(Event.language, literal_column("''"))
    )
    document = func.replace(
        func.lower(document), literal_column("'ё'"), literal_column("'е'")
    )
    return func.to_tsvector(literal_column("'russian'::regconfig"), document)


def text_query(value: str):
    return func.websearch_to_tsquery(
        literal_column("'russian'::regconfig"), normalize(value)
    )


def text_match(value: str):
    title = normalize_title(value)
    conditions = [search_vector().bool_op("@@")(text_query(value))]
    if title:
        conditions.extend(
            (
                Event.normalized_title.contains(title, autoescape=True),
                Event.normalized_title.bool_op("%")(title),
            )
        )
    return or_(*conditions)


def text_rank(value: str):
    return func.ts_rank_cd(search_vector(), text_query(value)) + func.similarity(
        Event.normalized_title, normalize_title(value)
    )


def event_bounds():
    """Unknown time occupies its calendar day; unknown end is a point in time.

    The original nullable fields are preserved in responses. These inferred
    bounds are used only for period filtering, never presented as known times.
    """
    start = func.coalesce(
        Event.date_start,
        func.timezone(
            TIME_ZONE, Event.start_date + func.coalesce(Event.start_time, time.min)
        ),
        type_=DateTime(timezone=True),
    )
    end = func.coalesce(
        Event.date_end,
        case(
            (
                Event.end_time.is_not(None),
                func.timezone(
                    TIME_ZONE,
                    func.coalesce(Event.end_date, Event.start_date) + Event.end_time,
                ),
            )
        ),
        case(
            (
                Event.duration_min > 0,
                start + Event.duration_min * literal_column("INTERVAL '1 minute'"),
            )
        ),
        case(
            (
                or_(Event.end_date.is_not(None), Event.start_time.is_(None)),
                func.timezone(
                    TIME_ZONE,
                    cast(func.coalesce(Event.end_date, Event.start_date) + 1, DateTime),
                ),
            )
        ),
        start,
        type_=DateTime(timezone=True),
    )
    return start, func.greatest(start, end)


def period_conditions(date_from=None, date_to=None):
    start, end = event_bounds()
    conditions = []
    if date_from is not None:
        conditions.append(or_(end > date_from, and_(end == start, start >= date_from)))
    if date_to is not None:
        conditions.append(start < date_to)
    return conditions


def tag_count(values: list[str] | None = None):
    tag = func.unnest(Event.tags).column_valued("tag")
    statement = select(func.count(func.distinct(normalized(tag)))).where(
        normalized(tag) != ""
    )
    if values is not None:
        statement = statement.where(normalized(tag).in_(values))
    return statement.correlate(Event).scalar_subquery()


def price_bounds():
    low = case(
        (Event.is_free.is_(True), 0),
        else_=func.coalesce(Event.price_min, Event.price_max),
    )
    high = case(
        (Event.is_free.is_(True), 0),
        else_=func.coalesce(Event.price_max, Event.price_min),
    )
    return low, high


def filter_conditions(filters: EventFilters):
    conditions = [Event.status.in_([status.value for status in filters.statuses])]
    if filters.q:
        conditions.append(text_match(filters.q))
    if filters.title:
        # Search the source title too, so punctuation in the title stays searchable.
        conditions.append(
            normalized(Event.title).contains(normalize(filters.title), autoescape=True)
        )
    if filters.categories:
        conditions.append(
            Event.category.in_([category.value for category in filters.categories])
        )
    if filters.tags:
        values = sorted({normalize(tag) for tag in filters.tags})
        count = tag_count(values)
        conditions.append(
            count == len(values) if filters.tags_mode == "all" else count > 0
        )
    if filters.city:
        conditions.append(normalized(Event.city) == normalize(filters.city))
    for field in ("venue", "address", "language"):
        value = getattr(filters, field)
        if value:
            conditions.append(
                normalized(getattr(Event, field)).contains(
                    normalize(value), autoescape=True
                )
            )
    if filters.source_statuses:
        conditions.append(
            Event.source_status.in_(
                [status.value for status in filters.source_statuses]
            )
        )
    conditions.extend(period_conditions(filters.date_from, filters.date_to))
    if filters.start_date_from is not None:
        conditions.append(Event.start_date >= filters.start_date_from)
    if filters.start_date_to is not None:
        conditions.append(Event.start_date <= filters.start_date_to)
    low, high = price_bounds()
    if filters.price_from is not None:
        conditions.append(high >= filters.price_from)
    if filters.price_to is not None:
        conditions.append(low <= filters.price_to)
    if filters.is_free is not None:
        conditions.append(Event.is_free == filters.is_free)
    if filters.age_limit_max is not None:
        conditions.append(Event.age_limit <= filters.age_limit_max)
    if filters.duration_from is not None:
        conditions.append(Event.duration_min >= filters.duration_from)
    if filters.duration_to is not None:
        conditions.append(Event.duration_min <= filters.duration_to)
    if filters.source_id:
        conditions.append(
            Event.sources.any(
                normalized(EventSource.source_id) == normalize(filters.source_id)
            )
        )
    if filters.confidence_min is not None:
        conditions.append(Event.confidence >= filters.confidence_min)
    if filters.updated_since is not None:
        conditions.append(Event.updated_at >= filters.updated_since)
    if filters.verified_since is not None:
        conditions.append(Event.verified_at >= filters.verified_since)
    if filters.radius_km is not None:
        lat = func.radians(cast(Event.latitude, Float))
        lon = func.radians(cast(Event.longitude, Float))
        origin_lat = func.radians(filters.latitude)
        origin_lon = func.radians(filters.longitude)
        cosine = func.sin(lat) * func.sin(origin_lat) + func.cos(lat) * func.cos(
            origin_lat
        ) * func.cos(lon - origin_lon)
        distance = 6371.0088 * func.acos(func.least(1.0, func.greatest(-1.0, cosine)))
        conditions.extend(
            (
                Event.latitude.is_not(None),
                Event.longitude.is_not(None),
                distance <= filters.radius_km,
            )
        )
    return conditions


def similarity(reference: Event):
    """Bounded [0, 1] score; missing features contribute zero."""
    title_score = func.similarity(Event.normalized_title, reference.normalized_title)
    description_score = (
        func.similarity(normalized(Event.description), normalize(reference.description))
        if reference.description.strip()
        else literal(0.0)
    )
    tags = sorted({normalize(tag) for tag in reference.tags if tag.strip()})
    shared = tag_count(tags) if tags else literal(0)
    tags_score = (
        cast(shared, Float) / func.nullif(tag_count() + len(tags) - shared, 0)
        if tags
        else literal(0.0)
    )
    category_match = and_(
        Event.category == reference.category, Event.category != "Other"
    )
    city_match = (
        normalized(Event.city) == normalize(reference.city)
        if reference.city.strip()
        else literal(False)
    )
    venue_match = (
        and_(city_match, normalized(Event.venue) == normalize(reference.venue))
        if reference.venue.strip()
        else literal(False)
    )
    score = (
        0.35 * title_score
        + 0.15 * description_score
        + 0.25 * func.coalesce(tags_score, 0.0)
        + case((category_match, 0.15), else_=0.0)
        + case((city_match, 0.05), else_=0.0)
        + case((venue_match, 0.05), else_=0.0)
    )
    # A shared city/venue by itself doesn't make two events similar.
    candidate = or_(
        category_match,
        shared > 0,
        title_score >= 0.15,
        and_(literal(bool(reference.description.strip())), description_score >= 0.2),
    )
    return func.least(1.0, func.greatest(0.0, score)), candidate
