"""HTTP contracts without database or provider requests."""

import unittest
from contextlib import contextmanager
from unittest.mock import Mock, patch
from uuid import UUID, uuid4

from api import create_app
from chat import ChatReply, ChatRunFailedError, ChatService
from fastapi.testclient import TestClient
from psycopg import OperationalError
from storage import RequestConflictError, SessionBusyError


class ChatApiTest(unittest.TestCase):
    def setUp(self):
        self.service = Mock(spec=ChatService)
        self.internal_session = uuid4()
        self.run_id = uuid4()
        self.service.send_message.side_effect = lambda **kwargs: ChatReply(
            session_id=self.internal_session,
            run_id=self.run_id,
            request_id=kwargs["request_id"],
            content="Уточни даты",
            trace_id=None,
        )
        self.payload = {
            "user_id": "user-001",
            "session_id": "front-chat-001",
            "message": "Что посмотреть в Уфе?",
            "request_id": str(uuid4()),
        }
        self.client = TestClient(
            create_app(self.service, cors_origins=["http://frontend.test"])
        )
        self.client.__enter__()
        self.addCleanup(self.client.__exit__, None, None, None)

    def test_frontend_ids_are_forwarded_and_external_session_is_returned(self):
        response = self.client.post("/api/chat", json=self.payload)
        self.assertEqual(response.status_code, 200)
        self.service.send_message.assert_called_once_with(
            user_id=self.payload["user_id"],
            session_key=self.payload["session_id"],
            request_id=UUID(self.payload["request_id"]),
            content=self.payload["message"],
            subscription_consent=False,
        )
        body = response.json()
        self.assertEqual(body["user_id"], self.payload["user_id"])
        self.assertEqual(body["session_id"], self.payload["session_id"])
        self.assertNotEqual(body["session_id"], str(self.internal_session))
        self.assertEqual(body["request_id"], self.payload["request_id"])
        self.assertEqual(body["reply"], "Уточни даты")
        self.assertFalse(body["cached"])
        self.assertIsNone(body["trace_id"])

    def test_minimal_request_generates_and_returns_request_uuid(self):
        del self.payload["request_id"]
        response = self.client.post("/api/chat", json=self.payload)
        self.assertEqual(response.status_code, 200)
        generated_id = UUID(response.json()["request_id"])
        self.assertEqual(
            self.service.send_message.call_args.kwargs["request_id"], generated_id
        )

    def test_explicit_subscription_consent_is_forwarded_separately_from_text(self):
        response = self.client.post(
            "/api/chat", json={**self.payload, "subscription_consent": True}
        )
        self.assertEqual(response.status_code, 200)
        self.assertIs(
            self.service.send_message.call_args.kwargs["subscription_consent"], True
        )
        for invalid in ("true", 1, None):
            with self.subTest(value=invalid):
                self.assertEqual(
                    self.client.post(
                        "/api/chat",
                        json={**self.payload, "subscription_consent": invalid},
                    ).status_code,
                    422,
                )

    def test_invalid_payload_never_reaches_chat_service(self):
        invalid = [
            {key: value for key, value in self.payload.items() if key != missing}
            for missing in ("user_id", "session_id", "message")
        ]
        invalid.extend(
            {**self.payload, field: value}
            for field, values in (
                ("user_id", (None, "", " \t", 123, "u" * 201)),
                ("session_id", (None, "", "\n", 123, "s" * 201)),
                ("message", (None, "", "\n\t ", ["hello"])),
                ("request_id", (None, "invalid-uuid")),
            )
            for value in values
        )
        invalid.append({**self.payload, "unknown": "unexpected"})
        for payload in invalid:
            with self.subTest(payload=payload):
                self.assertEqual(
                    self.client.post("/api/chat", json=payload).status_code, 422
                )
        self.service.send_message.assert_not_called()

    def test_service_errors_have_stable_codes_and_do_not_expose_backend_details(self):
        cases = (
            (SessionBusyError("Диалог занят"), 409, "session_busy"),
            (
                RequestConflictError("UUID использован с другим текстом"),
                409,
                "request_conflict",
            ),
            (
                ChatRunFailedError(self.run_id, "provider-private-value"),
                502,
                "generation_failed",
            ),
            (OperationalError("database-private-value"), 503, "storage_unavailable"),
        )
        for error, status, code in cases:
            with self.subTest(code=code):
                self.service.send_message.side_effect = error
                response = self.client.post("/api/chat", json=self.payload)
                self.assertEqual(response.status_code, status)
                detail = response.json()["detail"]
                self.assertEqual(detail["code"], code)
                self.assertEqual(detail["request_id"], self.payload["request_id"])
                self.assertNotIn("private-value", response.text)
                if isinstance(error, ChatRunFailedError):
                    self.assertEqual(detail["run_id"], str(self.run_id))

    def test_cached_reply_is_passed_through(self):
        self.service.send_message.side_effect = None
        self.service.send_message.return_value = ChatReply(
            session_id=self.internal_session,
            run_id=self.run_id,
            request_id=UUID(self.payload["request_id"]),
            content="Сохранённый ответ",
            trace_id="trace-123",
            cached=True,
        )
        response = self.client.post("/api/chat", json=self.payload)
        self.assertEqual(response.status_code, 200)
        self.assertTrue(response.json()["cached"])
        self.assertEqual(response.json()["trace_id"], "trace-123")

    def test_cors_preflight_and_response_allow_configured_frontend(self):
        headers = {
            "Origin": "http://frontend.test",
            "Access-Control-Request-Method": "POST",
            "Access-Control-Request-Headers": "content-type",
        }
        response = self.client.options("/api/chat", headers=headers)
        self.assertEqual(response.status_code, 200)
        self.assertEqual(
            response.headers["access-control-allow-origin"], "http://frontend.test"
        )
        response = self.client.post(
            "/api/chat", json=self.payload, headers={"Origin": headers["Origin"]}
        )
        self.assertEqual(
            response.headers["access-control-allow-origin"], "http://frontend.test"
        )
        response = self.client.options(
            "/api/chat", headers={**headers, "Origin": "http://unlisted.test"}
        )
        self.assertEqual(response.status_code, 400)
        self.assertNotIn("access-control-allow-origin", response.headers)

    def test_tool_catalog_contains_self_contained_input_and_output_schemas(self):
        response = self.client.get("/api/tools")
        self.assertEqual(response.status_code, 200)
        contracts = response.json()
        self.assertEqual(
            {contract["name"] for contract in contracts},
            {
                "search_events",
                "get_event",
                "find_similar",
                "get_venue_schedule",
                "subscribe",
                "save_feedback",
            },
        )

        def check_references(node, root):
            if isinstance(node, dict):
                if "$ref" in node:
                    target = root
                    self.assertTrue(node["$ref"].startswith("#/"))
                    for part in node["$ref"][2:].split("/"):
                        target = target[part]
                for value in node.values():
                    check_references(value, root)
            elif isinstance(node, list):
                for value in node:
                    check_references(value, root)

        for contract in contracts:
            self.assertTrue(contract["implemented"])
            self.assertTrue(contract["description"])
            for key in ("parameters", "returns"):
                check_references(contract[key], contract[key])
                self.assertEqual(contract[key]["type"], "object")
        for contract in contracts:
            if contract["name"] in ("search_events", "find_similar"):
                self.assertNotIn("short", contract["parameters"]["properties"])
                event_schema = contract["returns"]["$defs"]["EventSummary"]
                fields = {
                    "id",
                    "title",
                    "category",
                    "start_date",
                    "start_time",
                    "venue",
                    "age_limit",
                }
                self.assertEqual(set(event_schema["properties"]), fields)
                self.assertEqual(set(event_schema["required"]), fields)
        self.service.send_message.assert_not_called()

    def test_lifespan_creates_one_service_and_closes_it(self):
        events = []

        @contextmanager
        def runtime(config):
            events.append("start")
            yield self.service
            events.append("stop")

        with (
            patch("api.load_config") as load,
            patch("api.create_chat_service", side_effect=runtime) as factory,
        ):
            app = create_app(cors_origins=[])
            with TestClient(app) as client:
                for _ in range(2):
                    self.assertEqual(
                        client.post("/api/chat", json=self.payload).status_code, 200
                    )
                self.assertEqual(events, ["start"])
            self.assertEqual(events, ["start", "stop"])
            load.assert_called_once_with()
            factory.assert_called_once_with(load.return_value)


if __name__ == "__main__":
    unittest.main()
