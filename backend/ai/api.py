"""HTTP boundary for the persistent AI chat and its service-backed tools."""

from contextlib import asynccontextmanager
from typing import Annotated, Literal, get_type_hints
from uuid import UUID, uuid4

from chat import ChatRunFailedError, ChatService

# Load .env before importing the model/tracing runtime.
from config import ApiConfig, load_config
from fastapi import Depends, FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from main import create_chat_service
from psycopg import Error as DatabaseError
from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    StrictBool,
    StringConstraints,
    TypeAdapter,
)
from storage import RequestConflictError, SessionBusyError
from tools import TOOLS

Identity = Annotated[
    str, StringConstraints(min_length=1, max_length=200, pattern=r"\S")
]
MessageText = Annotated[str, StringConstraints(min_length=1, pattern=r"\S")]
ErrorCode = Literal[
    "session_busy", "request_conflict", "generation_failed", "storage_unavailable"
]


class ChatRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    user_id: Identity = Field(description="ID пользователя с фронтенда")
    session_id: Identity = Field(
        description="ID диалога с фронтенда, уникальный у пользователя"
    )
    message: MessageText = Field(description="Текст сообщения пользователя")
    subscription_consent: StrictBool = Field(
        default=False, description="Явное согласие на подписку, полученное интерфейсом"
    )
    request_id: UUID = Field(
        default_factory=uuid4,
        description="UUID сообщения; при сетевом повторе передай прежний UUID",
    )


class ChatResponse(BaseModel):
    user_id: str
    session_id: str = Field(
        description="Тот же внешний ID диалога, что пришёл в запросе"
    )
    request_id: UUID
    run_id: UUID
    reply: str
    trace_id: str | None
    cached: bool


class ErrorDetail(BaseModel):
    code: ErrorCode
    message: str
    request_id: UUID
    run_id: UUID | None = None


class ErrorResponse(BaseModel):
    detail: ErrorDetail


def get_chat_service(request: Request) -> ChatService:
    return request.app.state.chat_service


def create_app(
    service: ChatService | None = None, *, cors_origins: list[str] | None = None
) -> FastAPI:
    @asynccontextmanager
    async def lifespan(app: FastAPI):
        if service is not None:
            app.state.chat_service = service
            yield
        else:
            with create_chat_service(load_config()) as configured_service:
                app.state.chat_service = configured_service
                yield

    app = FastAPI(title="Bashkiria Events AI", version="0.1.0", lifespan=lifespan)
    app.add_middleware(
        CORSMiddleware,
        allow_origins=ApiConfig().api_cors_origins
        if cors_origins is None
        else cors_origins,
        allow_methods=["GET", "POST"],
        allow_headers=["Content-Type"],
    )

    @app.get("/health")
    def health() -> dict[str, str]:
        return {"status": "ok"}

    @app.post(
        "/api/chat",
        response_model=ChatResponse,
        summary="Отправить сообщение ИИ",
        responses={
            409: {
                "model": ErrorResponse,
                "description": "Диалог занят или request_id конфликтует",
            },
            502: {"model": ErrorResponse, "description": "Ошибка генерации ответа"},
            503: {
                "model": ErrorResponse,
                "description": "Хранилище переписки недоступно",
            },
        },
    )
    def chat(
        payload: ChatRequest, service: Annotated[ChatService, Depends(get_chat_service)]
    ) -> ChatResponse:
        # This synchronous route runs in FastAPI's thread pool: PG/model calls block.
        def fail(
            status: int, code: ErrorCode, message: str, run_id: UUID | None = None
        ) -> HTTPException:
            return HTTPException(
                status_code=status,
                detail=ErrorDetail(
                    code=code,
                    message=message,
                    request_id=payload.request_id,
                    run_id=run_id,
                ).model_dump(mode="json", exclude_none=True),
            )

        try:
            result = service.send_message(
                user_id=payload.user_id,
                session_key=payload.session_id,
                request_id=payload.request_id,
                content=payload.message,
                subscription_consent=payload.subscription_consent,
            )
        except SessionBusyError as error:
            raise fail(409, "session_busy", str(error)) from error
        except RequestConflictError as error:
            raise fail(409, "request_conflict", str(error)) from error
        except ChatRunFailedError as error:
            raise fail(
                502,
                "generation_failed",
                "Не удалось сформировать ответ. Для новой попытки используй новый request_id.",
                error.run_id,
            ) from error
        except DatabaseError as error:
            raise fail(
                503,
                "storage_unavailable",
                "Хранилище переписки недоступно; повтори позже.",
            ) from error
        return ChatResponse(
            user_id=payload.user_id,
            session_id=payload.session_id,
            request_id=result.request_id,
            run_id=result.run_id,
            reply=result.content,
            trace_id=result.trace_id,
            cached=result.cached,
        )

    @app.get("/api/tools", summary="Контракты инструментов агента")
    def tool_contracts() -> list[dict]:
        return [
            {
                "name": item.name,
                "description": item.description,
                "parameters": item.args_schema.model_json_schema(),
                "returns": TypeAdapter(get_type_hints(item.func)["return"]).json_schema(
                    mode="serialization"
                ),
                "implemented": True,
            }
            for item in TOOLS
        ]

    return app


app = create_app()
