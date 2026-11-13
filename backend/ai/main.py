"""Persistent GigaChat conversations with a bounded model context."""

import argparse
import sys
from uuid import UUID, uuid4

# Import config first so .env is loaded before the tracing integration.
from config import load_config
from history import sliding_window
from langchain_core.messages import SystemMessage
from langchain_core.runnables import RunnableConfig
from langchain_gigachat import GigaChat
from langfuse import Langfuse
from langfuse.langchain import CallbackHandler
from langgraph.graph import END, START, MessagesState, StateGraph
from prompts import DEFAULT_QUESTION, SYSTEM_PROMPT
from storage import ChatStore, RequestConflictError, SessionBusyError


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


def main():
    from chat import ChatRunFailedError, ChatService

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--user-id", help="Verified user ID (or CHAT_USER_ID)")
    parser.add_argument("--session-key", help="Conversation key (or CHAT_SESSION_ID)")
    parser.add_argument("--request-id", type=UUID, help="Stable UUID for retries of this message")
    parser.add_argument("question", nargs="*", help="Message text")
    args = parser.parse_args()
    try:
        config = load_config()
    except ValueError as error:
        raise SystemExit(str(error)) from None

    user_id = args.user_id or config.chat_user_id
    session_key = args.session_key or config.chat_session_id
    if not user_id or not session_key:
        parser.error("Укажи --user-id и --session-key или CHAT_USER_ID и CHAT_SESSION_ID")

    langfuse = Langfuse(
        public_key=config.langfuse_public_key,
        secret_key=config.langfuse_secret_key,
        base_url=config.langfuse_base_url,
        environment=config.langfuse_environment,
    )
    try:
        # The SDK obtains and refreshes its access token using these credentials.
        model = GigaChat(
            credentials=config.gigachat_credentials,
            model=config.gigachat_model,
            scope=config.gigachat_scope,
            verify_ssl_certs=config.gigachat_verify_ssl_certs,
            ca_bundle_file=config.gigachat_ca_bundle_file,
        )
        graph = build_graph(model)
        service = ChatService(
            ChatStore(config.database_url), graph,
            history_turns=config.chat_history_turns,
            callback_factory=lambda: CallbackHandler(public_key=config.langfuse_public_key),
        )
        request_id = args.request_id or uuid4()
        print(f"Request ID: {request_id}", file=sys.stderr)
        reply = service.send_message(
            user_id=user_id, session_key=session_key, request_id=request_id,
            content=" ".join(args.question) or DEFAULT_QUESTION,
        )
        print(reply.content)
        print(f"Session ID: {reply.session_id}")
        print(f"Trace ID: {reply.trace_id}")
        if reply.cached:
            print("Cached response")
    except (ChatRunFailedError, RequestConflictError, SessionBusyError, ValueError) as error:
        raise SystemExit(str(error)) from None
    finally:
        # Drain the queue on success AND failure before the CLI process exits.
        langfuse.shutdown()


if __name__ == "__main__":
    main()
