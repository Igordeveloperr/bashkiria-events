"""One LangGraph node calling Sber GigaChat, traced by Langfuse."""

import os
import re
import sys
from pathlib import Path

from dotenv import load_dotenv

# Load credentials before importing the tracing integration.
load_dotenv(Path(__file__).with_name(".env"))

from langchain_core.messages import SystemMessage
from langchain_gigachat import GigaChat
from langfuse import Langfuse
from langfuse.langchain import CallbackHandler
from langfuse.types import MaskOtelSpansResult, OtelSpanPatch
from langgraph.graph import END, START, MessagesState, StateGraph

PII = re.compile(
    r"\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b"
    r"|(?<!\w)(?:\+7|8)[\s(.-]*\d{3}[\s).-]*\d{3}[\s.-]*\d{2}[\s.-]*\d{2}(?!\w)"
)
SYSTEM_PROMPT = (
    "Ты помощник по культурным событиям Башкортостана. Отвечай кратко по-русски. "
    "Поиск событий пока не подключён: не выдумывай актуальные мероприятия, даты, "
    "цены и ссылки. Уточни город, даты и интересы пользователя."
)


def mask_spans(*, params):
    """Redact common PII and our API keys before telemetry leaves the process."""
    secrets = [
        os.getenv(key)
        for key in (
            "GIGACHAT_CREDENTIALS",
            "LANGFUSE_SECRET_KEY",
            "LANGFUSE_PUBLIC_KEY",
        )
    ]

    def redact(value):
        if isinstance(value, (tuple, list)):
            return tuple(redact(item) for item in value)
        if not isinstance(value, str):
            return value
        for secret in secrets:
            if secret:
                value = value.replace(secret, "[REDACTED]")
        return PII.sub("[REDACTED]", value)

    patches = {}
    for identifier, span in params.spans.items():
        changes = {key: redact(value) for key, value in span.attributes.items()}
        patches[identifier] = OtelSpanPatch(set_attributes=changes)
    return MaskOtelSpansResult(span_patches=patches)


def build_graph(model):
    def generate_response(state: MessagesState):
        response = model.invoke(
            [SystemMessage(content=SYSTEM_PROMPT), *state["messages"]],
            config={"run_name": "generate-answer"},
        )
        return {"messages": [response]}

    graph = StateGraph(MessagesState)
    graph.add_node("generate-response", generate_response)
    graph.add_edge(START, "generate-response")
    graph.add_edge("generate-response", END)
    return graph.compile()


def main():
    required = ("GIGACHAT_CREDENTIALS", "LANGFUSE_PUBLIC_KEY", "LANGFUSE_SECRET_KEY")
    missing = [key for key in required if not os.getenv(key, "").strip()]
    if missing:
        raise SystemExit("Заполни backend/ai/.env: " + ", ".join(missing))

    langfuse = Langfuse(
        environment=os.getenv("LANGFUSE_TRACING_ENVIRONMENT", "development"),
        mask_otel_spans=mask_spans,
    )
    handler = CallbackHandler()
    try:
        # The SDK reads GIGACHAT_* settings from .env and obtains/refreshes its token.
        model = GigaChat(model=os.getenv("GIGACHAT_MODEL", "GigaChat-2"), verify_ssl_certs=True)
        graph = build_graph(model)
        question = " ".join(sys.argv[1:]) or "Помоги выбрать мероприятие в Уфе"
        result = graph.invoke(
            {"messages": [("user", question)]},
            config={
                "callbacks": [handler],
                "run_name": "recommend-events",
                "tags": ["bashkiria-events"],
                "metadata": {"langfuse_session_id": os.getenv("CHAT_SESSION_ID", "")},
            },
        )
        print(result["messages"][-1].content)
        print(f"Trace ID: {handler.last_trace_id}")
    finally:
        # Drain the queue on success AND failure before the CLI process exits.
        langfuse.shutdown()


if __name__ == "__main__":
    main()
