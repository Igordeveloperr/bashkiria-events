"""Typed event tools, bound to shared service clients at agent startup."""

from uuid import UUID

from config import ToolsConfig
from langchain_core.runnables import RunnableConfig
from langchain_core.tools import tool
from models import (
    Age,
    EventCategory,
    FindSimilarResult,
    GetEventResult,
    GetVenueScheduleResult,
    Limit,
    Offset,
    Period,
    PriceRange,
    SaveFeedbackResult,
    SearchEventsResult,
    SubscribeResult,
    Text,
)
from pydantic import ConfigDict, ValidationError, create_model
from tool_backend import ToolBackend, ToolServiceError


def create_tools(backend: ToolBackend | None = None):
    def call(method, *args, **kwargs):
        try:
            if backend is not None:
                return getattr(backend, method)(*args, **kwargs)
            with ToolBackend(ToolsConfig()) as client:
                return getattr(client, method)(*args, **kwargs)
        except (ValidationError, KeyError, TypeError, ValueError):
            raise ToolServiceError(
                "invalid_service_response", "Сервис вернул некорректный ответ."
            ) from None

    @tool(parse_docstring=True)
    def search_events(
        city: Text | None = None,
        category: EventCategory | None = None,
        period: Period | None = None,
        price: PriceRange | None = None,
        age: Age | None = None,
        text: Text | None = None,
        limit: Limit = 10,
        offset: Offset = 0,
    ) -> SearchEventsResult:
        """Поиск опубликованных событий по фильтрам.

        Args:
            city: Город или населённый пункт.
            category: Категория мероприятия.
            period: Период проведения включительно, по времени Башкортостана.
            price: Допустимый бюджет в рублях.
            age: Возраст посетителя; возрастное ограничение события не выше этого значения.
            text: Свободный поисковый запрос.
            limit: Число результатов на странице.
            offset: Сколько результатов пропустить.

        Returns:
            Список events, общее число совпадений total, limit и offset.
        """
        return call(
            "search_events",
            city=city,
            category=category,
            period=period,
            price=price,
            age=age,
            text=text,
            limit=limit,
            offset=offset,
        )

    @tool(parse_docstring=True)
    def get_event(event_id: UUID) -> GetEventResult:
        """Получить полную публичную карточку события.

        Args:
            event_id: Идентификатор события.

        Returns:
            Полная карточка в поле event или null, если событие не найдено.
        """
        return call("get_event", event_id)

    @tool(parse_docstring=True)
    def find_similar(
        event_id: UUID,
        city: Text | None = None,
        period: Period | None = None,
        limit: Limit = 10,
    ) -> FindSimilarResult:
        """Найти похожие опубликованные события.

        Args:
            event_id: Исходное событие; оно исключается из выдачи.
            city: Город поиска похожих событий.
            period: Период проведения похожих событий.
            limit: Максимальное число результатов.

        Returns:
            Список events с карточками event и оценками score от 0 до 1 по убыванию сходства.
        """
        return call("find_similar", event_id, city=city, period=period, limit=limit)

    @tool(parse_docstring=True)
    def get_venue_schedule(
        venue: Text,
        period: Period,
        city: Text | None = None,
        limit: Limit = 10,
        offset: Offset = 0,
    ) -> GetVenueScheduleResult:
        """Получить расписание площадки за выбранный период.

        Args:
            venue: Точное название площадки.
            period: Период расписания включительно.
            city: Город для уточнения одноимённых площадок.
            limit: Число событий на странице.
            offset: Сколько событий пропустить.

        Returns:
            venue, city, period, список events по времени начала, total, limit и offset.
        """
        return call(
            "get_venue_schedule", venue, period, city=city, limit=limit, offset=offset
        )

    @tool(parse_docstring=True)
    def subscribe(
        city: Text,
        period: Period,
        config: RunnableConfig,
        category: EventCategory | None = None,
    ) -> SubscribeResult:
        """Подписать текущего пользователя после подтверждения в интерфейсе.

        Args:
            city: Город подписки.
            period: Период проведения интересующих событий.
            category: Категория; null означает все категории.

        Returns:
            subscription_id, status="active", city, category, period и created_at.
        """
        return call("subscribe", city, period, category, config)

    @tool(parse_docstring=True)
    def save_feedback(
        event_id: UUID, text: Text, config: RunnableConfig
    ) -> SaveFeedbackResult:
        """Зарегистрировать обращение текущего пользователя по поводу события.

        Args:
            event_id: Событие, которому адресовано обращение.
            text: Текст обращения до 5000 символов, явно порученный пользователем.

        Returns:
            feedback_id, event_id, status="submitted" и created_at; доставка не подтверждается.
        """
        return call("save_feedback", event_id, text, config)

    tools = [
        search_events,
        get_event,
        find_similar,
        get_venue_schedule,
        subscribe,
        save_feedback,
    ]
    for item in tools:
        # LangChain's inferred subset models omit the extra-fields policy.
        item.args_schema = create_model(
            f"{item.name}Args",
            __base__=item.args_schema,
            __config__=ConfigDict(extra="forbid"),
        )
    return tools


TOOLS = create_tools()
search_events, get_event, find_similar, get_venue_schedule, subscribe, save_feedback = (
    TOOLS
)
