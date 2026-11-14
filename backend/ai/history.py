"""Limit model context without modifying the persisted conversation."""

from collections.abc import Sequence

from langchain_core.messages import BaseMessage, HumanMessage


def sliding_window(
    messages: Sequence[BaseMessage], history_turns: int
) -> list[BaseMessage]:
    """Keep the current user turn and at most N preceding complete turns.

    A turn starts at a user message. Taking whole turns also preserves any
    tool-call/result pairs within the current agent turn.
    """
    if history_turns < 0:
        raise ValueError("history_turns должен быть >= 0")
    starts = [
        i for i, message in enumerate(messages) if isinstance(message, HumanMessage)
    ]
    if not starts:
        raise ValueError("Контекст должен содержать сообщение пользователя")
    start = starts[max(0, len(starts) - history_turns - 1)]
    return list(messages[start:])
