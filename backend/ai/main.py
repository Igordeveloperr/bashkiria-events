"""Persistent GigaChat conversations with a bounded model context."""

import argparse
import sys
from uuid import uuid4

from chat import ChatRunFailedError, ChatService

# Import config first so .env is loaded before the tracing integration.
from config import load_config
from history import sliding_window
from langchain_core.messages import SystemMessage
from langchain_core.runnables import RunnableConfig
from langchain_gigachat import GigaChat
from langfuse import Langfuse
from langfuse.langchain import CallbackHandler
from langgraph.graph import END, START, MessagesState, StateGraph
from prompts import SYSTEM_PROMPT
from storage import ChatStore, RequestConflictError, SessionBusyError, validate_identity


def build_graph(model):
    def generate_response(state: MessagesState, config: RunnableConfig):
        history_turns = config.get("configurable", {}).get("history_turns", 5)
        response = model.invoke(
            [
                SystemMessage(content=SYSTEM_PROMPT),
                *sliding_window(state["messages"], history_turns),
            ],
            config={"run_name": "generate-answer"},
        )
        return {"messages": [response]}

    graph = StateGraph(MessagesState)
    graph.add_node("generate-response", generate_response)
    graph.add_edge(START, "generate-response")
    graph.add_edge("generate-response", END)
    return graph.compile()


def main(argv=None):
    """Run an interactive conversation using the same persistent chat service."""

    parser = argparse.ArgumentParser(description="Интерактивный чат с GigaChat")
    parser.add_argument("--user-id", help="ID пользователя или CHAT_USER_ID из .env")
    parser.add_argument(
        "--session-key", help="Ключ диалога или CHAT_SESSION_ID из .env"
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
        service = ChatService(
            ChatStore(config.database_url.get_secret_value()),
            build_graph(model),
            history_turns=config.chat_history_turns,
            callback_factory=lambda: CallbackHandler(
                public_key=config.langfuse_public_key.get_secret_value()
            ),
        )
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
    finally:
        try:
            langfuse.shutdown()
        finally:
            if model is not None:
                model._client.close()


if __name__ == "__main__":
    main()
