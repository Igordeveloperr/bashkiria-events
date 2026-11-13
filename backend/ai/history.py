"""Limit model context without modifying the persisted conversation."""

from collections.abc import Sequence

from langchain_core.messages import BaseMessage, HumanMessage


def sliding_window(
    messages: Sequence[BaseMessage], history_turns: int
) -> list[BaseMessage]:
    """Keep the current user turn and at most N preceding complete turns.

    A turn starts at a user message. Taking whole turns also preserves any
    tool-call/result pairs when the graph eventually gains tools.
    """
    if history_turns < 0:
        raise ValueError("history_turns должен быть >= 0")
    if not messages or not isinstance(messages[-1], HumanMessage):
        raise ValueError("Контекст должен заканчиваться сообщением пользователя")
    starts = [i for i, message in enumerate(messages) if isinstance(message, HumanMessage)]
    start = starts[max(0, len(starts) - history_turns - 1)]
    return list(messages[start:])
