# bashkiria-events

Микросервисы афиши культурных мероприятий Башкортостана.

- [Парсер: запуск, API, EF Core, источники и тесты](backend/parser/README.md)
- [База данных и миграции Alembic](backend/db/README.md)
- [ИИ-сервис](backend/ai/README.md)
- [Поисковый сервис](backend/searcher/README.md)
- [HTTP-контракты сервисов: OpenAPI и генерация](contracts/README.md)

PostgreSQL и миграции запускаются из корня:

```powershell
docker compose up -d postgres
docker compose run --rm --build migrate
```

Парсер можно запускать локально через Visual Studio / .NET CLI или отдельным
профилем Compose. Настройки и требования к ключу API описаны в его README.

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

AI-сервис пока запускается через CLI. В `backend/ai/.env` укажи ключи модели,
Langfuse и локальную строку подключения (порт должен совпадать с `POSTGRES_PORT`):

```dotenv
DATABASE_URL=postgresql://bashkiria:bashkiria@localhost:5432/bashkiria
```

```powershell
cd backend/ai
uv sync --frozen
uv run --frozen python chat.py --user-id user-001 --session-key chat-001
```

История переписки заполняется автоматически. Настройки окна и трассировки описаны
в [AI README](backend/ai/README.md).

## Миграции

Схема и ревизии принадлежат [backend/db](backend/db/README.md). Сервис миграций
имеет собственный образ и окружение Python; ключи AI ему не нужны.

```powershell
docker compose run --rm migrate
docker compose run --rm migrate alembic current
```

При добавлении HTTP backend в этот же Compose укажи его зависимость:

```yaml
depends_on:
  postgres:
    condition: service_healthy
  migrate:
    condition: service_completed_successfully
```

Так backend начнёт работу после успешной миграции. Frontend и HTTP backend будут
добавлены в этот Compose по мере появления их исполняемых сервисов.
