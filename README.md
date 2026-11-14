# bashkiria-events

Микросервисы афиши культурных мероприятий Башкортостана.

- [Парсер: запуск, API, EF Core, источники и тесты](backend/parser/README.md)
- [База данных и миграции Alembic](backend/db/README.md)
- [ИИ-сервис](backend/ai/README.md)
- [Поисковый сервис](backend/searcher/README.md)
- [Подписки, уведомления и обратная связь](backend/interactionService/README.md)
- [API Gateway: маршруты, ключи и проверки](backend/gateway/README.md)

## Единая точка входа

Traefik v3.7 маршрутизирует запросы к parser, interaction, searcher и AI (`agent`).
В корневом `.env` должны быть заданы PARSER_API_KEY, INTERACTION_API_KEY,
INTERACTION_ADMIN_API_KEY, INTERACTION_DELIVERY_API_KEY (три ключа interaction
разные, не короче 32 символов). Существующий `.env` не перезаписывай примером.
Для AI заполни ключи GigaChat и Langfuse в `backend/ai/.env` по инструкции ниже.

```powershell
docker compose --profile gateway up -d --build --wait
```

| Адрес | Назначение |
| --- | --- |
| `http://localhost:8080/api/v1/parser/events` | Карточки парсера, нужен X-Parser-Key |
| `http://localhost:8080/api/v1/interaction/me/subscriptions` | Свои подписки, нужны X-Interaction-Key и проверенный X-User-Id |
| `http://localhost:8080/search/events` | Поиск событий |
| `http://localhost:8080/search/docs` | Swagger поиска |
| `http://localhost:8080/ai/api/chat` | Чат с ИИ, POST с JSON |
| `http://localhost:8080/ai/api/tools` | Контракты инструментов ИИ |
| `http://localhost:8080/ai/docs` | Swagger ИИ |
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
Данные текущего локального стека хранятся в `bashkiria-db_postgres_data`.
Для сохранения этого тома используется COMPOSE_PROJECT_NAME=bashkiria-db
в корневом `.env`; на новой установке имя тома зависит от имени Compose-проекта.

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

Через Gateway фронтенд отправляет `POST http://localhost:8080/ai/api/chat` с JSON:

```json
{
  "user_id": "user-001",
  "session_id": "chat-001",
  "message": "Помоги подобрать концерт в Уфе"
}
```

Ответ находится в поле `reply`. История переписки сохраняется автоматически по паре
`user_id` и `session_id`. Swagger через Gateway: `http://localhost:8080/ai/docs`.
Шесть инструментов подключены к графу агента; контракты доступны через `GET /ai/api/tools`.
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

Так backend начнёт работу после успешной миграции. AI HTTP backend уже включён
в Compose сервисом `agent`.

## Полная таблица HTTP API

Базовый адрес Gateway — **http://localhost:8080** (GATEWAY_HTTP_PORT).
Ниже указаны полные пути относительно этого адреса. Если не сказано иначе,
тело — JSON с Content-Type application/json. Параметры в фигурных скобках —
параметры пути, `{id}` и `{event_id}` — UUID. Query передаётся после `?`.
«Нет» в столбце параметров означает, что тело и query не нужны.

### Обозначения доступа и общие контракты

| Код доступа | Необходимые заголовки | Откуда взять значение |
| --- | --- | --- |
| — | Не требуются | Открытые поиск, AI, health и документация |
| P | `X-Parser-Key` | PARSER_API_KEY корневого `.env` |
| U | `X-Interaction-Key`, `X-User-Id` | INTERACTION_API_KEY и ID пользователя, проверенный доверенным backend |
| A | `X-Interaction-Admin-Key` | INTERACTION_ADMIN_API_KEY |
| D | `X-Interaction-Delivery-Key` | INTERACTION_DELIVERY_API_KEY |

Ключи не передаются браузеру или модели. Gateway сохраняет заголовки и проверку
прав backend; сам пользователя не аутентифицирует. Код U не даёт доступа к A/D.
Свои подписки, обращения и уведомления определяются по проверенному X-User-Id,
а не по произвольному userId в JSON. ID — 1..128 символов: буквы ASCII, цифры,
точка, двоеточие, дефис, подчёркивание.

| Обозначение | Формат / ограничения |
| --- | --- |
| `Page<T>` | Parser и interaction: `{total, page, pageSize, items: T[]}`; page по умолчанию 1, 1..100000; pageSize по умолчанию 30, 1..100 |
| `SearchPage<T>` | Searcher: `{items: T[], total, page, page_size, pages, has_next, has_previous}`; page по умолчанию 1, 1..1000000; page_size по умолчанию 50, 1..500 |
| Дата / время | Дата yyyy-MM-dd; время HH:mm:ss с возможной дробной частью; datetime ISO 8601 с UTC offset. Локальное время событий — Asia/Yekaterinburg |
| Статусы публикации | Parser/interaction JSON: draft, published, cancelled; searcher: Draft, Published, Cancelled |
| Категории | Parser/interaction: other, concert, theatre, exhibition, festival, cinema, excursion, workshop, lecture. В searcher — те же имена с заглавной первой буквой |
| Статусы источника | Parser: unknown, scheduled, cancelled, postponed, rescheduled, soldOut; searcher: Unknown, Scheduled, Cancelled, Postponed, Rescheduled, SoldOut |
| Пустая выдача | 200 и items=[], total=0; страница за концом списка также возвращает items=[] |
| Лимит parser | 60 запросов/минуту на экземпляр, общий для основного API и preview |
| Лимит interaction | 60 запросов/минуту на пользователя; admin/delivery — отдельные общие лимиты. Обращения дополнительно ограничены в БД: по умолчанию 5 новых/час/пользователь |

`null` означает отсутствие данных; неизвестная цена не считается бесплатной.
Все списковые ограничения указаны для текущих настроек по умолчанию.

### Parser: сбор, карточки, импорт и модерация

| Метод | Endpoint через Gateway | Доступ | Query / тело / дополнительные заголовки | Успешный ответ и назначение |
| --- | --- | --- | --- | --- |
| GET | `/api/v1/parser/sources` | P | Нет | 200, массив `{id, kind, enabled, listingUrl, priority}`, включая отключённые источники |
| POST | `/api/v1/parser/runs/{sourceId}` | P | Query maxPages: 1..Parser:MaxPagesPerRun, по умолчанию лимит конфигурации (200); тела нет | 202 + Location задания; CollectionRunResponse. Сбор выполняется в фоне |
| GET | `/api/v1/parser/runs` | P | Нет | 200, последние 50 заданий, createdAt DESC |
| GET | `/api/v1/parser/runs/{id}` | P | Нет | 200, состояние задания и итоговый report; 404, если нет задания |
| DELETE | `/api/v1/parser/runs/{id}` | P | Нет | 204, отмена активного задания; 409, если завершено или не существует |
| GET | `/api/v1/parser/events` | P | Query page, pageSize, status, city (точное имя), cityId, venueId; фильтры AND | 200, Page&lt;EventResponse&gt;, startDate ASC, id ASC; все статусы по умолчанию |
| GET | `/api/v1/parser/events/published` | P | Query page, pageSize | 200, Page&lt;EventResponse&gt;: только опубликованные, свежие, актуальные события с подтверждёнными cityId/venueId |
| GET | `/api/v1/parser/events/{id}` | P | Нет | 200, полная карточка с источниками и version, в том числе отменённая; 404, если нет записи |
| POST | `/api/v1/parser/events` | P | JSON EventInput; обязателен Idempotency-Key: UUID; externalId не допускается; до 128 КиБ | 201 + Location при создании или 200 при повторе; CreateEventResponse. Новая карточка — draft |
| PATCH | `/api/v1/parser/events/{id}/review` | P | JSON EventReview: expectedVersion, status, note и исправляемые поля | 204, модерация с аудитом и увеличением version; 409 при устаревшей версии/дубле |
| GET | `/api/v1/parser/events/{id}/audit` | P | Нет | 200, последние 100 записей `{id, action, actor, createdAt, snapshot}`; createdAt DESC |
| POST | `/api/v1/parser/imports/json/{datasetId}` | P | JSON EventInput[]; до 1000 записей, 5 МиБ по умолчанию | 200, ImportResponse с результатом каждой строки; частичные отказы находятся в отчёте |
| POST | `/api/v1/parser/imports/csv/{datasetId}` | P | UTF-8 CSV, Content-Type text/csv; до 1000 записей, 5 МиБ | 200, ImportResponse; заголовки полей EventInput, разделитель запятая/точка с запятой, теги разделяются символом &#124; |
| GET | `/api/v1/parser/raw-documents/{id}` | P | Нет | 200, исходник HTML/JSON/CSV с происхождением; 404, если нет документа |
| POST | `/api/v1/parser/raw-documents/{id}/reparse` | P | Нет | 200, повторный разбор без внешней загрузки: для страницы `{rawDocumentId, results}`, для ручного ввода/импорта ImportResponse |
| GET | `/api/v1/parser/stats` | P | Нет | 200, число исходников, группировки событий и заданий по статусам |

datasetId — 1..64 символа, строчные латинские буквы, цифры, `_`, `-`;
начинается с буквы. Стабильный externalId рекомендуется для каждой импортируемой
записи/сеанса. Повтор Idempotency-Key с другим ручным событием даёт 409.
Источники включаются конфигурацией; фактические значения возвращает /sources.

### Parser: справочники

| Метод | Endpoint через Gateway | Доступ | Query / JSON-тело | Успешный ответ и назначение |
| --- | --- | --- | --- | --- |
| GET | `/api/v1/parser/reference/cities` | P | page, pageSize, q — поиск по имени/алиасу | 200, Page&lt;CityResponse&gt;, name ASC, id ASC |
| GET | `/api/v1/parser/reference/cities/{id}` | P | Нет | 200, `{id, name, aliases}`; 404 для неизвестного ID |
| POST | `/api/v1/parser/reference/cities` | P | `{name}`: непустое имя до 200 символов | 201 + Location, `{id}`; имя/алиас уникально после нормализации |
| POST | `/api/v1/parser/reference/cities/{id}/aliases` | P | `{name}`: до 200 символов | 201, `{id}` алиаса; 404 для неизвестного города, 409 для конфликта |
| GET | `/api/v1/parser/reference/venues` | P | page, pageSize, cityId, q — поиск по имени/алиасу | 200, Page&lt;VenueResponse&gt;, name ASC, id ASC |
| GET | `/api/v1/parser/reference/venues/{id}` | P | Нет | 200, `{id, cityId, name, address, aliases}`; 404 для неизвестной площадки |
| POST | `/api/v1/parser/reference/venues` | P | `{cityId, name, address}`: существующий город, имя до 500, адрес до 2000 символов | 201 + Location, `{id}`; филиалы с разными адресами могут иметь одно имя |
| POST | `/api/v1/parser/reference/venues/{id}/aliases` | P | `{name, address?}`: имя до 500, опциональный вариант адреса до 2000 символов | 201, `{id}` алиаса; сочетание города/имени/адреса уникально |
| GET | `/api/v1/parser/reference/categories` | P | Нет | 200, массив `{id, name}`: код категории и русское название |
| POST | `/api/v1/parser/reference/normalize` | P | Query page, pageSize; тела нет | 200, Page&lt;{eventId, outcome, issues: string[]}&gt;; нормализует существующие карточки с сохранением исходников и ручных правок |

Алиас: `{id, name, address}`; у города address=null. Варианты площадок привязаны
к городу и проверенному адресу. Неизвестные/неоднозначные площадки остаются draft.
Исторический конфликт ключа дубля помечается reference_duplicate без удаления карточек.

### Тела запросов parser

| Контракт | Обязательные поля | Остальные поля и ограничения |
| --- | --- | --- |
| EventInput | title: непустая строка до 500 символов | externalId до 200 (только импорт); sourceUrl: HTTP(S) URL до 2048; category=other; tags: до 50 строк по 100 символов; startDate, startTime, endDate, endTime |
| EventInput: место | — | city до 200, venue до 500, address до 2000; cityId/venueId: UUID. Явный venueId заполняет канонические город, площадку и адрес; несовместимые ID отклоняются |
| EventInput: содержание | — | description до 50000, language до 100; orgLink/imageUrl: HTTP(S) до 2048, без учётных данных; latitude ±90, longitude ±180, до 7 дробных знаков |
| EventInput: стоимость/возраст/статус | — | priceMin/priceMax: 0..9999999999.99, до 2 дробных знаков, max не меньше min; isFree=true задаёт 0/0 и не совместим с положительной ценой; ageLimit: 0,6,12,16,18 или null; sourceStatus=unknown |
| EventReview | expectedVersion: int64; status: draft/published/cancelled; note: непустая строка до 2000 | title, city, venue, address, description, cityId, venueId, startDate, startTime, priceMin, priceMax, ageLimit, confirmedSourceStatus. Неуказанное и null сохраняют прежнее значение |

Отсутствующие сведения не выдумываются. Неизвестные JSON-поля отклоняются.
Окончание не может предшествовать началу; endTime без endDate относится к startDate.
Для публикации нужны заполненные обязательные сведения, однозначная площадка
и подтверждённый действующий статус. Подтверждённую источником отмену нельзя
снять простой модерацией. CSV принимает те же поля, названия также могут быть snake_case.

| Ответ parser | Поля |
| --- | --- |
| EventResponse | id, title, normalizedTitle, category, tags, startDate/startTime, endDate/endTime, dateStart/dateEnd (UTC), durationMin, timeZone, city/cityId, venue/venueId, address, latitude/longitude, description, priceMin/priceMax, isFree, ageLimit, language, orgLink, imageUrl |
| EventResponse: служебные поля | status, sourceStatus, confidence, issues, requiresReview, verifiedAt, createdAt, updatedAt, reviewedAt, reviewNote, version, sourceHash, sourceType, dedupKey, sources |
| Источник карточки | sourceId, sourceUrl, rawDocumentId, checkedAt, sourceType (page/import/manual) |
| CreateEventResponse | eventId, outcome (added/updated/unchanged), status, rawDocumentId, issues, requiresReview |
| ImportResponse | rawDocumentId, datasetId, total, added, updated, unchanged, rejected, requiresReview, items |
| Строка отчёта импорта | row, externalId, eventId, outcome, status, issues, errors; при отказе outcome=rejected, eventId/status=null |
| CollectionRunResponse | id, sourceId, status, maxPages, attempts, createdAt, startedAt, finishedAt, error, report |
| Статусы задания | queued, running, completed, completedWithErrors, failed, cancelled |
| Report сбора | sourceId, listingDocumentId, discoveredPages, processedPages, downloadedPages, parsedPages, items, errors, addedEvents, updatedEvents, unchangedEvents, undatedCandidates, reviewRequiredCandidates |
| RawDocument | id, sourceId, url, fetchedAt, httpStatus, content, contentHash, sourceType, contentType; для импорта/ручного ввода httpStatus=null и внутренний URN |
| ParsingIssue / InputError | issue: `{code, message}`; ошибка поля: `{field, code, message}` |

### Searcher: поиск и полные карточки

Префикс /search удаляется Gateway. Внешние имена JSON и query — snake_case;
числовые monetary/numeric значения в ответах сериализуются строками для сохранения
точности. GET не принимает JSON-тело. Неизвестные query-параметры дают 422.

| Метод | Endpoint через Gateway | Доступ | Query | Успешный ответ и назначение |
| --- | --- | --- | --- | --- |
| GET | `/search/events` | — | Все фильтры из таблицы ниже, page/page_size, sort | 200, SearchPage&lt;Event&gt;; по умолчанию все статусы, включая draft/cancelled, и все даты |
| GET | `/search/events/{event_id}` | — | Нет | 200, полное событие и sources без фильтра публикации; 404, если отсутствует |
| GET | `/search/events/{event_id}/similar` | — | page/page_size, min_score=0.15 (0..1) | 200, страница похожих событий со score и reference_event_id; 404, если исходного события нет |
| GET | `/search/venues/schedule` | — | Обязательны venue, date_from, date_to; опциональны city, address, page/page_size | 200, страница опубликованных событий площадки по времени; ответ дополнен venue, date_from/date_to, time_zone |

| Query поиска /events | Тип / правило |
| --- | --- |
| q, title | Непустой текст до 300 символов; q — текст/часть названия/опечатка, title — фильтр названия |
| city, venue, address, language | Непустые строки до 300 символов; city — точное имя без учёта регистра, venue/address/language — текстовые фильтры |
| categories | Повторяющийся параметр, до 20 значений: например categories=Concert&categories=Theatre |
| tags, tags_mode | До 50 повторяющихся tags по 300 символов; tags_mode=all (по умолчанию) или any |
| statuses, source_statuses | Повторяющиеся параметры: до 3 статусов публикации и до 6 статусов источника; значения PascalCase, перечислены выше |
| date_from, date_to | datetime с UTC offset, пересечение с периодом [from,to); from строго раньше to, можно задать одну границу |
| start_date_from, start_date_to | Календарные даты начала, обе границы включены; from не позже to |
| price_from, price_to | Неотрицательные рубли, до 2 дробных знаков, до 12 значащих цифр; пересечение диапазонов цен |
| is_free, age_limit_max | Boolean и максимальное возрастное ограничение 0..18 |
| duration_from, duration_to | Минуты, 0..525600; обе границы включены, from не больше to |
| source_id | Точный ID хотя бы одного источника, до 300 символов, без учёта регистра |
| confidence_min | Число 0..1 |
| updated_since, verified_since | datetime с UTC offset: обновлено/проверено не раньше заданного момента |
| latitude, longitude, radius_km | Указываются вместе: ±90, ±180, радиус больше 0 и не больше 20000 км |
| sort | date_asc, date_desc, price_asc, price_desc, updated_desc, relevance; relevance требует q. По умолчанию relevance при q, иначе date_asc |
| page, page_size | Пагинация SearchPage, ограничения указаны выше |
| short | По умолчанию false. При true — компактная модель для ИИ, без description, sources и служебных данных. Поддерживается также поиском похожих событий |

Фильтры поиска объединяются AND; значения categories/statuses внутри списка — OR.
Для опубликованных передай statuses=Published. Без периода могут вернуться прошедшие
мероприятия. Неизвестное значение не проходит числовой фильтр. Расписание принимает
точное venue без учёта регистра/ё; city и часть address различают филиалы;
date_from/date_to обязательны, from строго раньше to.

| Ответ searcher | Состав |
| --- | --- |
| Event | Все столбцы parser_events: соответствуют полям карточки parser в snake_case, включая normalized_title, city_id, venue_id, source_type, issues_json, data_hash, source_priority, manually_suppressed; дополнительно time_zone и sources |
| Source | Все столбцы parser_event_sources: id, identity_key, source_id, source_type, external_id, source_url, data_hash, event_id, raw_document_id, candidate_json, checked_at |
| SimilarPage | Пагинация SearchPage и reference_event_id; у каждого элемента items дополнительно score |
| ShortEvent | Только id, title, category, start_date, start_time, venue, age_limit. Остальные данные доступны в полном Event |
| ShortSimilarPage | Пагинация SearchPage и reference_event_id; items содержат ShortEvent плюс score |

ИИ автоматически использует `short=true` в поиске и поиске похожих событий;
фронтенд продолжает получать полные ответы. Флаг не ограничен авторизацией.

### Interaction: подписки, уведомления и обратная связь

| Метод | Endpoint через Gateway | Доступ | Query / тело / дополнительные заголовки | Успешный ответ и назначение |
| --- | --- | --- | --- | --- |
| GET | `/api/v1/interaction/me/subscriptions` | U | page, pageSize | 200, Page&lt;Subscription&gt;, createdAt ASC, id ASC |
| GET | `/api/v1/interaction/me/subscriptions/{id}` | U | Нет | 200, своя Subscription; чужая/отсутствующая — 404 |
| POST | `/api/v1/interaction/me/subscriptions` | U | JSON SubscriptionInput; consentConfirmed=true | 201 + Location для новой, 200 для совпадающей подписки; ответ Subscription |
| PUT | `/api/v1/interaction/me/subscriptions/{id}` | U | JSON SubscriptionInput; consentConfirmed=true и expectedVersion | 200, Subscription; 409 при старой версии или совпадении с другой подпиской |
| DELETE | `/api/v1/interaction/me/subscriptions/{id}` | U | Нет | 204, удалить свою подписку; повтор/чужой ID также 204 без удаления чужих данных |
| DELETE | `/api/v1/interaction/me/subscriptions` | U | Нет | 204, отписаться от всего: удалить свои recipient, подписки, уведомления и историю дедупликации |
| GET | `/api/v1/interaction/me/notifications` | U | page, pageSize, unreadOnly=false | 200, Page&lt;Notification&gt;, createdAt DESC, id ASC; карточки проверяются по текущему каталогу |
| PATCH | `/api/v1/interaction/me/notifications/{id}/read` | U | Нет | 204, установить readAt; повтор не меняет время; чужое/отсутствующее уведомление — 404 |
| POST | `/api/v1/interaction/me/feedback` | U | JSON FeedbackInput; обязателен Idempotency-Key: UUID | 201 + Location при приёме, 200 при идентичном повторе; Feedback. Другой текст с тем же ключом — 409 |
| GET | `/api/v1/interaction/me/feedback` | U | page, pageSize | 200, Page&lt;Feedback&gt;, свои обращения, createdAt DESC, id ASC |
| GET | `/api/v1/interaction/me/feedback/{id}` | U | Нет | 200, своё обращение; чужое/отсутствующее — 404 |
| GET | `/api/v1/interaction/admin/feedback` | A | page, pageSize, status, kind | 200, Page&lt;Feedback&gt;, очередь модерации, createdAt ASC, id ASC |
| PATCH | `/api/v1/interaction/admin/feedback/{id}/review` | A | JSON FeedbackReview; опциональный X-Actor-Id | 204, изменение статуса с проверкой версии и записью в историю |
| GET | `/api/v1/interaction/admin/feedback/{id}/history` | A | Нет | 200, последние 100 `{id, previousStatus, status, note, actor, createdAt}`, createdAt DESC; 404 для неизвестного обращения |
| POST | `/api/v1/interaction/admin/notifications/generate` | A | Нет | 200, `{created}`; один цикл формирования подборок с соблюдением тихих часов и дедупликации |
| POST | `/api/v1/interaction/admin/retention/cleanup` | A | Нет | 200, `{deleted}`; очистка по срокам хранения, без учёта каскадных строк в счётчике |
| GET | `/api/v1/interaction/admin/metrics` | A | Нет | 200, `{requests, errors, meanDurationMs}` с запуска процесса |
| POST | `/api/v1/interaction/deliveries/claim` | D | Query limit=10, 1..20; тела нет | 200, Delivery[]; аренда Telegram-подборок до 2 минут, не позднее 21:00; ночью/при пустой очереди [] |
| POST | `/api/v1/interaction/deliveries/{id}/ack` | D | JSON DeliveryAck | 204, подтверждение доставки/ошибки; неверная или истёкшая аренда — 409; удалённое уведомление — 404 |

| JSON-контракт interaction | Поля и правила |
| --- | --- |
| SubscriptionInput | cityId: обязательный существующий UUID; category: категория или null для всех; dateFrom/dateTo: необязательные даты, окончание не раньше начала/сегодня, верхняя граница не далее года; channel=web или telegram; consentConfirmed обязательно true; expectedVersion обязателен для PUT |
| FeedbackInput | kind: question, correction или organizerApplication; subject: непустая строка до 200; message: до 5000; eventId: существующий UUID или null, обязателен для correction; website: honeypot, отсутствует/пустой |
| FeedbackReview | expectedVersion: int64; status: inProgress, resolved или rejected; note: непустой текст до 2000. X-Actor-Id: непрозрачный ID модератора формата X-User-Id, по умолчанию admin-api |
| DeliveryAck | leaseToken: непустой UUID; success: обязательный boolean; permanentFailure=false; errorCode: опционально, 1..64 символа [a-z0-9_], без текста исключения и персональных данных |
| Subscription | id, cityId, category, dateFrom, dateTo, channel, createdAt, updatedAt, version |
| Feedback | id, kind, eventId, subject, message, status, createdAt, updatedAt, version; статусы new, inProgress, resolved, rejected |
| Notification | id, channel, status, createdAt, deliveredAt, readAt, events; статусы pending, leased, delivered, suppressed, failed |
| NotificationCard | id, title, category, startDate, startTime, city, venue, address, priceMin, priceMax, ageLimit; полная карточка/источники доступны в API каталога по id |
| Delivery | notificationId, userId, leaseToken, leaseExpiresAt, events: NotificationCard[] |

Тело interaction ограничено 64 КиБ; неизвестные JSON-поля отклоняются.
По умолчанию до 20 подписок на пользователя. Канал существующей подписки не меняется:
нужны удаление и новая подписка. Операции агента вызываются после явного подтверждения
пользователем, а не по одной инструкции из текста.

Подборки: по умолчанию до 5 событий, не чаще раза в день на пользователя/канал,
окно 09:00–21:00 Asia/Yekaterinburg, горизонт 30 дней. Перед чтением и доставкой
исключаются неактуальные карточки. Для web delivered означает доступность в inbox.
Бот отправляет одно сообщение на подборку и вызывает ACK; повтор успешного ACK
идемпотентен. Временная ошибка доставки даёт задержки 30/60/120/240 секунд, до
5 выдач; постоянная ошибка — failed. После сбоя между отправкой и ACK возможен
повтор внешнего сообщения (at least once). Уведомления об отменах/переносах как
отдельные сообщения и напоминания пока не реализованы.

### AI: чат и инструменты

Префикс `/ai` удаляется Gateway. Текущий прототип принимает `user_id` из JSON
и доверяет ему; авторизация пользователей в AI пока не реализована.

| Метод | Endpoint через Gateway | Доступ | JSON-тело | Успешный ответ / назначение |
| --- | --- | --- | --- | --- |
| POST | `/ai/api/chat` | — | Обязательны user_id, session_id, message; опциональны request_id (UUID для повторов), subscription_consent (bool, по умолчанию false) | 200, `{user_id, session_id, request_id, run_id, reply, trace_id, cached}`. История сохраняется по паре user_id/session_id |
| GET | `/ai/api/tools` | — | Нет | 200, шесть контрактов `{name, description, parameters, returns, implemented}` |

Форматы ошибок, повторы и согласие на подписку описаны в [AI README](backend/ai/README.md).

### Health, спецификации и страницы документации

| Метод | Endpoint через Gateway | Доступ | Успешный ответ / назначение |
| --- | --- | --- | --- |
| GET | `/ping` | — | 200, OK: живость Traefik |
| GET | `/parser/health` | — | 200, Healthy: живость парсера, без обращения к БД |
| GET | `/parser/health/ready` | — | 200, `{status: ready}`: доступность таблицы очереди; ошибка БД — 503 |
| GET | `/parser/openapi/v1.json` | — | 200, OpenAPI 3.1 парсера |
| GET | `/interaction/health` | — | 200, `{status: healthy}`: живость interaction |
| GET | `/interaction/health/ready` | — | 200, `{status: ready}`: БД и используемые таблицы доступны |
| GET | `/interaction/openapi/v1.json` | — | 200, OpenAPI 3.1 interaction |
| GET | `/search/health` | — | 200, `{status: ok}` после проверки соединения с БД |
| GET | `/search/openapi.json` | — | 200, OpenAPI поиска |
| GET | `/search/docs` | — | 200, Swagger UI поиска |
| GET | `/search/redoc` | — | 200, альтернативная страница ReDoc поиска |
| GET | `/search/docs/oauth2-redirect` | — | 200, служебная страница Swagger для OAuth2 redirect, не самостоятельный механизм авторизации |
| GET | `/ai/health` | — | 200, `{status: ok}`: проверка процесса AI без запроса к модели |
| GET | `/ai/openapi.json` | — | 200, OpenAPI AI с сервером `/ai` |
| GET | `/ai/docs` | — | 200, Swagger UI AI |
| GET | `/ai/redoc` | — | 200, ReDoc AI |
| GET | `/ai/docs/oauth2-redirect` | — | 200, служебная страница Swagger для OAuth2 redirect |

### Endpoint’ы вне Gateway

Прямые порты по умолчанию: parser 5164, interaction 5266, searcher 8000, AI 8001.
Основные пути parser/interaction совпадают с путями через Gateway; у прямого
поиска префикс /search отсутствует, у AI — /ai. Прямые health/OpenAPI находятся
в корне сервиса. Документацию AI в контейнере открывай через Gateway.

| Метод | Прямой адрес | Доступ | Тело / ответ / ограничение |
| --- | --- | --- | --- |
| GET | `http://localhost:5266/docs` | — | 200, справочная HTML-страница interaction. `/interaction/docs` через Gateway не настроен |
| POST | `http://localhost:5164/parser/preview` | P | Только Development: `{html, pageUrl}`, непустой HTML и HTTPS URL карточки ГКЗ `/events/...`; 200, EventCandidate[], без записи в БД |
| GET | `http://localhost:5164/parser/preview/gkz` | P | Только Development: загрузить фиксированную карточку ГКЗ, сохранить исходник, вернуть EventCandidate[] |
| GET | `http://localhost:5164/parser/preview/gkz/links` | P | Только Development: загрузить афишу, сохранить исходник; 200, `{rawDocumentId, count, links}` |
| GET | `http://localhost:5164/parser/preview/gkz/collect` | P | Только Development: синхронно собрать до 3 страниц и сохранить события; 200, отчёт сбора |

В Production preview отсутствует. В Development без настроенного ключа parser
проверка P отключается. У миграционного сервиса HTTP endpoint’ов нет.

### Ошибки и спецификации

| Где | HTTP / формат |
| --- | --- |
| Parser и interaction | application/problem+json: `{type, title, status, instance, code, traceId, errors}`; errors — массив `{field, code, message}` или [] |
| Ошибки .NET API | 400 — ввод/параметры, 401 — ключ, 404 — запись/маршрут, 405 — метод, 409 — конфликт/версия, 413 — размер, 415 — тип тела, 429 — лимит, 500 — внутренняя ошибка, 503 — БД/схема; parser также 502/504 для источника |
| 429 | Retry-After содержит паузу в секундах; interaction дополнительно возвращает feedback_rate_limit или subscription_limit (последний с HTTP 409) |
| Parser: внешняя загрузка | 502 — источник недоступен, 504 — таймаут; ошибка фонового сбора находится в задании и не меняет уже выданный 202 |
| Parser: частичный импорт | 200, ошибки конкретных строк внутри ImportResponse; испорченная структура файла отклоняет запрос целиком |
| Searcher | 422 — валидация FastAPI (detail с ошибками полей); 404 — `{detail: "Событие не найдено"}`; 503 — `{detail: "База событий временно недоступна"}` |
| AI | 422 — валидация FastAPI; 409 — session_busy/request_conflict; 502 — generation_failed; 503 — storage_unavailable. Ошибки сервиса: `{detail: {code, message, request_id, run_id?}}` |
| Gateway | Неизвестный префикс — 404 Traefik; недоступный backend — ошибка прокси, обычно 502/503/504. Единого JSON-формата для ошибок самого Traefik и разных backend нет |

Точные JSON-схемы доступны по перечисленным OpenAPI URL. Правила хранения,
повторной доставки и настройки: [Parser API](backend/parser/README.md),
[Searcher API](backend/searcher/README.md), [Interaction API](backend/interactionService/README.md),
[AI API](backend/ai/README.md).
