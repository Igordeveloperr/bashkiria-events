"""Persistent GigaChat conversations with a bounded model context."""

import argparse
import json
import sys
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from uuid import uuid4

from chat import ChatRunFailedError, ChatService

# Import config first so .env is loaded before the tracing integration.
from config import Config, load_config
from history import sliding_window
from langchain_core.messages import AIMessage, SystemMessage, ToolMessage
from langchain_core.runnables import RunnableConfig
from langchain_gigachat import GigaChat
from langchain_gigachat.utils import convert_to_gigachat_tool
from langfuse import Langfuse
from langfuse.langchain import CallbackHandler
from langgraph.graph import END, START, MessagesState, StateGraph
from prompts import SYSTEM_PROMPT
from pydantic import ValidationError
from storage import ChatStore, RequestConflictError, SessionBusyError, validate_identity
from tool_backend import ToolBackend, ToolServiceError
from tools import TOOLS, create_tools

MAX_TOOL_ROUNDS = 6
MAX_CALLS_PER_ROUND = 8


class AgentState(MessagesState):
    tool_rounds: int


def _gigachat_tool_schema(item):
    """Adapt wire schemas while keeping the executable tools' typed validation."""

    def normalize(node):
        if isinstance(node, dict):
            result = {key: normalize(value) for key, value in node.items()}
            # GigaChat accepts temporal formats, but rejects UUID/URI formats.
            if isinstance(result.get("format"), str) and result["format"] not in (
                "date",
                "date-time",
                "time",
            ):
                result.pop("format")
            return result
        if isinstance(node, list):
            return [normalize(value) for value in node]
        return node

    return normalize(convert_to_gigachat_tool(item))


def build_graph(model, tools=None):
    tools = TOOLS if tools is None else tools
    model_with_tools = model.bind_tools([_gigachat_tool_schema(item) for item in tools])
    tools_by_name = {item.name: item for item in tools}

    def generate_response(state: MessagesState, config: RunnableConfig):
        history_turns = config.get("configurable", {}).get("history_turns", 5)
        today = datetime.now(timezone(timedelta(hours=5))).date().isoformat()
        response = model_with_tools.invoke(
            [
                SystemMessage(
                    content=SYSTEM_PROMPT + f" Сегодня {today} (Башкортостан, UTC+5)."
                ),
                *sliding_window(state["messages"], history_turns),
            ],
            config={"run_name": "generate-answer"},
        )
        if not isinstance(response, AIMessage):
            raise TypeError("Ожидалось сообщение модели")
        return {"messages": [response]}

    def execute_tools(state: AgentState, config: RunnableConfig):
        results = []
        for index, call in enumerate(state["messages"][-1].tool_calls):
            status = "success"
            try:
                if index >= MAX_CALLS_PER_ROUND:
                    raise ToolServiceError(
                        "tool_limit",
                        "Слишком много операций за один шаг. Уточни запрос.",
                    )
                selected = tools_by_name.get(call["name"])
                if selected is None:
                    raise ToolServiceError("unknown_tool", "Неизвестный инструмент.")
                result = selected.invoke(call["args"], config=config)
                content = result.model_dump_json()
            except (ToolServiceError, ValidationError) as error:
                status = "error"
                code = (
                    error.code
                    if isinstance(error, ToolServiceError)
                    else "invalid_arguments"
                )
                message = (
                    str(error)
                    if isinstance(error, ToolServiceError)
                    else "Проверь аргументы инструмента по его схеме."
                )
                content = json.dumps(
                    {"error": {"code": code, "message": message}}, ensure_ascii=False
                )
            results.append(
                ToolMessage(
                    content=content,
                    tool_call_id=call["id"],
                    name=call["name"],
                    status=status,
                )
            )
        return {"messages": results, "tool_rounds": state.get("tool_rounds", 0) + 1}

    def route(state: AgentState):
        if not state["messages"][-1].tool_calls:
            return END
        return (
            "tools" if state.get("tool_rounds", 0) < MAX_TOOL_ROUNDS else "tool-limit"
        )

    def tool_limit(state):
        return {
            "messages": [
                AIMessage(
                    content="Не удалось завершить запрос за допустимое число шагов. Уточни город, даты или нужное действие."
                )
            ]
        }

    graph = StateGraph(AgentState)
    graph.add_node("generate-response", generate_response)
    graph.add_node("tools", execute_tools)
    graph.add_node("tool-limit", tool_limit)
    graph.add_edge(START, "generate-response")
    graph.add_conditional_edges(
        "generate-response", route, ["tools", "tool-limit", END]
    )
    graph.add_edge("tools", "generate-response")
    graph.add_edge("tool-limit", END)
    return graph.compile()


@contextmanager
def create_chat_service(config: Config):
    """Share model/tracing setup and cleanup between HTTP and the CLI."""
    langfuse = Langfuse(
        public_key=config.langfuse_public_key.get_secret_value(),
        secret_key=config.langfuse_secret_key.get_secret_value(),
        base_url=config.langfuse_base_url,
        environment=config.langfuse_environment,
    )
    model = None
    try:
        model = GigaChat(
            credentials=config.gigachat_credentials.get_secret_value(),
            model=config.gigachat_model,
            scope=config.gigachat_scope,
            verify_ssl_certs=config.gigachat_verify_ssl_certs,
            ca_bundle_file=config.gigachat_ca_bundle_file,
        )
        with ToolBackend(config) as backend:
            yield ChatService(
                ChatStore(config.database_url.get_secret_value()),
                build_graph(model, create_tools(backend)),
                history_turns=config.chat_history_turns,
                callback_factory=lambda: CallbackHandler(
                    public_key=config.langfuse_public_key.get_secret_value()
                ),
            )
    finally:
        try:
            langfuse.shutdown()
        finally:
            if model is not None:
                model._client.close()


def main(argv=None):
    """Run an interactive conversation using the same persistent chat service."""

    parser = argparse.ArgumentParser(description="Интерактивный чат с GigaChat")
    parser.add_argument("--user-id", help="ID пользователя или CHAT_USER_ID из .env")
    parser.add_argument(
        "--session-key", help="Ключ диалога или CHAT_SESSION_ID из .env"
    )
    parser.add_argument(
        "--subscription-consent",
        action="store_true",
        help="Явно разрешить создание подписок в этом запуске CLI",
    )
    args = parser.parse_args(argv)

    # Load .env before importing the tracing integration.

    try:
        config = load_config()
        user_id = (
            args.user_id or config.chat_user_id or input("ID пользователя: ").strip()
        )
        session_key = args.session_key or config.chat_session_id
        if not session_key:
            session_key = input("Ключ диалога (Enter — новый): ").strip() or str(
                uuid4()
            )
        validate_identity(user_id, session_key)
    except (EOFError, KeyboardInterrupt):
        print()
        return
    except ValueError as error:
        parser.error(str(error))

    try:
        with create_chat_service(config) as service:
            print(f"Пользователь: {user_id}\nДиалог: {session_key}")
            print("Для выхода введи /exit или нажми Ctrl+C.")
            while True:
                content = input("Вы: ").strip()
                if content.lower() in {"/exit", "/quit"}:
                    break
                if not content:
                    continue
                try:
                    reply = service.send_message(
                        user_id=user_id,
                        session_key=session_key,
                        request_id=uuid4(),
                        content=content,
                        subscription_consent=args.subscription_consent,
                    )
                    print(f"Ассистент: {reply.content}")
                except (
                    ChatRunFailedError,
                    RequestConflictError,
                    SessionBusyError,
                    ValueError,
                ) as error:
                    print(f"Ошибка: {error}", file=sys.stderr)
    except (EOFError, KeyboardInterrupt):
        print()


if __name__ == "__main__":
    main()
