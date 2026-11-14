"""Application settings and credentials loaded from the environment."""

from pathlib import Path

from dotenv import load_dotenv
from pydantic import AnyHttpUrl, Field, SecretStr, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

BASE_DIR = Path(__file__).resolve().parent
# Preserve shell overrides and load tracing settings before SDK imports.
load_dotenv(BASE_DIR / ".env")


class ApiConfig(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=BASE_DIR / ".env",
        env_file_encoding="utf-8",
        alias_generator=str.upper,
        populate_by_name=True,
        extra="ignore",
        frozen=True,
        hide_input_in_errors=True,
    )

    api_cors_origins: list[str] = Field(
        default_factory=lambda: [
            "http://localhost:3000",
            "http://localhost:5173",
            "http://127.0.0.1:3000",
            "http://127.0.0.1:5173",
        ]
    )


class ToolsConfig(ApiConfig):
    searcher_base_url: AnyHttpUrl = AnyHttpUrl("http://127.0.0.1:8000")
    parser_base_url: AnyHttpUrl = AnyHttpUrl("http://127.0.0.1:5164")
    interaction_base_url: AnyHttpUrl = AnyHttpUrl("http://127.0.0.1:5266")
    parser_api_key: SecretStr = SecretStr("")
    interaction_api_key: SecretStr = SecretStr("")
    tool_http_timeout_seconds: float = Field(default=10, gt=0, le=60)


class Config(ToolsConfig):
    gigachat_credentials: SecretStr = Field(min_length=1)
    langfuse_public_key: SecretStr = Field(min_length=1)
    langfuse_secret_key: SecretStr = Field(min_length=1)
    database_url: SecretStr = Field(min_length=1)
    gigachat_model: str = "GigaChat-2"
    gigachat_scope: str = "GIGACHAT_API_PERS"
    gigachat_verify_ssl_certs: bool = True
    gigachat_ca_bundle_file: str | None = None
    langfuse_base_url: str = "https://cloud.langfuse.com"
    langfuse_environment: str = Field(
        default="development", validation_alias="LANGFUSE_TRACING_ENVIRONMENT"
    )
    chat_session_id: str = ""
    chat_user_id: str = ""
    chat_history_turns: int = Field(default=5, ge=0, le=100)

    @field_validator(
        "gigachat_credentials",
        "langfuse_public_key",
        "langfuse_secret_key",
        "database_url",
        mode="before",
    )
    @classmethod
    def strip_secrets(cls, value: object) -> object:
        if isinstance(value, SecretStr):
            value = value.get_secret_value()
        return value.strip() if isinstance(value, str) else value

    @field_validator("gigachat_ca_bundle_file")
    @classmethod
    def resolve_ca_bundle(cls, value: str | None) -> str | None:
        if value is None or not value.strip():
            return None
        ca_path = Path(value.strip()).expanduser()
        if not ca_path.is_absolute():
            ca_path = BASE_DIR / ca_path
        if not ca_path.is_file():
            raise ValueError("файл сертификата не найден: " + str(ca_path))
        return str(ca_path.resolve())


def load_config() -> Config:
    return Config()
