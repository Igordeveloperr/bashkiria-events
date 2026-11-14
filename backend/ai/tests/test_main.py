"""Real graph + GigaChat adapter + Langfuse exporter, with no external HTTP."""

import json
import os
import unittest
from unittest.mock import patch
from uuid import uuid4

import httpx
from config import ToolsConfig, load_config
from gigachat.models import ChatCompletion
from langchain_gigachat import GigaChat
from langfuse import Langfuse
from langfuse.langchain import CallbackHandler
from main import build_graph
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter
from tool_backend import ToolBackend
from tools import create_tools


class TracingTest(unittest.TestCase):
    def setUp(self):
        self.public_key = f"pk-lf-test-{uuid4()}"
        env = patch.dict(
            os.environ,
            {
                "GIGACHAT_CREDENTIALS": "test-sber-secret",
                "LANGFUSE_PUBLIC_KEY": self.public_key,
                "LANGFUSE_SECRET_KEY": "sk-lf-test-secret",
                "DATABASE_URL": "postgresql://test:test@localhost/test",
                "LANGFUSE_TRACING_ENABLED": "true",
            },
            clear=True,
        )
        env.start()
        self.addCleanup(env.stop)
        config = load_config()
        self.exporter = InMemorySpanExporter()
        self.langfuse = Langfuse(
            public_key=config.langfuse_public_key.get_secret_value(),
            secret_key=config.langfuse_secret_key.get_secret_value(),
            tracer_provider=TracerProvider(),
            span_exporter=self.exporter,
        )
        self.addCleanup(self.langfuse.shutdown)
        model = GigaChat(
            credentials=config.gigachat_credentials.get_secret_value(),
            model=config.gigachat_model,
        )
        self.addCleanup(model._client.close)
        self.model = model
        self.graph = build_graph(model)
        self.handler = CallbackHandler(public_key=self.public_key)
        self.config = {
            "callbacks": [self.handler],
            "run_name": "recommend-events",
            "metadata": {"langfuse_session_id": "chat-001"},
            "tags": ["bashkiria-events"],
        }

    def test_response_hierarchy_usage_and_unmodified_messages(self):
        response = ChatCompletion.model_validate(
            {
                "choices": [
                    {
                        "message": {"role": "assistant", "content": "Уточни даты"},
                        "index": 0,
                        "finish_reason": "stop",
                    }
                ],
                "created": 1,
                "model": "GigaChat-2",
                "object": "chat.completion",
                "usage": {
                    "prompt_tokens": 12,
                    "completion_tokens": 3,
                    "total_tokens": 15,
                },
            }
        )
        query = "test@example.com +7 (999) 123-45-67"
        with patch("gigachat.GigaChat.chat", return_value=response) as chat:
            result = self.graph.invoke(
                {"messages": [("user", query)]}, config=self.config
            )
        self.langfuse.flush()
        spans = self.exporter.get_finished_spans()
        self.assertEqual(chat.call_count, 1)
        self.assertEqual(result["messages"][-1].content, "Уточни даты")
        self.assertEqual(result["messages"][0].content, query)
        self.assertEqual(len({s.context.trace_id for s in spans}), 1)
        root = next(s for s in spans if s.parent is None)
        node = next(s for s in spans if s.name == "generate-response")
        generation = next(s for s in spans if s.name == "generate-answer")
        self.assertEqual(root.name, "recommend-events")
        self.assertEqual(node.parent.span_id, root.context.span_id)
        self.assertEqual(generation.parent.span_id, node.context.span_id)
        self.assertEqual(
            generation.attributes["langfuse.observation.type"], "generation"
        )
        self.assertEqual(
            generation.attributes["langfuse.observation.model.name"], "GigaChat-2"
        )
        usage = json.loads(generation.attributes["langfuse.observation.usage_details"])
        self.assertEqual(usage["input"], 12)
        self.assertEqual(usage["output"], 3)
        self.assertEqual(root.attributes["session.id"], "chat-001")
        exported = str([dict(s.attributes) for s in spans])
        self.assertIn(query, exported)
        for secret in ("test-sber-secret", "sk-lf-test-secret", self.public_key):
            self.assertNotIn(secret, exported)
        self.assertIsNotNone(self.handler.last_trace_id)

    def test_outgoing_function_schemas_only_use_gigachat_supported_formats(self):
        response = ChatCompletion.model_validate(
            {
                "choices": [
                    {
                        "message": {"role": "assistant", "content": "Привет"},
                        "index": 0,
                        "finish_reason": "stop",
                    }
                ],
                "created": 1,
                "model": "GigaChat-2",
                "object": "chat.completion",
                "usage": {
                    "prompt_tokens": 1,
                    "completion_tokens": 1,
                    "total_tokens": 2,
                },
            }
        )

        def check_formats(node):
            if isinstance(node, dict):
                if isinstance(node.get("format"), str):
                    self.assertIn(node["format"], ("date", "date-time", "time"))
                for value in node.values():
                    check_formats(value)
            elif isinstance(node, list):
                for value in node:
                    check_formats(value)

        def respond(request):
            # Inspect the SDK payload at the provider boundary, after binding.
            payload = request.model_dump(mode="json", exclude_none=True)
            check_formats(payload["functions"])
            functions = {item["name"]: item for item in payload["functions"]}
            self.assertEqual(len(functions), 6)
            for name in ("get_event", "find_similar", "save_feedback"):
                parameters = functions[name]["parameters"]
                self.assertEqual(parameters["properties"]["event_id"]["type"], "string")
                self.assertIn("event_id", parameters["required"])
            for name in ("search_events", "find_similar", "get_venue_schedule", "subscribe"):
                period = functions[name]["parameters"]["properties"]["period"]
                for key in ("start", "end"):
                    self.assertEqual(period["properties"][key]["format"], "date")
            return response

        with patch("gigachat.GigaChat.chat", side_effect=respond):
            result = self.graph.invoke(
                {"messages": [("user", "Привет")]}, config=self.config
            )
        self.assertEqual(result["messages"][-1].content, "Привет")

    def test_provider_error_is_traced_without_text_changes(self):
        with (
            patch(
                "gigachat.GigaChat.chat",
                side_effect=RuntimeError("test@example.com test-sber-secret"),
            ),
            self.assertRaises(RuntimeError),
        ):
            self.graph.invoke({"messages": [("user", "Привет")]}, config=self.config)
        self.langfuse.flush()
        spans = self.exporter.get_finished_spans()
        self.assertTrue(
            any(
                s.attributes.get("langfuse.observation.level") == "ERROR" for s in spans
            )
        )
        exported = str([dict(s.attributes) for s in spans])
        self.assertIn("test@example.com test-sber-secret", exported)

    def test_gigachat_function_call_and_result_are_traced_under_one_graph(self):
        def completion(message, finish_reason="stop"):
            return ChatCompletion.model_validate(
                {
                    "choices": [
                        {"message": message, "index": 0, "finish_reason": finish_reason}
                    ],
                    "created": 1,
                    "model": "GigaChat-2",
                    "object": "chat.completion",
                    "usage": {
                        "prompt_tokens": 12,
                        "completion_tokens": 3,
                        "total_tokens": 15,
                    },
                }
            )

        calls = [
            completion(
                {
                    "role": "assistant",
                    "content": "",
                    "function_call": {
                        "name": "search_events",
                        "arguments": {"city": "Уфа"},
                    },
                    "functions_state_id": "state-001",
                },
                "function_call",
            ),
            completion({"role": "assistant", "content": "Событий не найдено"}),
        ]
        with httpx.Client(
            transport=httpx.MockTransport(
                lambda request: httpx.Response(200, json={"items": [], "total": 0})
            )
        ) as client:
            backend = ToolBackend(
                ToolsConfig(
                    _env_file=None,
                    parser_api_key="secret-parser-key",
                    interaction_api_key="secret-interaction-key",
                ),
                client=client,
            )
            graph = build_graph(self.model, create_tools(backend))
            with patch("gigachat.GigaChat.chat", side_effect=calls) as chat:
                result = graph.invoke(
                    {"messages": [("user", "Найди концерты в Уфе")]}, config=self.config
                )
        self.assertEqual(chat.call_count, 2)
        self.assertEqual(result["messages"][-1].content, "Событий не найдено")
        sent = chat.call_args.args[0]
        self.assertEqual(sent.messages[-1].role, "function")
        self.assertEqual(json.loads(sent.messages[-1].content)["events"], [])
        self.langfuse.flush()
        spans = self.exporter.get_finished_spans()
        self.assertEqual(len({span.context.trace_id for span in spans}), 1)
        tool_span = next(span for span in spans if span.name == "search_events")
        node = next(span for span in spans if span.name == "tools")
        self.assertEqual(tool_span.parent.span_id, node.context.span_id)
        self.assertEqual(tool_span.attributes["langfuse.observation.type"], "tool")
        self.assertEqual(sum(span.name == "generate-answer" for span in spans), 2)
        exported = str([dict(span.attributes) for span in spans])
        for secret in (
            "secret-parser-key",
            "secret-interaction-key",
            "test-sber-secret",
            "sk-lf-test-secret",
        ):
            self.assertNotIn(secret, exported)


if __name__ == "__main__":
    unittest.main()
