"""Adapters to the catalog, reference and interaction HTTP APIs."""

import hashlib
import re
import unicodedata
from datetime import datetime, time, timedelta, timezone
from uuid import NAMESPACE_URL, UUID, uuid5

import httpx
from langchain_core.tools import ToolException
from models import (
    EventCard,
    EventSummary,
    FindSimilarResult,
    GetEventResult,
    GetVenueScheduleResult,
    Period,
    SaveFeedbackResult,
    SearchEventsResult,
    SimilarEvent,
    SubscribeResult,
)
from pydantic import HttpUrl, TypeAdapter, ValidationError

EVENT_TIME_ZONE = timezone(timedelta(hours=5))


class ToolServiceError(ToolException):
    def __init__(self, code: str, message: str):
        self.code = code
        super().__init__(message)


def period_params(period: Period | None) -> dict:
    if period is None:
        return {}
    # Calendar dates are inclusive; the search API expects an exclusive upper bound.
    if period.end.year == 9999 and period.end.month == 12 and period.end.day == 31:
        raise ToolServiceError("invalid_period", "Укажи более близкую дату окончания.")
    return {
        "date_from": datetime.combine(
            period.start, time.min, EVENT_TIME_ZONE
        ).isoformat(),
        "date_to": datetime.combine(
            period.end + timedelta(days=1), time.min, EVENT_TIME_ZONE
        ).isoformat(),
    }


def city_key(value: str) -> str:
    value = re.sub(r"^(?:г\.\s*|г\s+|город\s+)", "", value.strip(), flags=re.IGNORECASE)
    value = unicodedata.normalize("NFKC", value).lower().replace("ё", "е")
    return re.sub(r"[^\w]+|_", " ", value).strip()


def public_event(event: dict) -> bool:
    return event.get("status") == "Published" and not event.get(
        "manually_suppressed", False
    )


def summary(event: dict) -> EventSummary:
    return EventSummary.model_validate({**event, "category": event["category"].lower()})


def http_url(value) -> str | None:
    try:
        return str(TypeAdapter(HttpUrl).validate_python(value)) if value else None
    except ValidationError:
        return None


class ToolBackend:
    """One thread-safe client per agent runtime; credentials never enter tool arguments."""

    def __init__(self, config, *, client: httpx.Client | None = None):
        self._config = config
        self._client = client or httpx.Client(timeout=config.tool_http_timeout_seconds)
        self._owns_client = client is None

    def __enter__(self):
        return self

    def __exit__(self, *_):
        if self._owns_client:
            self._client.close()

    def _request(
        self, service: str, method: str, path: str, *, missing_ok=False, **kwargs
    ):
        base = str(getattr(self._config, f"{service}_base_url")).rstrip("/")
        headers = dict(kwargs.pop("headers", {}))
        if service in ("parser", "interaction"):
            key = getattr(self._config, f"{service}_api_key").get_secret_value()
            if key:
                headers[f"X-{service.title()}-Key"] = key
        try:
            response = self._client.request(
                method, base + path, headers=headers, **kwargs
            )
            if response.status_code == 404 and missing_ok:
                return None
            if response.status_code >= 400:
                messages = {
                    400: (
                        "invalid_request",
                        "Сервис отклонил параметры. Уточни фильтры или период.",
                    ),
                    401: (
                        "service_configuration",
                        "Не настроен доступ агента к сервису.",
                    ),
                    403: ("service_configuration", "Агенту недоступна эта операция."),
                    404: ("not_found", "Событие или запись не найдены."),
                    409: (
                        "conflict",
                        "Операция конфликтует с существующей записью или достигнут лимит подписок.",
                    ),
                    422: (
                        "invalid_request",
                        "Сервис отклонил параметры. Уточни фильтры или период.",
                    ),
                    429: ("rate_limit", "Достигнут лимит запросов. Повтори позже."),
                }
                code, message = messages.get(
                    response.status_code,
                    (
                        "service_unavailable",
                        "Сервис временно недоступен. Повтори позже.",
                    ),
                )
                raise ToolServiceError(code, message)
            response.raise_for_status()
            return response.json()
        except httpx.HTTPError:
            # Do not return URLs, headers, API keys or raw service errors to the model.
            raise ToolServiceError(
                "service_unavailable", "Сервис временно недоступен. Повтори позже."
            ) from None
        except ValueError:
            raise ToolServiceError(
                "invalid_service_response", "Сервис вернул некорректный ответ."
            ) from None

    def search_events(
        self,
        *,
        city=None,
        category=None,
        period=None,
        price=None,
        age=None,
        text=None,
        limit=10,
        offset=0,
    ):
        params = {
            "public_only": True,
            "page_size": limit,
            "offset": offset,
            **period_params(period),
        }
        for key, value in (
            ("city", city),
            ("categories", category.title() if category else None),
            ("age_limit_max", min(age, 18) if age is not None else None),
            ("q", text),
        ):
            if value is not None:
                params[key] = value
        if price is not None:
            if price.min is not None:
                params["price_from"] = price.min
            if price.max is not None:
                params["price_to"] = price.max
        page = self._request("searcher", "GET", "/events", params=params)
        return SearchEventsResult(
            events=[summary(item) for item in page["items"]],
            total=page["total"],
            limit=limit,
            offset=offset,
        )

    def get_event(self, event_id: UUID) -> GetEventResult:
        event = self._request("searcher", "GET", f"/events/{event_id}", missing_ok=True)
        if event is None or not public_event(event):
            return GetEventResult(event=None)
        sources = [
            http_url(source.get("source_url")) for source in event.get("sources", [])
        ]
        card = EventCard.model_validate(
            {
                **summary(event).model_dump(),
                **{
                    key: event.get(key)
                    for key in (
                        "description",
                        "address",
                        "duration_min",
                        "language",
                        "verified_at",
                    )
                },
                "lat": event.get("latitude"),
                "lon": event.get("longitude"),
                "org_link": http_url(event.get("org_link")),
                "source_url": next((url for url in sources if url), None),
                "status": event["status"].lower(),
            }
        )
        return GetEventResult(event=card)

    def find_similar(self, event_id: UUID, *, city=None, period=None, limit=10):
        if self.get_event(event_id).event is None:
            raise ToolServiceError("not_found", "Исходное событие не найдено.")
        params = {"page_size": limit, "public_only": True, **period_params(period)}
        if city is not None:
            params["city"] = city
        page = self._request(
            "searcher", "GET", f"/events/{event_id}/similar", params=params
        )
        return FindSimilarResult(
            events=[
                SimilarEvent(event=summary(item), score=item["score"])
                for item in page["items"]
            ]
        )

    def get_venue_schedule(self, venue, period, *, city=None, limit=10, offset=0):
        params = {
            "venue": venue,
            "page_size": limit,
            "offset": offset,
            **period_params(period),
        }
        if city is not None:
            params["city"] = city
        page = self._request("searcher", "GET", "/venues/schedule", params=params)
        return GetVenueScheduleResult(
            venue=page["venue"],
            city=city,
            period=period,
            events=[summary(item) for item in page["items"]],
            total=page["total"],
            limit=limit,
            offset=offset,
        )

    @staticmethod
    def _identity(config):
        context = config.get("configurable", {})
        user = context.get("user_id", "")
        if not isinstance(user, str) or not re.fullmatch(
            r"[A-Za-z0-9_.:-]{1,128}", user
        ):
            raise ToolServiceError(
                "identity_required",
                "Для записи требуется идентификатор пользователя из доверенного контекста.",
            )
        return user, context

    def _city(self, name: str):
        key = city_key(name)
        found = []
        page = 1
        while True:
            result = self._request(
                "parser",
                "GET",
                "/api/v1/parser/reference/cities",
                params={"q": key, "page": page, "pageSize": 100},
            )
            found.extend(
                item
                for item in result["items"]
                if city_key(item["name"]) == key
                or any(
                    city_key(alias["name"]) == key for alias in item.get("aliases", [])
                )
            )
            if page * 100 >= result["total"]:
                break
            page += 1
            if page > 10:
                raise ToolServiceError(
                    "ambiguous_city", "Уточни полное название города."
                )
        unique = {item["id"]: item for item in found}
        if len(unique) != 1:
            raise ToolServiceError(
                "city_not_found",
                "Город не найден однозначно в справочнике. Уточни название.",
            )
        return next(iter(unique.values()))

    def subscribe(self, city, period, category, config):
        user, context = self._identity(config)
        if context.get("subscription_consent") is not True:
            raise ToolServiceError(
                "consent_required",
                "Подписка требует явного подтверждения в интерфейсе. Сам текст диалога не считается подтверждением.",
            )
        reference = self._city(city)
        result = self._request(
            "interaction",
            "POST",
            "/api/v1/interaction/me/subscriptions",
            headers={"X-User-Id": user},
            json={
                "cityId": reference["id"],
                "category": category,
                "dateFrom": period.start.isoformat(),
                "dateTo": period.end.isoformat(),
                "channel": "web",
                "consentConfirmed": True,
            },
        )
        return SubscribeResult(
            subscription_id=result["id"],
            status="active",
            city=reference["name"],
            category=result["category"],
            period=Period(start=result["dateFrom"], end=result["dateTo"]),
            created_at=result["createdAt"],
        )

    def save_feedback(self, event_id, text, config):
        user, context = self._identity(config)
        try:
            request_id = UUID(str(context["request_id"]))
            session_id = context["session_id"]
            if not isinstance(session_id, str) or not session_id.strip():
                raise ValueError
        except (KeyError, ValueError, TypeError):
            raise ToolServiceError(
                "request_context_required",
                "Для обращения требуется контекст запроса и диалога.",
            ) from None
        text = text.strip()
        if len(text) > 5000:
            raise ToolServiceError(
                "invalid_feedback", "Сократи обращение до 5000 символов."
            )
        event = self.get_event(event_id).event
        if event is None:
            raise ToolServiceError("not_found", "Событие для обращения не найдено.")
        digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
        key = uuid5(
            NAMESPACE_URL,
            f"feedback:{user}:{session_id}:{request_id}:{event_id}:{digest}",
        )
        result = self._request(
            "interaction",
            "POST",
            "/api/v1/interaction/me/feedback",
            headers={"X-User-Id": user, "Idempotency-Key": str(key)},
            json={
                "kind": "question",
                "eventId": str(event_id),
                "subject": ("Вопрос о событии: " + event.title)[:200],
                "message": text,
            },
        )
        return SaveFeedbackResult(
            feedback_id=result["id"],
            event_id=result["eventId"],
            status="submitted",
            created_at=result["createdAt"],
        )
