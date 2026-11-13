"""Real graph + GigaChat adapter + Langfuse exporter, with no external HTTP."""

import json
import os
import unittest
from unittest.mock import patch
from uuid import uuid4

from config import load_config
from gigachat.models import ChatCompletion
from langchain_gigachat import GigaChat
from langfuse import Langfuse
from langfuse.langchain import CallbackHandler
from main import build_graph
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter


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
            public_key=config.langfuse_public_key,
            secret_key=config.langfuse_secret_key,
            tracer_provider=TracerProvider(),
            span_exporter=self.exporter,
        )
        self.addCleanup(self.langfuse.shutdown)
        model = GigaChat(
            credentials=config.gigachat_credentials, model=config.gigachat_model
        )
        self.addCleanup(model._client.close)
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

    def test_provider_error_is_traced_without_text_changes(self):
        with patch(
            "gigachat.GigaChat.chat",
            side_effect=RuntimeError("test@example.com test-sber-secret"),
        ), self.assertRaises(RuntimeError):
            self.graph.invoke(
                {"messages": [("user", "Привет")]}, config=self.config
            )
        self.langfuse.flush()
        spans = self.exporter.get_finished_spans()
        self.assertTrue(
            any(
                s.attributes.get("langfuse.observation.level") == "ERROR" for s in spans
            )
        )
        exported = str([dict(s.attributes) for s in spans])
        self.assertIn("test@example.com test-sber-secret", exported)


if __name__ == "__main__":
    unittest.main()
