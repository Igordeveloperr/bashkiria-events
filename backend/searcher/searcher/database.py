from collections.abc import Iterator

from fastapi import Request
from sqlalchemy import create_engine, text
from sqlalchemy.orm import Session, sessionmaker

from searcher.config import Settings


def create_session_factory(settings: Settings):
    engine = create_engine(
        settings.sqlalchemy_url(),
        pool_pre_ping=True,
        pool_size=settings.database_pool_size,
        max_overflow=settings.database_max_overflow,
        pool_timeout=5,
        isolation_level="REPEATABLE READ",
        connect_args={"connect_timeout": 5},
    )
    return engine, sessionmaker(engine, autoflush=False, expire_on_commit=False)


def get_session(request: Request) -> Iterator[Session]:
    with request.app.state.sessions() as session, session.begin():
        # Count, page and sources see one snapshot even while the parser writes.
        session.execute(text("SET TRANSACTION READ ONLY"))
        session.execute(
            text("SELECT set_config('statement_timeout', :timeout, true)"),
            {"timeout": str(request.app.state.settings.database_statement_timeout_ms)},
        )
        yield session
