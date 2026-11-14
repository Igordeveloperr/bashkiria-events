from datetime import date, datetime, time
from decimal import Decimal
from enum import Enum
from typing import Annotated, Any, Literal, Self
from uuid import UUID

from pydantic import (
    AwareDatetime,
    BaseModel,
    ConfigDict,
    Field,
    StringConstraints,
    model_validator,
)

TIME_ZONE = "Asia/Yekaterinburg"
SearchText = Annotated[
    str, StringConstraints(strip_whitespace=True, min_length=1, max_length=300)
]
Money = Annotated[
    Decimal, Field(ge=0, max_digits=12, decimal_places=2, allow_inf_nan=False)
]


class Category(str, Enum):
    other = "Other"
    concert = "Concert"
    theatre = "Theatre"
    exhibition = "Exhibition"
    festival = "Festival"
    cinema = "Cinema"
    excursion = "Excursion"
    workshop = "Workshop"
    lecture = "Lecture"


class PublicationStatus(str, Enum):
    draft = "Draft"
    published = "Published"
    cancelled = "Cancelled"


class SourceStatus(str, Enum):
    unknown = "Unknown"
    scheduled = "Scheduled"
    cancelled = "Cancelled"
    postponed = "Postponed"
    rescheduled = "Rescheduled"
    sold_out = "SoldOut"


class Pagination(BaseModel):
    model_config = ConfigDict(extra="forbid")

    page: int = Field(default=1, ge=1, le=1_000_000)
    page_size: int = Field(default=50, ge=1, le=500)
    offset: int | None = Field(
        default=None,
        ge=0,
        le=9223372036854775807,
        description="Смещение; при наличии заменяет page",
    )

    @property
    def start_offset(self) -> int:
        return (
            self.offset if self.offset is not None else (self.page - 1) * self.page_size
        )


class EventFilters(BaseModel):
    model_config = ConfigDict(extra="forbid")

    public_only: bool = Field(
        default=False, description="Только опубликованные события без ручного скрытия"
    )

    q: SearchText | None = Field(
        default=None, description="Полный текст, часть названия или опечатка"
    )
    title: SearchText | None = None
    categories: list[Category] = Field(default_factory=list, max_length=20)
    tags: list[SearchText] = Field(default_factory=list, max_length=50)
    tags_mode: Literal["any", "all"] = "all"
    city: SearchText | None = Field(
        default=None, description="Точное название без учёта регистра"
    )
    venue: SearchText | None = Field(
        default=None, description="Часть названия площадки"
    )
    address: SearchText | None = None
    language: SearchText | None = None
    statuses: list[PublicationStatus] = Field(
        default_factory=list,
        max_length=3,
        description="Статусы публикации; без параметра возвращаются все статусы",
    )
    source_statuses: list[SourceStatus] = Field(default_factory=list, max_length=6)
    date_from: AwareDatetime | None = Field(
        default=None, description="Начало периода, включительно; нужен UTC offset"
    )
    date_to: AwareDatetime | None = Field(
        default=None, description="Конец периода, исключительно; нужен UTC offset"
    )
    start_date_from: date | None = None
    start_date_to: date | None = None
    price_from: Money | None = None
    price_to: Money | None = None
    is_free: bool | None = None
    age_limit_max: int | None = Field(default=None, ge=0, le=18)
    duration_from: int | None = Field(default=None, ge=0, le=525600)
    duration_to: int | None = Field(default=None, ge=0, le=525600)
    source_id: SearchText | None = None
    confidence_min: float | None = Field(default=None, ge=0, le=1, allow_inf_nan=False)
    updated_since: AwareDatetime | None = None
    verified_since: AwareDatetime | None = None
    latitude: float | None = Field(default=None, ge=-90, le=90, allow_inf_nan=False)
    longitude: float | None = Field(default=None, ge=-180, le=180, allow_inf_nan=False)
    radius_km: float | None = Field(default=None, gt=0, le=20000, allow_inf_nan=False)

    @model_validator(mode="after")
    def valid_ranges(self) -> Self:
        if (
            self.date_from is not None
            and self.date_to is not None
            and self.date_from >= self.date_to
        ):
            raise ValueError("date_from должен быть раньше date_to")
        for first, last in (
            ("start_date_from", "start_date_to"),
            ("price_from", "price_to"),
            ("duration_from", "duration_to"),
        ):
            lower, upper = getattr(self, first), getattr(self, last)
            if lower is not None and upper is not None and lower > upper:
                raise ValueError(f"{first} не может быть больше {last}")
        geo = (self.latitude, self.longitude, self.radius_km)
        if any(value is not None for value in geo) and not all(
            value is not None for value in geo
        ):
            raise ValueError("latitude, longitude и radius_km указываются вместе")
        return self


class SearchQuery(EventFilters, Pagination):
    short: bool = Field(
        default=False,
        description="Компактные события для ИИ: без description, исходных JSON и служебных данных",
    )
    sort: (
        Literal[
            "date_asc",
            "date_desc",
            "price_asc",
            "price_desc",
            "updated_desc",
            "relevance",
        ]
        | None
    ) = Field(
        default=None,
        description="По умолчанию relevance при наличии q, иначе date_asc",
    )

    @model_validator(mode="after")
    def relevance_needs_query(self) -> Self:
        if self.sort == "relevance" and not self.q:
            raise ValueError("Сортировка relevance требует q")
        return self


class ScheduleQuery(Pagination):
    venue: SearchText = Field(description="Точное название площадки без учёта регистра")
    date_from: AwareDatetime
    date_to: AwareDatetime
    city: SearchText | None = None
    address: SearchText | None = Field(
        default=None, description="Часть адреса для различения одноимённых площадок"
    )

    @model_validator(mode="after")
    def valid_period(self) -> Self:
        if self.date_from >= self.date_to:
            raise ValueError("date_from должен быть раньше date_to")
        return self


class SimilarQuery(Pagination):
    short: bool = Field(
        default=False,
        description="Компактные похожие события для ИИ, без description и источников",
    )
    min_score: float = Field(default=0.15, ge=0, le=1, allow_inf_nan=False)
    city: SearchText | None = None
    date_from: AwareDatetime | None = None
    date_to: AwareDatetime | None = None
    public_only: bool = False

    @model_validator(mode="after")
    def valid_period(self) -> Self:
        if (
            self.date_from is not None
            and self.date_to is not None
            and self.date_from >= self.date_to
        ):
            raise ValueError("date_from должен быть раньше date_to")
        return self


class SourceResponse(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: UUID
    identity_key: str
    source_id: str
    source_type: Literal["Page", "Import", "Manual"]
    external_id: str
    source_url: str
    data_hash: str
    event_id: UUID
    raw_document_id: UUID
    candidate_json: Any
    checked_at: datetime


class EventResponse(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: UUID
    dedup_key: str
    title: str
    normalized_title: str
    category: Category
    tags: list[str]
    start_date: date | None
    start_time: time | None
    end_date: date | None
    end_time: time | None
    date_start: datetime | None
    date_end: datetime | None
    duration_min: int | None
    time_zone: str = TIME_ZONE
    city: str
    city_id: UUID | None
    venue_id: UUID | None
    source_type: Literal["Page", "Import", "Manual"]
    venue: str
    address: str
    latitude: Decimal | None
    longitude: Decimal | None
    description: str
    price_min: Decimal | None
    price_max: Decimal | None
    is_free: bool | None
    age_limit: int | None
    language: str | None
    org_link: str | None
    image_url: str | None
    status: PublicationStatus
    source_status: SourceStatus
    source_type: Literal["Page", "Import", "Manual"]
    city_id: UUID | None
    venue_id: UUID | None
    confidence: Decimal
    issues_json: Any
    data_hash: str
    source_priority: int
    verified_at: datetime
    created_at: datetime
    updated_at: datetime
    reviewed_at: datetime | None
    review_note: str | None
    version: int
    manually_suppressed: bool
    sources: list[SourceResponse]


class ShortEventResponse(BaseModel):
    """Facts needed to select and recommend an event without parser payloads."""

    model_config = ConfigDict(from_attributes=True)

    id: UUID
    title: str
    category: Category
    start_date: date | None
    start_time: time | None
    venue: str
    age_limit: int | None


class PageMetadata(BaseModel):
    total: int
    page: int
    page_size: int
    pages: int
    has_next: bool
    has_previous: bool


class EventPage(PageMetadata):
    items: list[EventResponse]


class ShortEventPage(PageMetadata):
    items: list[ShortEventResponse]


class SchedulePage(EventPage):
    venue: str
    date_from: datetime
    date_to: datetime
    time_zone: str = TIME_ZONE


class SimilarEventResponse(EventResponse):
    score: float = Field(ge=0, le=1, allow_inf_nan=False)


class SimilarPage(EventPage):
    reference_event_id: UUID
    items: list[SimilarEventResponse]


class ShortSimilarEventResponse(ShortEventResponse):
    score: float = Field(ge=0, le=1, allow_inf_nan=False)


class ShortSimilarPage(ShortEventPage):
    reference_event_id: UUID
    items: list[ShortSimilarEventResponse]
