import logging
from contextlib import asynccontextmanager
from typing import Annotated
from uuid import UUID

from fastapi import Depends, FastAPI, Query, Request
from fastapi.responses import JSONResponse
from sqlalchemy import text
from sqlalchemy.exc import SQLAlchemyError
from sqlalchemy.orm import Session

from searcher.config import Settings
from searcher.database import create_session_factory, get_session
from searcher.schemas import (
    EventPage,
    EventResponse,
    SchedulePage,
    ScheduleQuery,
    SearchQuery,
    ShortEventPage,
    ShortSimilarPage,
    SimilarPage,
    SimilarQuery,
)
from searcher.service import EventNotFoundError, EventSearchService

logger = logging.getLogger(__name__)
DatabaseSession = Annotated[Session, Depends(get_session)]


def create_app(settings: Settings | None = None) -> FastAPI:
    configured = settings or Settings()

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        app.state.settings = configured
        engine, app.state.sessions = create_session_factory(app.state.settings)
        try:
            yield
        finally:
            engine.dispose()

    app = FastAPI(
        title="Bashkiria Events Search",
        version="0.1.0",
        description="Полные события, поиск по фильтрам, расписания площадок и похожие мероприятия.",
        lifespan=lifespan,
        root_path=configured.root_path,
    )

    @app.exception_handler(EventNotFoundError)
    async def event_not_found(request: Request, error: EventNotFoundError):
        return JSONResponse(status_code=404, content={"detail": "Событие не найдено"})

    @app.exception_handler(SQLAlchemyError)
    async def database_unavailable(request: Request, error: SQLAlchemyError):
        # Do not expose SQL parameters, connection strings or parser data.
        logger.error("Search database request failed: %s", type(error).__name__)
        return JSONResponse(
            status_code=503, content={"detail": "База событий временно недоступна"}
        )

    @app.get("/health", tags=["health"], operation_id="health")
    def health(session: DatabaseSession):
        session.execute(text("SELECT 1"))
        return {"status": "ok"}

    @app.get(
        "/events",
        response_model=EventPage | ShortEventPage,
        tags=["events"],
        operation_id="search_events",
    )
    def search_events(query: Annotated[SearchQuery, Query()], session: DatabaseSession):
        return EventSearchService(session).search_events(query)

    @app.get(
        "/venues/schedule",
        response_model=SchedulePage,
        tags=["venues"],
        operation_id="get_venue_schedule",
    )
    def get_venue_schedule(
        query: Annotated[ScheduleQuery, Query()], session: DatabaseSession
    ):
        return EventSearchService(session).get_venue_schedule(query)

    @app.get(
        "/events/{event_id}/similar",
        response_model=SimilarPage | ShortSimilarPage,
        tags=["events"],
        operation_id="find_similar",
    )
    def find_similar(
        event_id: UUID,
        query: Annotated[SimilarQuery, Query()],
        session: DatabaseSession,
    ):
        return EventSearchService(session).find_similar(event_id, query)

    @app.get(
        "/events/{event_id}",
        response_model=EventResponse,
        tags=["events"],
        operation_id="get_event",
    )
    def get_event(event_id: UUID, session: DatabaseSession):
        return EventSearchService(session).get_event(event_id)

    return app


app = create_app()
