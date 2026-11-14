# API Gateway

Traefik v3.7, Docker provider. Единственная внешняя точка входа —
`http://127.0.0.1:8080`; порт меняется через GATEWAY_HTTP_PORT в корневом `.env`.
Маршрутизируются parser, interaction, searcher и AI (`agent`).

## Запуск

Из корня репозитория при работающем Docker Desktop:

```powershell
docker compose --profile gateway up -d --build --wait
```

Перед запуском заполни ключи GigaChat и Langfuse в `backend/ai/.env`
по [инструкции AI](../ai/README.md).

Профиль gateway включает парсер и interaction, запускает поиск, AI, PostgreSQL,
миграции и Traefik. Gateway ждёт готовности поиска и AI и запуска остальных зависимостей; Traefik дополнительно
проверяет readiness каждого backend. Сеть и том существующей БД сохраняются.
Для текущей локальной БД корневой `.env` задаёт COMPOSE_PROJECT_NAME=bashkiria-db.
На новой машине без этого переопределения применяется имя из compose.yaml.

## Маршруты

| Вход через Gateway | Backend | Изменение пути |
| --- | --- | --- |
| `/api/v1/parser` и `/api/v1/parser/...` | parser:8080 | Без изменения |
| `/api/v1/interaction` и `/api/v1/interaction/...` | interaction:8080 | Без изменения |
| `/search/...` | searcher:8080 | Удаляется `/search` |
| `/ai/...` | agent:8080 | Удаляется `/ai` |
| `/parser/health` | parser:8080/health | Удаляется `/parser` |
| `/parser/health/ready` | parser:8080/health/ready | Удаляется `/parser` |
| `/parser/openapi/v1.json` | parser:8080/openapi/v1.json | Удаляется `/parser` |
| `/interaction/health` | interaction:8080/health | Удаляется `/interaction` |
| `/interaction/health/ready` | interaction:8080/health/ready | Удаляется `/interaction` |
| `/interaction/openapi/v1.json` | interaction:8080/openapi/v1.json | Удаляется `/interaction` |
| `/ping` | Traefik | Проверка самого Gateway |

Поиск: `/search/events`, `/search/events/{id}`, `/search/events/{id}/similar`,
`/search/venues/schedule`, `/search/health`. Swagger: `/search/docs`,
OpenAPI: `/search/openapi.json`. Searcher получает ROOT_PATH=/search в Compose,
чтобы Swagger правильно загружал спецификацию и выполнял запросы через Gateway.
При локальном запуске ROOT_PATH пустой. Внутренние пути поискового API не меняются.

AI: `POST /ai/api/chat`, `GET /ai/api/tools`, `GET /ai/health`.
Swagger: `/ai/docs`, OpenAPI: `/ai/openapi.json`, ReDoc: `/ai/redoc`.
Compose задаёт Uvicorn переменную `UVICORN_ROOT_PATH=/ai`, чтобы документация
загружала спецификацию и выполняла запросы через Gateway. Внутренние пути AI
остаются `/api/chat`, `/api/tools` и `/health`; локальный запуск без Compose
использует пустой root path. Прямой порт агента сохраняется для вызовов API,
а документацию контейнера следует открывать через Gateway.

Query string и заголовки передаются backend без изменения. Правила проверяют
границу сегмента: например, `/api/v1/parser-other`, `/search-other` и `/ai-other` не совпадают.
Неизвестный маршрут получает 404 Traefik; недоступный backend — ошибку Gateway.
Ответы работающего сервиса, в том числе его формат ошибок, сохраняются.
Traefik не приводит ошибки разных backend к одному JSON-контракту.

У парсера и interaction спецификация сохраняет исходные абсолютные API-пути:
её можно загрузить с префиксного URL и выполнять запросы на тот же Gateway.
Корневой `/openapi/v1.json` не маршрутизируется, чтобы не было конфликта сервисов.
Страница `/interaction/docs` пока доступна на прямом порту interaction: её
абсолютные ссылки не предназначены для размещения под префиксом Gateway.

## Ключи и окружение

Traefik не хранит ключи в labels и не подставляет их автоматически.
Сохраняется проверка в каждом сервисе:

| API | Заголовки |
| --- | --- |
| Parser | X-Parser-Key = PARSER_API_KEY |
| Interaction /me | X-Interaction-Key = INTERACTION_API_KEY и проверенный X-User-Id |
| Interaction /admin | X-Interaction-Admin-Key = INTERACTION_ADMIN_API_KEY |
| Interaction /deliveries | X-Interaction-Delivery-Key = INTERACTION_DELIVERY_API_KEY |
| Searcher | Ключ не требуется |
| AI | Ключ не требуется; текущий прототип доверяет `user_id` в JSON |

Ключи берутся из корневого `.env` для Docker. Локальные запуски используют
соответствующие `backend/parser/.env`, `backend/interactionService/.env` и
`backend/searcher/.env`. В контейнерах БД имеет адрес postgres:5432; локальные
приложения используют localhost и фактический POSTGRES_PORT (сейчас 55432).
Searcher в Docker использует PG*-переменные, локально — свои DATABASE_URL или PG*.

Ключи interaction должны быть разными, минимум 32 символа. Секреты не выводятся
в документацию и не передаются браузеру/модели. X-User-Id выставляет доверенный
backend после проверки пользователя; знание адреса Gateway не даёт права
подставлять чужой ID. Текущий Gateway выполняет маршрутизацию, аутентификация
пользователей через JWT/OIDC здесь ещё не добавлена. Публикация порта ограничена
localhost; HTTPS/домен для внешнего развёртывания настраиваются отдельно.

Traefik видит только контейнеры своего Compose-проекта с traefik.enable=true.
Порт backend явно задан как 8080. Dashboard отключён. Docker socket смонтирован
как в исходной конфигурации — read-only; секретов в labels нет.
Прямые порты 5164, 5266, 8000 и 8001 (AGENT_HTTP_PORT) сохранены на localhost для отладки. Если запускаешь
те же приложения в Visual Studio, останови Docker-экземпляр либо выбери разные порты.

Миграция 0007_merge_service_heads объединяет независимые вершины 0006 без DDL.
Она нужна, чтобы `alembic upgrade head` был однозначным после слияния веток.

## Примеры

```powershell
$base = 'http://localhost:8080'
# Открытый поиск:
Invoke-RestMethod "$base/search/events?page=1&page_size=10" | ConvertTo-Json -Depth 8
# Парсер: ключ из PARSER_API_KEY корневого .env:
$parserHeaders = @{ 'X-Parser-Key' = 'КЛЮЧ_ПАРСЕРА' }
Invoke-RestMethod "$base/api/v1/parser/events?page=1&pageSize=10" -Headers $parserHeaders |
    ConvertTo-Json -Depth 8
# Interaction: запрос от доверенного backend:
$userHeaders = @{ 'X-Interaction-Key' = 'КЛЮЧ_ШЛЮЗА'; 'X-User-Id' = 'telegram:12345' }
Invoke-RestMethod "$base/api/v1/interaction/me/subscriptions" -Headers $userHeaders |
    ConvertTo-Json -Depth 8
# Контракты инструментов AI без вызова модели:
Invoke-RestMethod "$base/ai/api/tools" | ConvertTo-Json -Depth 8
```

Проверки:

```powershell
docker compose --profile gateway config --quiet
docker compose --profile gateway ps
./backend/gateway/scripts/Test-Gateway.ps1
```

Проверочный скрипт читает ключи из конфигурации Compose в память, не печатает их
и не изменяет карточки, подписки, обращения или историю чата. Проверяет все четыре сервиса,
Swagger/OpenAPI/ReDoc, контракт инструментов AI, валидацию запроса чата,
отсутствие автоматического обхода авторизации и границы префиксов. Модель не вызывается.

Документация Traefik: [Docker provider](https://doc.traefik.io/traefik/v3.7/reference/install-configuration/providers/docker/),
[StripPrefix](https://doc.traefik.io/traefik/v3.7/reference/routing-configuration/http/middlewares/stripprefix/).
