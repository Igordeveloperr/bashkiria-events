"""Application settings and credentials loaded from the environment."""

import os
from dataclasses import dataclass, field
from pathlib import Path

from dotenv import load_dotenv

BASE_DIR = Path(__file__).resolve().parent
# Preserve shell overrides and load tracing settings before SDK imports.
load_dotenv(BASE_DIR / ".env")


@dataclass(frozen=True)
class Config:
    gigachat_credentials: str = field(repr=False)
    langfuse_public_key: str = field(repr=False)
    langfuse_secret_key: str = field(repr=False)
    database_url: str = field(repr=False)
    gigachat_model: str = "GigaChat-2"
    gigachat_scope: str = "GIGACHAT_API_PERS"
    gigachat_verify_ssl_certs: bool = True
    gigachat_ca_bundle_file: str | None = None
    langfuse_base_url: str = "https://cloud.langfuse.com"
    langfuse_environment: str = "development"
    chat_session_id: str = ""
    chat_user_id: str = ""
    chat_history_turns: int = 5


def load_config() -> Config:
    required = (
        "GIGACHAT_CREDENTIALS", "LANGFUSE_PUBLIC_KEY", "LANGFUSE_SECRET_KEY", "DATABASE_URL"
    )
    secrets = {key: os.getenv(key, "").strip() for key in required}
    missing = [key for key, value in secrets.items() if not value]
    if missing:
        raise ValueError("Заполни backend/ai/.env: " + ", ".join(missing))

    try:
        history_turns = int(os.getenv("CHAT_HISTORY_TURNS", str(Config.chat_history_turns)))
    except ValueError:
        raise ValueError("CHAT_HISTORY_TURNS должен быть целым числом от 0 до 100") from None
    if not 0 <= history_turns <= 100:
        raise ValueError("CHAT_HISTORY_TURNS должен быть целым числом от 0 до 100")

    ca_bundle = os.getenv("GIGACHAT_CA_BUNDLE_FILE", "").strip()
    if ca_bundle:
        ca_path = Path(ca_bundle).expanduser()
        if not ca_path.is_absolute():
            ca_path = BASE_DIR / ca_path
        if not ca_path.is_file():
            raise ValueError("GIGACHAT_CA_BUNDLE_FILE: файл сертификата не найден: " + str(ca_path))
        ca_bundle = str(ca_path.resolve())

    return Config(
        gigachat_credentials=secrets["GIGACHAT_CREDENTIALS"],
        langfuse_public_key=secrets["LANGFUSE_PUBLIC_KEY"],
        langfuse_secret_key=secrets["LANGFUSE_SECRET_KEY"],
        database_url=secrets["DATABASE_URL"],
        gigachat_model=os.getenv("GIGACHAT_MODEL", Config.gigachat_model),
        gigachat_scope=os.getenv("GIGACHAT_SCOPE", Config.gigachat_scope),
        gigachat_ca_bundle_file=ca_bundle or None,
        langfuse_base_url=os.getenv("LANGFUSE_BASE_URL", Config.langfuse_base_url),
        langfuse_environment=os.getenv("LANGFUSE_TRACING_ENVIRONMENT", Config.langfuse_environment),
        chat_session_id=os.getenv("CHAT_SESSION_ID", ""),
        chat_user_id=os.getenv("CHAT_USER_ID", ""),
        chat_history_turns=history_turns,
    )
