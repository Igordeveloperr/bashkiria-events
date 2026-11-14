from pathlib import Path

from pydantic import Field, SecretStr
from pydantic_settings import BaseSettings, SettingsConfigDict
from sqlalchemy.engine import URL, make_url


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=Path(__file__).resolve().parents[1] / ".env",
        env_file_encoding="utf-8",
        extra="ignore",
        hide_input_in_errors=True,
    )

    database_url: SecretStr | None = None
    pghost: str = "localhost"
    pgport: int = Field(default=5432, ge=1, le=65535)
    pguser: str = "bashkiria"
    pgpassword: SecretStr = SecretStr("bashkiria")
    pgdatabase: str = "bashkiria"
    database_pool_size: int = Field(default=10, ge=1, le=100)
    database_max_overflow: int = Field(default=10, ge=0, le=100)
    database_statement_timeout_ms: int = Field(default=10000, ge=1, le=120000)

    def sqlalchemy_url(self) -> URL:
        if self.database_url:
            url = make_url(self.database_url.get_secret_value())
            if url.drivername not in {"postgres", "postgresql", "postgresql+psycopg"}:
                raise ValueError("Searcher requires PostgreSQL with psycopg")
            return url.set(drivername="postgresql+psycopg")
        return URL.create(
            "postgresql+psycopg",
            username=self.pguser,
            password=self.pgpassword.get_secret_value(),
            host=self.pghost,
            port=self.pgport,
            database=self.pgdatabase,
        )
