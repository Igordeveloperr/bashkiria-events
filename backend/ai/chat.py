"""Conversation service shared by the CLI and HTTP API."""

import logging
from dataclasses import dataclass
from uuid import UUID

from langchain_core.messages import AIMessage, HumanMessage
from storage import ChatStore

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
        """The caller supplies user_id and the external conversation key.

        Reusing a request ID replays its result/status. To try again after a
        failed run, the caller must submit a new request ID.
        """
        if not isinstance(request_id, UUID):
            raise TypeError("request_id должен быть UUID")
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
                    raise TypeError("Ожидался текстовый ответ модели")
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
            except Exception as error:  # graph/framework code can raise arbitrary exceptions that must be persisted as chat run failures
                try:
                    conversation.fail_run(
                        run["id"],
                        str(error) or type(error).__name__,
                        getattr(handler, "last_trace_id", None),
                    )
                except Exception:  # noqa: BLE001 - persisting failure status must not mask the original run error
                    logger.error(
                        "Could not persist failed status for chat run %s", run["id"]
                    )
                raise ChatRunFailedError(run["id"], str(error)) from error
