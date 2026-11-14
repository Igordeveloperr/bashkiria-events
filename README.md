# bashkiria-events

Все сервисы проекта описываются в корневом [compose.yaml](compose.yaml).
Сейчас доступны PostgreSQL и одноразовый сервис миграций Alembic.

## Запуск

Из корня проекта при запущенном Docker Desktop:

```powershell
docker compose up -d --build
docker compose wait migrate
docker compose logs migrate
```

PostgreSQL проходит healthcheck, затем `migrate` выполняет `alembic upgrade head`
и завершается с кодом 0. Повторный запуск применяет только новые ревизии.
При ошибке `docker compose wait migrate` возвращает ненулевой код.

По умолчанию база, пользователь и пароль — `bashkiria`, локальный порт — `5432`.
Параметры можно задать в корневом `.env` по [.env.example](.env.example).
Данные хранятся в томе `bashkiria-db_postgres_data`; имя проекта сохранено для
совместимости с прежним Compose из `backend/db`.

Если Windows выдаёт `ports are not available` / `bind ... accesspermissions`
для `5432`, задай свободный внешний порт в корневом `.env`, например:

```dotenv
POSTGRES_PORT=55432
```

Тогда в `backend/ai/.env` укажи
`DATABASE_URL=postgresql://bashkiria:bashkiria@localhost:55432/bashkiria`
и повтори `docker compose up -d`. Внутри Docker остаётся адрес `postgres:5432`;
пересоздание контейнера с другим внешним портом сохраняет существующий том БД.

AI-сервис доступен через HTTP и CLI. В `backend/ai/.env` укажи ключи модели,
Langfuse и локальную строку подключения (порт должен совпадать с `POSTGRES_PORT`):

```dotenv
DATABASE_URL=postgresql://bashkiria:bashkiria@localhost:5432/bashkiria
```

```powershell
cd backend/ai
uv sync --frozen
uv run --frozen uvicorn api:app --host 127.0.0.1 --port 8001
```

Фронтенд отправляет `POST http://localhost:8001/api/chat` с JSON:

```json
{
  "user_id": "user-001",
  "session_id": "chat-001",
  "message": "Помоги подобрать концерт в Уфе"
}
```

Ответ находится в поле `reply`. История переписки сохраняется автоматически по паре
`user_id` и `session_id`. Swagger: `http://localhost:8001/docs`.
Контракты шести инструментов доступны через `GET /api/tools`; их выполнение пока
не реализовано. Форматы ответа, сетевые повторы, CORS и CLI описаны
в [AI README](backend/ai/README.md), инструменты — в [TOOLS.md](backend/ai/TOOLS.md).

## Миграции

Схема и ревизии принадлежат [backend/db](backend/db/README.md). Сервис миграций
имеет собственный образ и окружение Python; ключи AI ему не нужны.

```powershell
docker compose run --rm migrate
docker compose run --rm migrate alembic current
```

При контейнеризации HTTP API в этом же Compose укажи его зависимость:

```yaml
depends_on:
  postgres:
    condition: service_healthy
  migrate:
    condition: service_completed_successfully
```

Так backend начнёт работу после успешной миграции. Пока Compose запускает БД и
миграции; HTTP API запускается локально командой выше.
