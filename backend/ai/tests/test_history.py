"""Verify exactly what reaches the model, including the graph boundary."""

import unittest

from langchain_core.messages import AIMessage, HumanMessage, SystemMessage, ToolMessage

from history import sliding_window
from main import build_graph
from prompts import SYSTEM_PROMPT


class RecordingModel:
    def __init__(self):
        self.calls = []
        self.error = None
        self.on_invoke = None

    def invoke(self, messages, config=None):
        self.calls.append(messages)
        if self.on_invoke:
            self.on_invoke()
        if self.error:
            raise self.error
        return AIMessage(
            content=f"Ответ: {messages[-1].content}",
            usage_metadata={"input_tokens": 10, "output_tokens": 3, "total_tokens": 13},
        )


class HistoryTest(unittest.TestCase):
    def test_window_at_model_boundary_preserves_original_state(self):
        model = RecordingModel()
        graph = build_graph(model)
        messages = []
        for i in range(8):
            messages.extend([HumanMessage(content=f"u{i}"), AIMessage(content=f"a{i}")])
        messages.append(HumanMessage(content="current"))
        result = graph.invoke(
            {"messages": messages}, {"configurable": {"history_turns": 2}}
        )
        self.assertEqual(
            [m.content for m in model.calls[0]],
            [SYSTEM_PROMPT, "u6", "a6", "u7", "a7", "current"],
        )
        self.assertIsInstance(model.calls[0][0], SystemMessage)
        self.assertEqual(len(result["messages"]), 18)
        self.assertEqual(len(messages), 17)

    def test_zero_history_keeps_current_user_message(self):
        current = HumanMessage(content="new")
        messages = [HumanMessage(content="old"), AIMessage(content="answer"), current]
        self.assertEqual(sliding_window(messages, 0), [current])

    def test_turn_boundary_keeps_tool_exchange_together(self):
        messages = [
            HumanMessage(content="old"), AIMessage(content="old answer"),
            HumanMessage(content="find events"),
            AIMessage(content="", tool_calls=[{"name": "search", "args": {}, "id": "call-1"}]),
            ToolMessage(content="events", tool_call_id="call-1"),
            AIMessage(content="recommendation"), HumanMessage(content="which is free?"),
        ]
        self.assertEqual(sliding_window(messages, 1), messages[2:])

    def test_invalid_window_or_missing_current_message(self):
        for messages, turns in (([], 1), ([AIMessage(content="a")], 1),
                                ([HumanMessage(content="u")], -1)):
            with self.subTest(messages=messages, turns=turns), self.assertRaises(ValueError):
                sliding_window(messages, turns)
