# bashkiria-events

Микросервисы афиши культурных мероприятий Башкортостана.

- [Парсер: запуск, API, EF Core, источники и тесты](backend/parser/README.md)
- [База данных и миграции Alembic](backend/db/README.md)
- [ИИ-сервис](backend/ai/README.md)
- [Поисковый сервис](backend/searcher/README.md)
- [Подписки, уведомления и обратная связь](backend/interactionService/README.md)
- [API Gateway: маршруты, ключи и проверки](backend/gateway/README.md)

## Единая точка входа

Traefik v3.7 маршрутизирует запросы к parser, interaction и searcher.
В корневом `.env` должны быть заданы PARSER_API_KEY, INTERACTION_API_KEY,
INTERACTION_ADMIN_API_KEY, INTERACTION_DELIVERY_API_KEY (три ключа interaction
разные, не короче 32 символов). Существующий `.env` не перезаписывай примером.

```powershell
docker compose --profile gateway up -d --build --wait
```

| Адрес | Назначение |
| --- | --- |
| `http://localhost:8080/api/v1/parser/events` | Карточки парсера, нужен X-Parser-Key |
| `http://localhost:8080/api/v1/interaction/me/subscriptions` | Свои подписки, нужны X-Interaction-Key и проверенный X-User-Id |
| `http://localhost:8080/search/events` | Поиск событий |
| `http://localhost:8080/search/docs` | Swagger поиска |
| `http://localhost:8080/parser/openapi/v1.json` | OpenAPI парсера |
| `http://localhost:8080/interaction/openapi/v1.json` | OpenAPI interaction |

GATEWAY_HTTP_PORT меняет порт 8080. Прямые порты сервисов сохранены для отладки.
В текущем `.env` COMPOSE_PROJECT_NAME=bashkiria-db сохраняет существующие сеть
и том; без переопределения применяется имя bashkiria-events из compose.yaml.
Traefik не подставляет ключи и не аутентифицирует пользователей самостоятельно.
Подробности: [Gateway README](backend/gateway/README.md).

PostgreSQL и миграции запускаются из корня:

```powershell
docker compose up -d postgres
docker compose run --rm --build migrate
```

Парсер можно запускать локально через Visual Studio / .NET CLI или отдельным
профилем Compose. Настройки и требования к ключу API описаны в его README.

Все сервисы проекта описываются в корневом [compose.yaml](compose.yaml).
Доступны PostgreSQL, одноразовый сервис миграций Alembic, поисковый HTTP-сервис
и парсер в отдельном профиле.

## Запуск

Из корня проекта при запущенном Docker Desktop:

```powershell
docker compose up -d --build
docker compose wait migrate
docker compose logs migrate
```

PostgreSQL проходит healthcheck, затем `migrate` выполняет `alembic upgrade head`
и завершается с кодом 0, после чего запускается поиск. Повторный запуск применяет только новые ревизии.
При ошибке `docker compose wait migrate` возвращает ненулевой код.

По умолчанию база, пользователь и пароль — `bashkiria`, локальный порт — `5432`.
Параметры можно задать в корневом `.env` по [.env.example](.env.example).
Данные текущего локального стека хранятся в `bashkiria-db_postgres_data`.
Для сохранения этого тома используется COMPOSE_PROJECT_NAME=bashkiria-db
в корневом `.env`; на новой установке имя тома зависит от имени Compose-проекта.

Если Windows выдаёт `ports are not available` / `bind ... accesspermissions`
для `5432`, задай свободный внешний порт в корневом `.env`, например:

```dotenv
POSTGRES_PORT=55432
```

Тогда в `backend/ai/.env` укажи
`DATABASE_URL=postgresql://bashkiria:bashkiria@localhost:55432/bashkiria`
и повтори `docker compose up -d`. Внутри Docker остаётся адрес `postgres:5432`;
пересоздание контейнера с другим внешним портом сохраняет существующий том БД.

Поисковый API доступен на `http://localhost:8000`, Swagger — на
`http://localhost:8000/docs`. Поддерживаются полные события с пагинацией,
фильтры, расписание площадки и похожие события по UUID. Порт задаётся
через `SEARCHER_HTTP_PORT`. Контракты и примеры — в [Search README](backend/searcher/README.md).

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

Для новых сервисов в этом же Compose укажи зависимость, как у поиска:

```yaml
depends_on:
  postgres:
    condition: service_healthy
  migrate:
    condition: service_completed_successfully
```

Так backend начнёт работу после успешной миграции. Frontend и AI HTTP backend
будут добавлены в этот Compose по мере появления их исполняемых сервисов.
