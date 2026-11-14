# bashkiria-events

Микросервисы афиши культурных мероприятий Башкортостана.

- [Парсер: запуск, API, EF Core, источники и тесты](backend/parser/README.md)
- [База данных и миграции Alembic](backend/db/README.md)
- [ИИ-сервис](backend/ai/README.md)
- [Поисковый сервис](backend/searcher/README.md)

PostgreSQL и миграции запускаются из корня:

```powershell
docker compose up -d postgres
docker compose run --rm --build migrate
```

Парсер можно запускать локально через Visual Studio / .NET CLI или отдельным
профилем Compose. Настройки и требования к ключу API описаны в его README.

Все сервисы проекта описываются в корневом [compose.yaml](compose.yaml).
Доступны PostgreSQL, одноразовый сервис миграций Alembic, поисковый HTTP-сервис,
ИИ-агент и парсер в отдельном профиле.

## Запуск

Перед первым запуском создай файл настроек агента:

```powershell
if (-not (Test-Path backend/ai/.env)) { Copy-Item backend/ai/.env.example backend/ai/.env }
```

Заполни в нём `GIGACHAT_CREDENTIALS`, `LANGFUSE_PUBLIC_KEY` и `LANGFUSE_SECRET_KEY`.
Затем из корня проекта при запущенном Docker Desktop:

```powershell
docker compose up -d --build
docker compose wait migrate
docker compose logs migrate
```

PostgreSQL проходит healthcheck, затем `migrate` выполняет `alembic upgrade head`
и завершается с кодом 0, после чего запускаются поиск и агент. Повторный запуск применяет только новые ревизии.
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

При локальном запуске агента в `backend/ai/.env` также укажи
`DATABASE_URL=postgresql://bashkiria:bashkiria@localhost:55432/bashkiria`
и повтори `docker compose up -d`. Внутри Docker остаётся адрес `postgres:5432`;
пересоздание контейнера с другим внешним портом сохраняет существующий том БД.

Поисковый API доступен на `http://localhost:8000`, Swagger — на
`http://localhost:8000/docs`. Поддерживаются полные события с пагинацией,
фильтры, расписание площадки и похожие события по UUID. Порт задаётся
через `SEARCHER_HTTP_PORT`. Контракты и примеры — в [Search README](backend/searcher/README.md).

ИИ-агент запускается сервисом `agent`. Его API доступен на `http://localhost:8001`,
порт задаётся через `AGENT_HTTP_PORT` в корневом `.env`. Если `8001` уже занят
локальным Python-сервисом, задай `AGENT_HTTP_PORT=8002` и используй
`http://localhost:8002` в клиенте. Затем повтори запуск Compose.
Для запуска только агента с его зависимостями:

```powershell
docker compose up -d --build --wait agent
```

Настройки модели и Langfuse загружаются из `backend/ai/.env`. В контейнере
`DATABASE_URL` переопределяется адресом `postgres:5432` и параметрами `POSTGRES_*`
из корневого `.env`; внешний порт PostgreSQL на это подключение не влияет.
Локальный запуск HTTP API и CLI описан в [AI README](backend/ai/README.md).

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
Шесть инструментов подключены к графу агента; контракты доступны через `GET /api/tools`.
Поиск использует `searcher`; подписки и обращения — `interactionService`,
справочник городов — `parser`. Для всех инструментов запусти:

```powershell
docker compose --profile parser --profile interaction up -d --build --wait agent
```

Ключи парсера и Interaction API задаются в корневом `.env`. Подписка требует
явного подтверждения в интерфейсе (`subscription_consent: true` в запросе чата).
Форматы ответа, сетевые повторы, CORS и CLI описаны
в [AI README](backend/ai/README.md), инструменты — в [TOOLS.md](backend/ai/TOOLS.md).

## Миграции

Схема и ревизии принадлежат [backend/db](backend/db/README.md). Сервис миграций
имеет собственный образ и окружение Python; ключи AI ему не нужны.

```powershell
docker compose run --rm migrate
docker compose run --rm migrate alembic current
```

Для новых сервисов в этом же Compose укажи зависимость, как у поиска и агента:

```yaml
depends_on:
  postgres:
    condition: service_healthy
  migrate:
    condition: service_completed_successfully
```

Так backend начнёт работу после успешной миграции.
