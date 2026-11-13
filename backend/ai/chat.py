"""Conversation service for a CLI or an authenticated HTTP backend."""

import logging
from dataclasses import dataclass
from uuid import UUID, uuid4

from langchain_core.messages import AIMessage, HumanMessage
from storage import ChatStore, RequestConflictError, SessionBusyError, validate_identity

logger = logging.getLogger(__name__)


class ChatRunFailedError(RuntimeError):
    def __init__(self, run_id: UUID, error: str):
        self.run_id = run_id
        super().__init__(error)


@dataclass(frozen=True)
class ChatReply:
    session_id: UUID
    run_id: UUID
    request_id: UUID
    content: str
    trace_id: str | None
    cached: bool = False


class ChatService:
    def __init__(
        self, store: ChatStore, graph, *, history_turns=5, callback_factory=None
    ):
        if not isinstance(history_turns, int) or not 0 <= history_turns <= 100:
            raise ValueError("history_turns должен быть от 0 до 100")
        self.store = store
        self.graph = graph
        self.history_turns = history_turns
        self.callback_factory = callback_factory

    def send_message(
        self, *, user_id: str, session_key: str, request_id: UUID, content: str
    ) -> ChatReply:
        """user_id must come from a verified identity at the HTTP boundary.

        Reusing a request ID replays its result/status. To try again after a
        failed run, the caller must submit a new request ID.
        """
        if not isinstance(request_id, UUID):
            raise ValueError("request_id должен быть UUID")
        if not isinstance(content, str) or not content.strip():
            raise ValueError("Сообщение не должно быть пустым")
        with self.store.conversation(user_id, session_key) as conversation:
            run, created = conversation.begin_run(request_id, content)
            if not created:
                if run["status"] == "failed":
                    raise ChatRunFailedError(run["id"], run["error"])
                answer = conversation.answer(run["id"])
                return ChatReply(
                    conversation.session_id,
                    run["id"],
                    request_id,
                    answer.content,
                    run["trace_id"],
                    cached=True,
                )
            handler = None
            try:
                handler = self.callback_factory() if self.callback_factory else None
                history = conversation.context(self.history_turns)
                messages = [
                    (
                        HumanMessage(content=m.content)
                        if m.role == "user"
                        else AIMessage(content=m.content)
                    )
                    for m in history
                ]
                messages.append(HumanMessage(content=content))
                result = self.graph.invoke(
                    {"messages": messages},
                    config={
                        "configurable": {"history_turns": self.history_turns},
                        "callbacks": [handler] if handler else [],
                        "run_name": "recommend-events",
                        "tags": ["bashkiria-events"],
                        "metadata": {
                            "langfuse_session_id": str(conversation.session_id),
                            "langfuse_user_id": user_id,
                            "chat_run_id": str(run["id"]),
                            "request_id": str(request_id),
                            "history_turns": self.history_turns,
                        },
                    },
                )
                response = result["messages"][-1]
                if not isinstance(response, AIMessage) or not isinstance(
                    response.content, str
                ):
                    raise ValueError("Ожидался текстовый ответ модели")
                trace_id = getattr(handler, "last_trace_id", None)
                metadata = response.model_dump(
                    mode="json", include={"usage_metadata", "response_metadata"}
                )
                answer = conversation.complete_run(
                    run["id"], response.content, metadata, trace_id
                )
                return ChatReply(
                    conversation.session_id,
                    run["id"],
                    request_id,
                    answer.content,
                    trace_id,
                )
            except Exception as error:
                try:
                    conversation.fail_run(
                        run["id"],
                        str(error) or type(error).__name__,
                        getattr(handler, "last_trace_id", None),
                    )
                except Exception:
                    logger.error(
                        "Could not persist failed status for chat run %s", run["id"]
                    )
                raise ChatRunFailedError(run["id"], str(error)) from error


def main(argv=None):
    """Run an interactive conversation using the same persistent chat service."""
    import argparse
    import sys

    parser = argparse.ArgumentParser(description="Интерактивный чат с GigaChat")
    parser.add_argument("--user-id", help="ID пользователя или CHAT_USER_ID из .env")
    parser.add_argument(
        "--session-key", help="Ключ диалога или CHAT_SESSION_ID из .env"
    )
    args = parser.parse_args(argv)

    # Load .env before importing the tracing integration.
    from config import load_config

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

    from langchain_gigachat import GigaChat
    from langfuse import Langfuse
    from langfuse.langchain import CallbackHandler
    from main import build_graph
    from psycopg import Error as DatabaseError

    langfuse = Langfuse(
        public_key=config.langfuse_public_key,
        secret_key=config.langfuse_secret_key,
        base_url=config.langfuse_base_url,
        environment=config.langfuse_environment,
    )
    model = None
    try:
        model = GigaChat(
            credentials=config.gigachat_credentials,
            model=config.gigachat_model,
            scope=config.gigachat_scope,
            verify_ssl_certs=config.gigachat_verify_ssl_certs,
            ca_bundle_file=config.gigachat_ca_bundle_file,
        )
        service = ChatService(
            ChatStore(config.database_url),
            build_graph(model),
            history_turns=config.chat_history_turns,
            callback_factory=lambda: CallbackHandler(
                public_key=config.langfuse_public_key
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
                DatabaseError,
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
