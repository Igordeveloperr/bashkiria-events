"""Event tool prototypes; implementations are intentionally absent."""

from uuid import UUID

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
    """Поиск событий по фильтрам.

    Args:
        city: Город или населённый пункт.
        category: Категория мероприятия.
        period: Период проведения включительно.
        price: Допустимый бюджет в рублях.
        age: Возраст посетителя; возрастное ограничение события не выше этого значения.
        text: Свободный поисковый запрос.
        limit: Число результатов на странице.
        offset: Сколько результатов пропустить.

    Returns:
        Список events, общее число совпадений total, limit и offset.
    """
    raise NotImplementedError("search_events: доступен только контракт")


@tool(parse_docstring=True)
def get_event(event_id: UUID) -> GetEventResult:
    """Получить полную карточку события.

    Args:
        event_id: Идентификатор события.

    Returns:
        Полная карточка в поле event или null, если событие не найдено.
    """
    raise NotImplementedError("get_event: доступен только контракт")


@tool(parse_docstring=True)
def find_similar(
    event_id: UUID,
    city: Text | None = None,
    period: Period | None = None,
    limit: Limit = 10,
) -> FindSimilarResult:
    """Найти похожие события.

    Args:
        event_id: Исходное событие; оно исключается из выдачи.
        city: Город поиска похожих событий.
        period: Период проведения похожих событий.
        limit: Максимальное число результатов.

    Returns:
        Список events с карточками event и оценками score от 0 до 1 по убыванию сходства.
    """
    raise NotImplementedError("find_similar: доступен только контракт")


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
        venue: Название площадки.
        period: Период расписания включительно.
        city: Город для уточнения одноимённых площадок.
        limit: Число событий на странице.
        offset: Сколько событий пропустить.

    Returns:
        venue, city, period, список events по времени начала, total, limit и offset.
    """
    raise NotImplementedError("get_venue_schedule: доступен только контракт")


@tool(parse_docstring=True)
def subscribe(
    city: Text,
    period: Period,
    category: EventCategory | None = None,
) -> SubscribeResult:
    """Подписать текущего пользователя на события по фильтру.

    Args:
        city: Город подписки.
        period: Период проведения интересующих событий.
        category: Категория; null означает все категории.

    Returns:
        subscription_id, status="active", city, category, period и created_at.
    """
    raise NotImplementedError("subscribe: доступен только контракт")


@tool(parse_docstring=True)
def save_feedback(event_id: UUID, text: Text) -> SaveFeedbackResult:
    """Зарегистрировать обращение текущего пользователя к организатору события.

    Args:
        event_id: Событие, организатору которого адресовано обращение.
        text: Текст обращения к организатору.

    Returns:
        feedback_id, event_id, status="submitted" и created_at.
    """
    raise NotImplementedError("save_feedback: доступен только контракт")


TOOLS = [search_events, get_event, find_similar, get_venue_schedule, subscribe, save_feedback]
