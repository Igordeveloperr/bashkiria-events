"""Exercise all tools against the service contracts, without external requests."""

import json
import unittest
from decimal import Decimal
from uuid import UUID, uuid4

import httpx
from config import ToolsConfig
from langchain_core.messages import AIMessage, ToolMessage
from main import MAX_TOOL_ROUNDS, build_graph
from pydantic import ValidationError
from tool_backend import ToolBackend, ToolServiceError
from tools import create_tools

EVENT_ID = UUID("8276312c-cec5-4d62-b70c-d17f2b9df244")
SIMILAR_ID = UUID("8326312c-cec5-4d62-b70c-d17f2b9df244")
CITY_ID = str(uuid4())
PERIOD = {"start": "2035-01-10", "end": "2035-01-11"}


def event(**changes):
    return {
        "id": str(EVENT_ID),
        "title": "Джазовый вечер",
        "category": "Concert",
        "tags": ["джаз"],
        "date_start": "2035-01-10T19:00:00+05:00",
        "date_end": None,
        "start_date": "2035-01-10",
        "start_time": "19:00:00",
        "city": "Уфа",
        "venue": "ГКЗ Башкортостан",
        "price_min": "500.50",
        "price_max": None,
        "is_free": None,
        "age_limit": None,
        "description": "Концерт",
        "address": "ул. Ленина, 50",
        "latitude": "54.7",
        "longitude": "55.9",
        "duration_min": None,
        "language": None,
        "org_link": "https://tickets.example/event",
        "verified_at": "2035-01-01T00:00:00Z",
        "status": "Published",
        "source_status": "Scheduled",
        "manually_suppressed": False,
        "sources": [{"source_url": "https://events.example/event"}],
        "issues_json": {"private_parser_field": "not for the model"},
        **changes,
    }


class ToolFixture:
    def setUp(self):
        self.requests = []
        self.item = event()
        self.feedback_id, self.subscription_id = str(uuid4()), str(uuid4())
        self.config = {
            "configurable": {
                "user_id": "user-001",
                "session_id": "chat-001",
                "request_id": str(uuid4()),
                "subscription_consent": True,
            }
        }
        settings = ToolsConfig(
            _env_file=None,
            searcher_base_url="http://search.test",
            parser_base_url="http://parser.test",
            interaction_base_url="http://interaction.test",
            parser_api_key="private-parser-key",
            interaction_api_key="private-interaction-key",
        )
        client = httpx.Client(transport=httpx.MockTransport(self.respond))
        self.addCleanup(client.close)
        self.backend = ToolBackend(settings, client=client)
        self.tools = {item.name: item for item in create_tools(self.backend)}

    def respond(self, request):
        self.requests.append(request)
        path = request.url.path
        if path == "/events":
            return httpx.Response(200, json={"items": [self.item], "total": 11})
        if path == f"/events/{EVENT_ID}":
            return httpx.Response(200, json=self.item)
        if path.endswith("/similar"):
            return httpx.Response(
                200, json={"items": [event(id=str(SIMILAR_ID), score=0.7)], "total": 1}
            )
        if path == "/venues/schedule":
            return httpx.Response(
                200,
                json={"items": [self.item], "total": 5, "venue": "ГКЗ Башкортостан"},
            )
        if path == "/api/v1/parser/reference/cities":
            return httpx.Response(
                200,
                json={
                    "items": [
                        {"id": CITY_ID, "name": "Уфа", "aliases": [{"name": "Ufa"}]}
                    ],
                    "total": 1,
                },
            )
        if path == "/api/v1/interaction/me/subscriptions":
            body = json.loads(request.content)
            return httpx.Response(
                201,
                json={
                    "id": self.subscription_id,
                    "category": body["category"],
                    "dateFrom": body["dateFrom"],
                    "dateTo": body["dateTo"],
                    "createdAt": "2034-12-30T08:00:00Z",
                },
            )
        if path == "/api/v1/interaction/me/feedback":
            return httpx.Response(
                201,
                json={
                    "id": self.feedback_id,
                    "eventId": str(EVENT_ID),
                    "createdAt": "2034-12-30T08:00:00Z",
                },
            )
        return httpx.Response(404, json={"detail": "not found"})

    def invoke(self, name, args, config=None):
        return self.tools[name].invoke(args, config=config or self.config)


class ToolTest(ToolFixture, unittest.TestCase):
    def test_search_maps_all_filters_without_losing_zeroes_or_money(self):
        result = self.invoke(
            "search_events",
            {
                "city": "Уфа",
                "category": "concert",
                "period": PERIOD,
                "price": {"min": 0, "max": 0},
                "age": 0,
                "text": "джаз",
                "limit": 3,
                "offset": 7,
            },
        )
        query = self.requests[-1].url.params
        self.assertEqual(
            dict(query),
            {
                "public_only": "true",
                "page_size": "3",
                "offset": "7",
                "date_from": "2035-01-10T00:00:00+05:00",
                "date_to": "2035-01-12T00:00:00+05:00",
                "city": "Уфа",
                "categories": "Concert",
                "age_limit_max": "0",
                "q": "джаз",
                "price_from": "0",
                "price_to": "0",
            },
        )
        self.assertEqual((result.total, result.limit, result.offset), (11, 3, 7))
        self.assertEqual(result.events[0].price_min, Decimal("500.50"))
        self.assertIsNone(result.events[0].is_free)
        self.assertIsNone(result.events[0].age_limit)
        self.assertNotIn("issues_json", result.model_dump_json())
        self.assertNotIn("X-Parser-Key", self.requests[-1].headers)
        self.assertNotIn("X-Interaction-Key", self.requests[-1].headers)

    def test_get_event_maps_card_and_preserves_unknown_time(self):
        self.item.update(date_start=None, start_time=None)
        card = self.invoke("get_event", {"event_id": str(EVENT_ID)}).event
        self.assertEqual(card.category, "concert")
        self.assertEqual(card.lat, 54.7)
        self.assertEqual(str(card.source_url), "https://events.example/event")
        self.assertIsNone(card.date_start)
        self.assertIsNone(card.start_time)
        self.assertEqual(card.start_date.isoformat(), "2035-01-10")
        for changes in ({"status": "Draft"}, {"manually_suppressed": True}):
            with self.subTest(changes=changes):
                self.item = event(**changes)
                self.assertIsNone(
                    self.invoke("get_event", {"event_id": str(EVENT_ID)}).event
                )
        self.assertIsNone(self.invoke("get_event", {"event_id": str(uuid4())}).event)

    def test_similar_filters_before_limiting_and_returns_scores(self):
        result = self.invoke(
            "find_similar",
            {"event_id": str(EVENT_ID), "city": "Уфа", "period": PERIOD, "limit": 2},
        )
        query = self.requests[-1].url.params
        self.assertEqual(query["city"], "Уфа")
        self.assertEqual(query["page_size"], "2")
        self.assertEqual(query["public_only"], "true")
        self.assertEqual(query["date_to"], "2035-01-12T00:00:00+05:00")
        self.assertEqual(result.events[0].event.id, SIMILAR_ID)
        self.assertEqual(result.events[0].score, 0.7)

    def test_schedule_preserves_period_and_arbitrary_offset(self):
        result = self.invoke(
            "get_venue_schedule",
            {
                "venue": "ГКЗ Башкортостан",
                "period": PERIOD,
                "city": "Уфа",
                "offset": 3,
                "limit": 2,
            },
        )
        self.assertEqual(self.requests[-1].url.params["offset"], "3")
        self.assertEqual(result.period.start.isoformat(), PERIOD["start"])
        self.assertEqual((result.total, result.offset, result.limit), (5, 3, 2))

    def test_subscribe_uses_canonical_city_and_trusted_consent_and_identity(self):
        args = {"city": "Ufa", "period": PERIOD, "category": "lecture"}
        with self.assertRaises(ToolServiceError) as raised:
            self.invoke("subscribe", args, {"configurable": {"user_id": "user-001"}})
        self.assertEqual(raised.exception.code, "consent_required")
        self.assertEqual(self.requests, [])
        result = self.invoke("subscribe", args)
        reference_request, write = self.requests
        self.assertEqual(
            reference_request.headers["X-Parser-Key"], "private-parser-key"
        )
        self.assertEqual(write.headers["X-Interaction-Key"], "private-interaction-key")
        self.assertEqual(write.headers["X-User-Id"], "user-001")
        self.assertNotIn("X-Parser-Key", write.headers)
        self.assertEqual(
            json.loads(write.content),
            {
                "cityId": CITY_ID,
                "category": "lecture",
                "dateFrom": PERIOD["start"],
                "dateTo": PERIOD["end"],
                "channel": "web",
                "consentConfirmed": True,
            },
        )
        self.assertEqual(result.city, "Уфа")
        self.assertEqual(str(result.subscription_id), self.subscription_id)

    def test_model_cannot_supply_identity_consent_or_config(self):
        args = {"city": "Уфа", "period": PERIOD}
        for extra in (
            {"user_id": "other-user"},
            {"subscription_consent": True},
            {"config": self.config},
        ):
            with self.subTest(extra=extra), self.assertRaises(ValidationError):
                self.invoke("subscribe", {**args, **extra})
        self.assertEqual(self.requests, [])
        for item in self.tools.values():
            schema = item.tool_call_schema.model_json_schema()
            for hidden in (
                "user_id",
                "session_id",
                "request_id",
                "config",
                "subscription_consent",
            ):
                self.assertNotIn(hidden, schema["properties"])

    def test_feedback_is_idempotent_within_request_and_scoped_to_user(self):
        args = {"event_id": str(EVENT_ID), "text": "  Будет ли антракт?  "}
        first = self.invoke("save_feedback", args)
        self.invoke("save_feedback", args)
        writes = [r for r in self.requests if r.method == "POST"]
        self.assertEqual(
            writes[0].headers["Idempotency-Key"], writes[1].headers["Idempotency-Key"]
        )
        self.assertEqual(
            json.loads(writes[0].content),
            {
                "kind": "question",
                "eventId": str(EVENT_ID),
                "subject": "Вопрос о событии: Джазовый вечер",
                "message": "Будет ли антракт?",
            },
        )
        self.assertEqual(first.status, "submitted")
        other = {
            "configurable": {**self.config["configurable"], "user_id": "another-user"}
        }
        self.invoke("save_feedback", args, other)
        self.assertNotEqual(
            writes[0].headers["Idempotency-Key"],
            self.requests[-1].headers["Idempotency-Key"],
        )
        self.assertEqual(self.requests[-1].headers["X-User-Id"], "another-user")

    def test_invalid_feedback_or_context_does_not_write(self):
        for args, context in (
            ({"event_id": str(EVENT_ID), "text": "x" * 5001}, self.config),
            (
                {"event_id": str(EVENT_ID), "text": "Вопрос"},
                {"configurable": {"user_id": "user-001"}},
            ),
            (
                {"event_id": str(EVENT_ID), "text": "Вопрос"},
                {"configurable": {"user_id": "bad\r\nheader"}},
            ),
            ({"event_id": str(uuid4()), "text": "Вопрос"}, self.config),
        ):
            with self.subTest(args=args), self.assertRaises(ToolServiceError):
                self.invoke("save_feedback", args, context)
        self.assertFalse(any(r.method == "POST" for r in self.requests))

    def test_http_failures_and_invalid_responses_do_not_expose_private_data(self):
        for status in (400, 401, 409, 429, 500, 503):
            with (
                self.subTest(status=status),
                httpx.Client(
                    transport=httpx.MockTransport(
                        lambda request, status=status: httpx.Response(
                            status,
                            text="private-interaction-key secret service details",
                        )
                    )
                ) as client,
            ):
                tools = create_tools(ToolBackend(self.backend._config, client=client))
                with self.assertRaises(ToolServiceError) as raised:
                    tools[0].invoke({})
                self.assertNotIn("private", str(raised.exception))
        self.item.pop("title")
        with self.assertRaises(ToolServiceError) as raised:
            self.invoke("search_events", {})
        self.assertEqual(raised.exception.code, "invalid_service_response")


class ScriptedModel:
    def __init__(self, responses):
        self.responses = iter(responses)
        self.calls = []

    def bind_tools(self, tools):
        self.tools = tools
        return self

    def invoke(self, messages, config=None):
        self.calls.append(messages)
        return next(self.responses)


class GraphToolsTest(ToolFixture, unittest.TestCase):
    def test_all_six_tools_are_executed_and_results_returned_to_model(self):
        calls = [
            ("search_events", {"city": "Уфа"}),
            ("get_event", {"event_id": str(EVENT_ID)}),
            ("find_similar", {"event_id": str(EVENT_ID)}),
            ("get_venue_schedule", {"venue": "ГКЗ Башкортостан", "period": PERIOD}),
            ("subscribe", {"city": "Уфа", "period": PERIOD}),
            ("save_feedback", {"event_id": str(EVENT_ID), "text": "Будет ли антракт?"}),
        ]
        model = ScriptedModel(
            [
                AIMessage(
                    content="",
                    tool_calls=[
                        {"name": name, "args": args, "id": str(index)}
                        for index, (name, args) in enumerate(calls)
                    ],
                ),
                AIMessage(content="Готово"),
            ]
        )
        result = build_graph(model, list(self.tools.values())).invoke(
            {"messages": [("user", "Найди события")]}, self.config
        )
        messages = [m for m in model.calls[-1] if isinstance(m, ToolMessage)]
        self.assertEqual(len(messages), 6)
        self.assertTrue(all(message.status == "success" for message in messages))
        self.assertTrue(
            all(isinstance(json.loads(message.content), dict) for message in messages)
        )
        self.assertEqual(result["messages"][-1].content, "Готово")

    def test_invalid_arguments_and_unknown_tools_are_returned_as_errors(self):
        model = ScriptedModel(
            [
                AIMessage(
                    content="",
                    tool_calls=[
                        {
                            "name": "get_event",
                            "args": {"event_id": "invalid"},
                            "id": "bad-uuid",
                        },
                        {"name": "invented_tool", "args": {}, "id": "unknown"},
                    ],
                ),
                AIMessage(content="Уточни событие"),
            ]
        )
        build_graph(model, list(self.tools.values())).invoke(
            {"messages": [("user", "Помоги")]}, self.config
        )
        errors = [
            json.loads(m.content)["error"]["code"]
            for m in model.calls[-1]
            if isinstance(m, ToolMessage)
        ]
        self.assertEqual(errors, ["invalid_arguments", "unknown_tool"])
        self.assertEqual(self.requests, [])

    def test_repeated_tool_requests_stop_at_bounded_number_of_rounds(self):
        call = AIMessage(
            content="", tool_calls=[{"name": "search_events", "args": {}, "id": "loop"}]
        )
        model = ScriptedModel(
            [call.model_copy(deep=True) for _ in range(MAX_TOOL_ROUNDS + 1)]
        )
        result = build_graph(model, list(self.tools.values())).invoke(
            {"messages": [("user", "Помоги")]}, self.config
        )
        self.assertEqual(len(self.requests), MAX_TOOL_ROUNDS)
        self.assertFalse(result["messages"][-1].tool_calls)
        self.assertIn("допустимое число", result["messages"][-1].content)


if __name__ == "__main__":
    unittest.main()
