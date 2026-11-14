"""Data models used by the event tools."""

from datetime import date, time
from decimal import Decimal
from typing import Annotated, Literal, Self
from uuid import UUID

from pydantic import (
    AwareDatetime,
    BaseModel,
    ConfigDict,
    Field,
    HttpUrl,
    StringConstraints,
    model_validator,
)

Text = Annotated[str, StringConstraints(min_length=1, pattern=r"\S")]
Rubles = Annotated[int, Field(ge=0)]
Limit = Annotated[int, Field(ge=1, le=100)]
Offset = Annotated[int, Field(ge=0)]
Age = Annotated[int, Field(ge=0, le=120)]
EventCategory = Literal[
    "concert",
    "theatre",
    "exhibition",
    "festival",
    "cinema",
    "excursion",
    "workshop",
    "lecture",
    "other",
]
Amount = Annotated[
    Decimal, Field(ge=0, max_digits=12, decimal_places=2, allow_inf_nan=False)
]


class Period(BaseModel):
    """Календарный период включительно, по местному времени события."""

    model_config = ConfigDict(extra="forbid")

    start: date = Field(description="Первый день, YYYY-MM-DD")
    end: date = Field(description="Последний день, YYYY-MM-DD; не раньше start")

    @model_validator(mode="after")
    def ordered_dates(self) -> Self:
        if self.end < self.start:
            raise ValueError("Конец периода не может быть раньше начала")
        return self


class PriceRange(BaseModel):
    """Бюджет в рублях включительно; max=0 означает бесплатные события."""

    model_config = ConfigDict(extra="forbid")

    min: Rubles | None = Field(default=None, description="Минимальная цена")
    max: Rubles | None = Field(default=None, description="Максимальная цена")

    @model_validator(mode="after")
    def ordered_prices(self) -> Self:
        if self.min is not None and self.max is not None and self.max < self.min:
            raise ValueError("Максимальная цена не может быть меньше минимальной")
        return self


class EventSummary(BaseModel):
    """Краткая карточка события в списке результатов."""

    id: UUID = Field(description="Идентификатор события")
    title: Text = Field(description="Название события")
    category: EventCategory
    start_date: date | None
    start_time: time | None
    venue: str
    age_limit: int | None


class EventCard(EventSummary):
    """Полная публичная карточка события."""

    tags: list[str] = Field(description="Тематические метки")
    date_start: AwareDatetime | None = Field(
        default=None,
        description="Начало с часовым поясом; null при неизвестном времени",
    )
    date_end: AwareDatetime | None = Field(
        default=None, description="Окончание, если известно"
    )
    end_date: date | None = None
    end_time: time | None = None
    time_zone: str = "Asia/Yekaterinburg"
    city: Text
    address: str
    lat: Annotated[float, Field(ge=-90, le=90)] | None
    lon: Annotated[float, Field(ge=-180, le=180)] | None
    duration_min: Annotated[int, Field(ge=0)] | None
    language: str | None
    org_link: HttpUrl | None = Field(description="Ссылка на организатора или билеты")
    status: Literal["published", "draft", "cancelled"]
    price_min: Amount | None = Field(description="Минимальная цена; null — неизвестна")
    price_max: Amount | None = Field(description="Максимальная цена; null — неизвестна")
    is_free: bool | None
    source_status: str = "Unknown"
    description: str
    source_url: HttpUrl | None = Field(
        description="Публичная ссылка на источник, если есть"
    )
    verified_at: AwareDatetime | None = Field(description="Когда проверен источник")


class SearchEventsResult(BaseModel):
    events: list[EventSummary] = Field(
        description="Найденные события, пустой список при отсутствии совпадений"
    )
    total: Annotated[int, Field(ge=0)] = Field(
        description="Всего совпадений до пагинации"
    )
    limit: Limit
    offset: Offset


class GetEventResult(BaseModel):
    event: EventCard | None = Field(
        description="Полная карточка; null, если событие не найдено"
    )


class SimilarEvent(BaseModel):
    event: EventSummary
    score: Annotated[float, Field(ge=0, le=1)] = Field(
        description="Степень сходства от 0 до 1"
    )


class FindSimilarResult(BaseModel):
    events: list[SimilarEvent] = Field(
        description="События по убыванию сходства, без исходного события"
    )


class GetVenueScheduleResult(SearchEventsResult):
    venue: Text
    city: Text | None
    period: Period


class SubscribeResult(BaseModel):
    subscription_id: UUID = Field(description="Идентификатор созданной подписки")
    status: Literal["active"]
    city: Text
    category: EventCategory | None
    period: Period
    created_at: AwareDatetime


class SaveFeedbackResult(BaseModel):
    feedback_id: UUID = Field(description="Идентификатор зарегистрированного обращения")
    event_id: UUID
    status: Literal["submitted"] = Field(
        description="Обращение зарегистрировано; доставка отдельно не подтверждается"
    )
    created_at: AwareDatetime
