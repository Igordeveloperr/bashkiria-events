# Parser API

Микросервис собирает афишу Башкортостана, сохраняет исходные страницы в PostgreSQL,
нормализует мероприятия, объединяет дубли и предоставляет API сбора и модерации.
Стек: ASP.NET Core / .NET 10, EF Core 10, Npgsql, AngleSharp. Миграциями управляет
отдельный сервис `backend/db` через Alembic.

## Запуск

Команды выполняются из корня репозитория. Нужны .NET SDK 10 и PostgreSQL.

```powershell
docker compose up -d postgres
docker compose run --rm --build migrate
docker compose port postgres 5432
Copy-Item backend/parser/.env.example backend/parser/.env
```

В `backend/parser/.env` укажи фактические реквизиты БД, например:

```dotenv
ConnectionStrings__ParserDatabase="Host=localhost;Port=55432;Database=bashkiria;Username=bashkiria;Password=replace-me;Timeout=5;Command Timeout=10"
```

```powershell
dotnet run --project backend/parser/parser --launch-profile http
```

Адрес профиля `http`: `http://localhost:5164`. В Visual Studio запускается проект
`backend/parser/parser`. Файл `.env` читается только в Development.
Корневой `.env` и `.env` парсера используются отдельно.

Для Docker задай `PARSER_API_KEY` в корневом окружении / `.env`:

```powershell
docker compose --profile parser up -d --build
```

Compose ждёт готовности PostgreSQL и успешных миграций. БД внутри Docker:
`postgres:5432`; HTTP-порт контейнера: `8080`; внешний адрес по умолчанию:
`127.0.0.1:5164`. `PARSER_HTTP_PORT` меняет внешний порт,
`PARSER_SCHEDULER_ENABLED` включает расписание (в Compose по умолчанию `true`).
Контейнер работает в Production от непривилегированного пользователя;
`.env` парсера в образ не копируется.

## Общие правила API

- Основной префикс: `/api/v1/parser`. JSON-поля имеют имена `camelCase`.
- Для тела JSON передавай `Content-Type: application/json`.
- Авторизация: `X-Parser-Key`, значение из `Parser:ApiKey`. В Development без
  настроенного ключа проверка отключена. В остальных окружениях пустой ключ
  запрещает запуск приложения.
- Основные и preview-маршруты имеют общий лимит **60 запросов за фиксированное
  минутное окно на экземпляр**, без очереди. Лимит общий для клиентов;
  превышение возвращает `429`.
- `{id}` — UUID. Дата: `yyyy-MM-dd`, время: `HH:mm:ss` с возможной дробной частью
  секунд, временная метка: ISO 8601 с UTC-смещением.
- Локальные дата и время мероприятия относятся к `Asia/Yekaterinburg` (UTC+05:00).
  `dateStart`, `dateEnd` и служебные временные метки хранятся в UTC.
- `null` означает отсутствие данных. Неизвестная цена не равна бесплатному входу,
  неизвестное время не заменяется полуночью.
- Enum в JSON передаются строками `camelCase`; значения перечислены в контрактах.

### Ошибки

Обработчик исключений возвращает `application/problem+json` с полями `type`,
`title`, `status`, `traceId`. Пример:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.4",
  "title": "База данных недоступна или схема не обновлена.",
  "status": 503,
  "traceId": "идентификатор запроса"
}
```

| Код | Причина |
| --- | --- |
| `400` | Неверные параметры, JSON или данные модерации |
| `401` | Отсутствует или неверен `X-Parser-Key` |
| `404` | Запись отсутствует; источник неизвестен / отключён; маршрут недоступен |
| `409` | Устаревшая версия, конфликт уникальности, потеря аренды или невозможность отмены задания |
| `429` | Превышен лимит API |
| `500` | Необработанная ошибка сервиса |
| `502` | Ошибка загрузки внешнего источника при синхронном запросе |
| `503` | БД недоступна, схема не обновлена или запись данных завершилась ошибкой |
| `504` | Таймаут внешнего источника при синхронном запросе |

Прямые проверки параметров отдельных методов возвращают `{"message":"..."}`
или пустое тело. `404` при поиске записи и `204` также могут быть без тела.
Ошибки привязки параметров обрабатывает ASP.NET Core. Единого формата для всех
ошибок сейчас нет. Ошибка фонового сбора записывается в задание и не меняет
уже возвращённый HTTP `202`.

## Endpoint’ы

Пути указаны полностью. Для `/api/v1/parser/*` действуют авторизация, лимит
и общие ошибки выше. Если тело или query-параметры не описаны, они не требуются.

### Служебные маршруты

| Метод и путь | Ответ и назначение |
| --- | --- |
| `GET /health` | `200`, текст `Healthy`: liveness без обращения к БД |
| `GET /health/ready` | `200`, `{"status":"ready"}`: запрос к таблице очереди; ошибка БД / отсутствующая таблица — `503` |
| `GET /openapi/v1.json` | `200`, спецификация OpenAPI 3.1, генерируемая приложением |

Доступны без ключа и без лимита `parser-api`. Readiness проверяет таблицу
`parser_collection_runs`, а не всю схему или внешние сайты. Swagger UI не подключён.

### GET /api/v1/parser/sources

`200`: массив настроенных источников, включая отключённые:

```json
[
  {
    "id": "gkz",
    "kind": "Gkz",
    "enabled": true,
    "listingUrl": "https://gkzbashkortostan.ru/afisha/",
    "priority": 100
  }
]
```

`kind` — `Gkz`, `Structured`, `Gorodzovet`, `Bashgf` или `Bashopera`. Больший целочисленный `priority` даёт источнику
преимущество при обновлении объединённой карточки.

### POST /api/v1/parser/runs/{sourceId}

Ставит сбор в очередь. `sourceId` — ID включённого источника без учёта регистра.

| Query | Тип | По умолчанию | Ограничение |
| --- | --- | --- | --- |
| `maxPages` | integer | `Parser:MaxPagesPerRun` | `1..Parser:MaxPagesPerRun`; лимит карточек, не страниц списка |

`202`: `CollectionRunResponse`, `Location` содержит путь получения задания.
HTTP-запрос не ожидает завершения сбора. При наличии активного задания источника
возвращается оно же; исходный `maxPages` не меняется.
`400`: неверный лимит. `404`: неизвестный / отключённый источник.

```powershell
$base = 'http://localhost:5164'
$headers = @{ 'X-Parser-Key' = $env:PARSER_API_KEY }
$run = Invoke-RestMethod -Method Post -Headers $headers `
    -Uri "$base/api/v1/parser/runs/gkz?maxPages=3"
Invoke-RestMethod -Headers $headers -Uri "$base/api/v1/parser/runs/$($run.id)" |
    ConvertTo-Json -Depth 15
```

### GET /api/v1/parser/runs

`200`: массив `CollectionRunResponse`, последние 50 заданий по `createdAt`
от новых к старым. Фильтров и пагинации нет.

### GET /api/v1/parser/runs/{id}

`200`: `CollectionRunResponse`. `404`: задание отсутствует.
Отчёт появляется после завершения сбора; промежуточные счётчики не выдаются.

### DELETE /api/v1/parser/runs/{id}

Отменяет задание `queued` / `running`, сохраняет историю.
`204`: отмена принята. `409`: задание отсутствует или уже завершено.
Загрузка в процессе может закончиться сохранением исходника; сбор остановится
на следующей проверке аренды. Записанные мероприятия не откатываются.

### GET /api/v1/parser/events

| Query | Тип | По умолчанию | Ограничение / смысл |
| --- | --- | --- | --- |
| `page` | integer | `1` | `1..100000` |
| `pageSize` | integer | `30` | `1..100` |
| `status` | PublicationStatus | без фильтра | `draft`, `published`, `cancelled` |
| `city` | string | без фильтра | Точное совпадение с сохранённым городом |

`200`: `PageResponse<EventResponse>`. Сортировка: `startDate`, затем `id`
по возрастанию. `total` учитывает фильтры до пагинации. За пределами списка
возвращается `items: []`. `400`: неверные параметры.
Без `status` возвращаются все статусы, включая черновики и отменённые записи.

### GET /api/v1/parser/events/published

Query: только `page` / `pageSize`; значения, ограничения и сортировка — как у
`/events`. `200`: `PageResponse<EventResponse>`. `400`: неверная пагинация.

Условия включения записи:

- `status = published`, `sourceStatus` не `cancelled` и не `postponed`;
- `verifiedAt` не старше `Parser:FreshnessHours`;
- `endDate ?? startDate` не раньше текущей даты в `Asia/Yekaterinburg`;
- при известном `dateEnd` окончание ещё не наступило; без него событие проходит
  фильтр, если `endDate > startDate` либо `dateStart` ещё не наступил.

Однодневное событие без известного окончания исчезает из выдачи после времени
начала. Метод также требует ключ: опубликованные данные не означают анонимный доступ.

### GET /api/v1/parser/events/{id}

`200`: `EventResponse` с версией и источниками. `404`: запись отсутствует.
Фильтры свежести и публикации не применяются.

### PATCH /api/v1/parser/events/{id}/review

Модерация и исправление карточки. JSON-тело:

| Поле | Тип | Обязательно | Значение / ограничение |
| --- | --- | --- | --- |
| `expectedVersion` | integer, int64 | Да | Текущий `version` карточки |
| `status` | PublicationStatus | Да | Новый статус публикации |
| `note` | string | Да | Непустое обоснование, до 2000 символов |
| `title`, `city`, `venue`, `address`, `description` | string / null | Нет | Исправленные текстовые поля |
| `startDate`, `startTime` | date / null, time / null | Нет | Локальное начало мероприятия |
| `priceMin`, `priceMax` | decimal / null | Нет | Неотрицательные цены, максимум не меньше минимума |
| `ageLimit` | integer / null | Нет | `0`, `6`, `12`, `16`, `18` |
| `confirmedSourceStatus` | SourceEventStatus / null | Нет | Подтверждённый модератором статус источника |

Пропущенные опциональные поля и `null` сохраняют прежнее значение. Очистить поле
через `null` нельзя. Категория, теги, окончание, ссылки и координаты этим методом
не редактируются.

```json
{
  "expectedVersion": 3,
  "status": "draft",
  "note": "Требуется уточнить стоимость билетов"
}
```

`204`: изменения сохранены, версия увеличена, добавлен аудит.
`404`: событие отсутствует. `409`: устаревшая версия или конфликт уникальности;
нужно заново получить карточку. `400`: неверные данные / условия публикации.

Для `published` обязательны название, город, площадка, адрес, дата и время начала,
известная минимальная цена или бесплатный вход, возраст и действующий статус.
Невалидные диапазоны, прошедшая дата и неподтверждённый статус блокируют публикацию.
Подтверждённую источником отмену нельзя снять одной модерацией: сначала требуются
обновлённые данные источника.

Исходные `issues` и `confidence` остаются сведениями о разборе: модерация их
не пересчитывает и не обновляет `verifiedAt`. Подтверждение сохраняется в аудите.
Ручное снятие с публикации сохраняется при следующих автоматических сборах.

### GET /api/v1/parser/events/{id}/audit

`200`: последние 100 записей аудита, новые первыми:

| Поле | Тип | Смысл |
| --- | --- | --- |
| `id`, `createdAt` | UUID, timestamp | ID и время записи |
| `action` | string | `collected`, `source_updated`, `source_observed`, `moderated` |
| `actor` | string | `source:{sourceId}` или `api-key` |
| `snapshot` | object | Сбор: `before`, `after`, `rawDocumentId`; модерация: `before`, `review` |

Пагинации нет. Для неизвестного события возвращается `200` и `[]`.

### GET /api/v1/parser/raw-documents/{id}

`200`: `RawDocument`, включая полный текст ответа в JSON-поле `content`.
`404`: документ отсутствует. HTML не возвращается как исполняемая HTML-страница.

### POST /api/v1/parser/raw-documents/{id}/reparse

Разбирает сохранённый документ текущим парсером и сохраняет мероприятия.
Новой HTTP-загрузки нет.

`200`:

```json
{
  "rawDocumentId": "00000000-0000-0000-0000-000000000001",
  "results": [
    {
      "eventId": "00000000-0000-0000-0000-000000000002",
      "outcome": "unchanged",
      "status": "draft"
    }
  ]
}
```

`outcome`: `added`, `updated`, `unchanged`; `status`: PublicationStatus.
Без кандидатов возвращается `results: []`.
`400`: HTTP-статус документа вне `200..299`.
`404`: нет документа либо источник неизвестен / отключён.
Старый исходник не заменяет более новую проверку. `fetchedAt` не меняется:
повторный разбор не подтверждает свежесть данных на сайте. Кандидаты сохраняются
отдельными транзакциями; общей транзакции на документ нет.

### GET /api/v1/parser/stats

`200`:

```json
{
  "rawDocuments": 10,
  "events": [{ "status": "draft", "count": 3 }],
  "runs": [{ "status": "completed", "count": 1 }]
}
```

Счётчики всех сохранённых данных, без фильтра свежести. Нулевые группы отсутствуют.
Запросы счётчиков выполняются последовательно, без единого транзакционного снимка.

### Preview: только Development

В остальных окружениях маршруты не регистрируются (`404`). Действуют тот же ключ
и общий лимит API. Синхронные загрузки ожидают ответ внешнего сайта.

| Метод и путь | Вход | Успешный ответ и запись данных |
| --- | --- | --- |
| `POST /parser/preview` | JSON: `html` — непустая строка; `pageUrl` — HTTPS URL карточки ГКЗ, путь `/events/...` | `200`, EventCandidate[]; без HTTP-загрузки и записи в БД |
| `GET /parser/preview/gkz` | Нет | `200`, EventCandidate[] фиксированной карточки; исходник сохраняется, мероприятия — нет |
| `GET /parser/preview/gkz/links` | Нет | `200`, `{rawDocumentId: UUID, count: integer, links: string[]}`; исходник сохраняется |
| `GET /parser/preview/gkz/collect` | Нет | `200`, CollectionResult; до 3 карточек, исходники и мероприятия сохраняются |

Фиксированная карточка `/gkz`:
`https://gkzbashkortostan.ru/events/yubilejnyj-gala-konczert-studii-diva/`.
`/gkz/links` загружает `https://gkzbashkortostan.ru/afisha/`.
POST возвращает `400` при неверном HTML / URL. URL должен иметь разрешённый хост
ГКЗ, стандартный HTTPS-порт и не содержать учётных данных.
Preview-разбор не выполняет полную нормализацию хранилища; окончательные проблемы
и статус публикации определяются при сохранении мероприятия.

## Контракты ответов

### PageResponse<T>

`{total: integer, page: integer, pageSize: integer, items: T[]}`.

### CollectionRunResponse

| Поле | Тип | Смысл |
| --- | --- | --- |
| `id`, `sourceId` | UUID, string | ID задания и источника |
| `status` | CollectionRunStatus | Состояние задания |
| `maxPages` | integer | Лимит карточек задания |
| `attempts` | integer | Число получений задания worker; при постановке `0` |
| `createdAt` | timestamp | Постановка в очередь |
| `startedAt`, `finishedAt` | timestamp / null | Начало последней попытки и завершение |
| `error` | string / null | Ошибка всего задания / причина отмены |
| `report` | CollectionResult / null | Итог успешного / частично успешного сбора |

### CollectionResult

| Поле | Тип | Смысл |
| --- | --- | --- |
| `sourceId`, `listingDocumentId` | string, UUID / null | Источник, первый документ афиши |
| `discoveredPages` | integer | Уникальные найденные ссылки; может превышать лимит |
| `processedPages` | integer | Карточки, для которых начата загрузка |
| `downloadedPages` | integer | Успешно загруженные карточки |
| `parsedPages` | integer | Карточки с хотя бы одним кандидатом |
| `items` | EventCandidate[] | Сохранённые кандидаты; карточка может дать несколько сеансов |
| `errors` | CollectionError[] | Ошибки страниц / обхода |
| `addedEvents`, `updatedEvents`, `unchangedEvents` | integer | Результаты сохранения кандидатов, не число страниц |
| `undatedCandidates`, `reviewRequiredCandidates` | integer | Кандидаты без даты / требующие проверки |

`CollectionError`: `{pageUrl: string, code: string, message: string}`.

| `code` | Причина |
| --- | --- |
| `listing_failed` | Не загрузилась следующая страница списка; прежние ссылки обрабатываются |
| `empty_listing` | В афише нет подходящих ссылок |
| `download_timeout`, `download_failed` | Таймаут / ошибка загрузки карточки |
| `source_unavailable` | Достигнут лимит последовательных ошибок загрузки |
| `parse_failed`, `no_candidates` | Исключение разбора / отсутствие кандидатов |

Ошибка первой загрузки афиши или сохранения в БД завершает задание как `failed`
с `error`, без `report`. Ошибки отдельных карточек дают `completedWithErrors`.

### EventResponse

| Поле | Тип | Смысл |
| --- | --- | --- |
| `id` | UUID | ID мероприятия |
| `title`, `normalizedTitle` | string | Название и нормализованное название для сравнения |
| `category`, `tags` | EventCategory, string[] | Категория и теги |
| `startDate`, `endDate` | date / null | Локальные даты начала / окончания |
| `startTime`, `endTime` | time / null | Локальное время начала / окончания |
| `dateStart`, `dateEnd` | timestamp / null | UTC-моменты при известных дате и времени |
| `durationMin` | integer / null | Продолжительность при известном начале и окончании |
| `timeZone` | string | `Asia/Yekaterinburg` |
| `city`, `venue`, `address`, `description` | string | Город, площадка, адрес, описание; неизвестные — пустые строки |
| `latitude`, `longitude` | decimal / null | Координаты: `-90..90`, `-180..180` |
| `priceMin`, `priceMax` | decimal / null | Минимальная / максимальная цена |
| `isFree` | boolean / null | Бесплатный вход / платный вход / неизвестно |
| `ageLimit` | integer / null | Возрастное ограничение |
| `language`, `orgLink`, `imageUrl` | string / null | Язык, организатор, изображение |
| `status`, `sourceStatus` | PublicationStatus, SourceEventStatus | Публикация и статус источника |
| `confidence` | decimal | Эвристика качества, `0.1..0.95` |
| `issues` | ParsingIssue[] | Проблемы исходных данных и нормализации |
| `requiresReview` | boolean | `status == draft` |
| `verifiedAt` | timestamp | Последнее подтверждение основных данных источником |
| `createdAt`, `updatedAt` | timestamp | Создание / изменение записи |
| `reviewedAt`, `reviewNote` | timestamp / null, string / null | Последняя модерация и обоснование |
| `version` | integer, int64 | Версия для `expectedVersion` |
| `sources` | EventSourceResponse[] | Происхождение карточки |
| `sourceType` | string | `page` |
| `sourceHash` | string | SHA-256 нормализованного кандидата основных данных; не хеш HTML |
| `dedupKey` | string | SHA-256 ключа межисточниковой дедупликации |

`EventSourceResponse`: `{sourceId: string, sourceUrl: string, rawDocumentId: UUID,
checkedAt: timestamp}`. Проверки второстепенного источника отражаются в его
`checkedAt` и не обязательно обновляют `verifiedAt` основной карточки.

### EventCandidate

Поля извлечённого кандидата в preview и отчётах:

| Поля | Тип |
| --- | --- |
| `title`, `sourceUrl` | string, string (URL) |
| `rawDocumentId`, `externalId` | UUID / null, string / null |
| `startDate`, `endDate`, `startTime`, `endTime` | date / null, date / null, time / null, time / null |
| `city`, `venue`, `address`, `description`, `language` | string / null |
| `priceMin`, `priceMax`, `latitude`, `longitude` | decimal / null |
| `ageLimit`, `isFree` | integer / null, boolean / null |
| `category`, `sourceStatus`, `tags` | EventCategory, SourceEventStatus, string[] |
| `orgLink`, `imageUrl` | string (URL) / null |
| `issues`, `requiresReview` | ParsingIssue[], boolean |

`externalId` — ID события / сеанса у источника. У кандидата `requiresReview`
означает отсутствие даты, времени или наличие `issues`; у EventResponse — статус `draft`.

### RawDocument

| Поле | Тип | Смысл |
| --- | --- | --- |
| `id`, `sourceId` | UUID, string | ID исходника и источника |
| `url` | string (URL) | Адрес конкретного HTTP-ответа |
| `fetchedAt`, `httpStatus` | timestamp, integer | Время получения и HTTP-код источника |
| `content` | string | Декодированный текст ответа |
| `contentHash` | string | SHA-256 UTF-8 представления `content`, 64 строчных hex-символа |

### Enum

| Тип | Значения |
| --- | --- |
| PublicationStatus | `draft`, `published`, `cancelled` |
| SourceEventStatus | `unknown`, `scheduled`, `cancelled`, `postponed`, `rescheduled`, `soldOut` |
| CollectionRunStatus | `queued`, `running`, `completed`, `completedWithErrors`, `failed`, `cancelled` |
| EventCategory | `other`, `concert`, `theatre`, `exhibition`, `festival`, `cinema`, `excursion`, `workshop`, `lecture` |

### ParsingIssue

Формат: `{code: string, message: string}`; сообщение содержит пояснение на русском.

| Коды | Причина |
| --- | --- |
| `missing_title`, `missing_city`, `missing_venue`, `missing_address` | Нет обязательного текстового поля |
| `missing_start_date`, `missing_start_time` | Нет даты / времени начала |
| `unknown_price`, `unknown_age` | Неизвестны цена / возраст |
| `invalid_price`, `price_conflict` | Неверные цены / противоречие бесплатному входу |
| `invalid_age`, `invalid_coordinates`, `invalid_end` | Неверные возраст, координаты, окончание |
| `expired`, `unconfirmed_status` | Прошедшая дата / статус unknown или postponed |
| `region_unconfirmed` | Не подтверждена принадлежность площадки к Башкортостану |
| `session_identity_unconfirmed` | Несколько сеансов без устойчивых ID |
| `invalid_visible_date`, `invalid_visible_time` | Неразбираемая дата / время расписания ГКЗ |
| `multiple_start_values` | Несколько вариантов начала в карточке ГКЗ |
| `calendar_time_unconfirmed` | Время календаря требует подтверждения |
| `start_date_conflict`, `start_time_conflict` | Расписание, календарь или JSON-LD противоречат друг другу |
| `source_status_conflict` | Источники расходятся в статусе, второстепенный сообщает отмену / перенос |
| `schedule_unconfirmed` | У агрегатора не найдено отдельное видимое расписание сеансов |
| `session_end_conflict`, `session_venue_conflict` | Строки расписания агрегатора расходятся в окончании / площадке |
| `inferred_start_year` | Год сеанса театра сопоставлен по дню недели и требует проверки |

## Конфигурация

Секция `Parser` находится в `parser/appsettings.json`. В переменных окружения
используется `__` вместо `:`, например `Parser__RequestTimeoutSeconds=30`.
Приоритет: аргументы командной строки → окружение → JSON-настройки.
Development-файл `backend/parser/.env` добавляет отсутствующие переменные
через `NoClobber`, не заменяя существующие. Настройки проверяются при запуске
и автоматически не перезагружаются.

| Ключ внутри `Parser` | По умолчанию | Допустимые значения / единицы |
| --- | --- | --- |
| `ApiKey` | не задан | Непустая строка обязательна вне Development |
| `SchedulerEnabled` | `false` | boolean; в Compose по умолчанию `true` |
| `ScheduleIntervalHours` | `6` | `1..168` часов |
| `MaxPagesPerRun` | `200` | `1..500` карточек |
| `MaxListingPages` | `5` | `1..30` страниц списка; ГКЗ и театр оперы обходят одну |
| `RequestTimeoutSeconds` | `20` | `1..60` секунд на HTTP-запрос |
| `RequestDelayMilliseconds` | `500` | `250..10000` мс перед запросом страницы |
| `MaxConsecutiveFailures` | `3` | `1..10` последовательных ошибок загрузки |
| `FreshnessHours` | `24` | `1..168` часов для `/events/published` |
| `Sources` | см. ниже | 1–20 элементов, ID уникальны без учёта регистра |

### Sources

| Поле | По умолчанию | Требования |
| --- | --- | --- |
| `Id` | — | `^[a-z][a-z0-9_-]{0,63}$` |
| `Kind` | `Structured` | `Gkz`, `Structured`, `Gorodzovet`, `Bashgf`, `Bashopera`; регистр значим |
| `Enabled` | `true` | Отключённый источник виден в `/sources`, но не собирается |
| `ListingUrl` | — | Абсолютный HTTPS URL, стандартный порт, без учётных данных |
| `EventPathPattern` | `^/events/` | Regex пути карточки, таймаут 100 мс |
| `Priority` | `50` | integer, больший предпочтительнее |
| `City`, `Venue`, `Address` | `null` | Известная площадка; Structured использует значения при отсутствии в разметке, адаптеры учреждений — из конфигурации |
| `UserAgent` | `BashkiriaEventsParser/1.0` | HTTP User-Agent; `null` / пустая строка отключают заголовок |

Настроенные источники:

| ID | Kind | Афиша | Приоритет | Включён |
| --- | --- | --- | --- | --- |
| `gkz` | `Gkz` | `https://gkzbashkortostan.ru/afisha/` | `100` | Да |
| `gorodzovet` | `Gorodzovet` | `https://gorodzovet.ru/ufa/culture/` | `40` | Да |
| `bashgf` | `Bashgf` | `https://bashgf.ru/afisha/` | `100` | Да |
| `bashopera` | `Bashopera` | `https://bashopera.ru/affiche/` | `100` | Да |
| `culture` | `Structured` | `https://www.culture.ru/afisha/respublika-bashkortostan` | `50` | Нет: проверка браузера / HTTP 425 |

Пути карточек: ГКЗ — `^/events/[^/]+/?$`, ГородЗовёт —
`^/ufa/[^/]+-event[0-9]+/?$`, филармония — `^/afisha/event/[^/]+/?$`,
опера — `^/repertoire/[^/]+/[0-9]+/?$`, Культура.РФ — `^/events/[0-9]+/`.

Специализированный `Kind` допускает только соответствующий ему хост.
Стратегии разбора регистрируются через `ISourceEventParser` в DI.
Новые источники используют ту же очередь, сохранение исходников, EF Core,
нормализацию, дедупликацию и модерацию.

- ГородЗовёт: текущая конфигурация охватывает раздел «Культура и искусство» Уфы,
  а не общую ленту бизнеса, спорта и других категорий. JSON-LD дополняется видимыми
  строками расписания; отдельные сеансы не превращаются в один длинный период.
  Без устойчивых ID сеансов, при расхождении площадок / окончания или отсутствии
  видимого расписания добавляются проблемы для модерации. Полнота охвата всех
  городов Башкортостана не заявляется.
- Филармония: карточки Events Maker, даты `.dtstart` / `.dtend`, цены и билетные
  ссылки. Равное началу окончание считается неизвестным. Зал сохраняется в тегах;
  город, учреждение и адрес заданы конфигурацией. Следующая страница берётся из
  реальной ссылки `next`, а не из предположения о query-параметре. `UserAgent=null`
  нужен из-за HTTP 415 от хостинга при заголовке нашего приложения.
- Театр оперы: страницы произведений с отдельными сеансами и стабильными ID
  билетных ссылок. Билетные iframe не загружаются. Если нет года, он сопоставляется
  по дню недели среди предыдущего, текущего и следующего годов; только единственный
  вариант сохраняется с `inferred_start_year` и требует проверки. При неоднозначности
  дата остаётся неизвестной. Окончание вычисляется только при явно указанной
  длительности. Неизвестные цена и статус не додумываются.
  У ГородЗовёт переход выполняется только по `rel=next`, если такая ссылка есть;
  динамическая подгрузка карточек JavaScript не выполняется.

Запуск новых источников по 3 карточки (при настроенном ключе добавь `-Headers`):

```powershell
foreach ($sourceId in 'gorodzovet', 'bashgf', 'bashopera') {
    Invoke-RestMethod -Method Post "http://localhost:5164/api/v1/parser/runs/${sourceId}?maxPages=3"
}
```

Для `Gkz` разрешён только хост `gkzbashkortostan.ru`. Значения площадки заданы
адаптером: Уфа, ГКЗ «Башкортостан», ул. Ленина, 50.
Structured читает JSON-LD Event, ссылки `@graph` / `@id`, HTML-микроразметку
Event/Place/Offer. Следующие страницы списка запрашиваются с `?page=N`;
обход прекращается без новых ссылок или при достижении лимита карточек.
Онлайн-события и карточки с явно указанным регионом вне Башкортостана пропускаются.
Без региона используется проверка известных городов / настроенной площадки;
неподтверждённый регион даёт `region_unconfirmed`.
Источник с поддерживаемой разметкой добавляется конфигурацией.
Культура.РФ остаётся в конфигурации отключённой; для возврата к ней требуется
решить вопрос доступа или подключить официальный API отдельным адаптером.

### Подключение к PostgreSQL

| Настройка | Назначение |
| --- | --- |
| `ConnectionStrings:ParserDatabase` / `ConnectionStrings__ParserDatabase` | Основная строка, приоритетнее `PG*` |
| `PGHOST` | Хост; включает сборку строки, если основная отсутствует |
| `PGPORT` | Порт, по умолчанию `5432` |
| `PGDATABASE`, `PGUSER`, `PGPASSWORD` | База, пользователь, пароль |

Для `PG*` задаются `Timeout=5`, `CommandTimeout=10` секунд. В основной строке эти
параметры задаются явно либо используются значения Npgsql.
Модели: `parser/Models`; конфигурации EF: `parser/Data/Configurations`.
Все операции БД асинхронные через EF Core с `CancellationToken`;
контекст создаётся фабрикой для отдельной операции.

## Технические параметры обработки

### HTTP и очередь

| Параметр | Значение / поведение |
| --- | --- |
| User-Agent / Accept | `Sources[].UserAgent` / `text/html`; у филармонии User-Agent отключён |
| Сжатие ответов | Автоматическая распаковка gzip, deflate, Brotli |
| Максимальный размер буферизуемого HTTP-тела | 8 MiB |
| Редиректы | До 3; каждый ответ сохраняется, следующий URL проверяется |
| Ограничение URL | HTTPS того же хоста; www нормализуется, нестандартный порт и credentials запрещены |
| Cookies | Отключены |
| Параллельность | Одно задание на экземпляр worker, страницы загружаются последовательно |
| Активные задания | Одно queued / running на источник, уникальный индекс БД |
| Опрос пустой очереди / пауза после ошибки worker | 3 / 15 секунд |
| Аренда задания | 2 минуты, продлевается на контрольных точках сбора |
| Восстановление после сбоя | Просроченная аренда возвращает задание в очередь; после 3 получений — failed |
| Расписание | При старте worker и далее через ScheduleIntervalHours; уникальный ключ UTC-окна защищает от повторной постановки после рестарта |

Обычная ошибка источника не запускает автоматический повтор задания. Повтор —
новый ручной запуск или следующее окно расписания. Несколько экземпляров получают
задания через атомарное резервирование в БД. Отмена отзывает аренду.
Отсутствие карточки в афише не означает отмену. Ответы `404` / `410` карточек
не увеличивают счётчик общей недоступности источника.

### Сохранение, дубли и публикация

1. Каждый полученный HTTP-ответ, включая `4xx`, `5xx` и редиректы, сохраняется
   до разбора. Повторный HTML также создаёт новый исходник. Ошибка записи исходника
   останавливает обработку: мероприятие без происхождения не сохраняется.
2. При загрузке в БД вычисляется `identityKey = SHA256(sourceId + "|" +
   (externalId ?? canonicalUrl))` для поиска известной записи источника.
3. Межисточниковый `dedupKey` — SHA-256 нормализованных названия, города, площадки,
   даты и времени начала, соединённых `|`. Нормализация: Unicode NFKC, нижний
   регистр, `ё → е`, свёртка пунктуации и пробелов; у названий ГКЗ есть алиас.
   Без даты, времени, города или площадки используется `identityKey`.
   Разные сеансы сохраняются отдельно; нечёткого сравнения названий нет.
4. `sourceHash` / внутренний `dataHash` — SHA-256 сериализованного нормализованного
   кандидата без `rawDocumentId`; определяет изменение данных. `contentHash`
   относится к исходному тексту. Дубли проверяются при сохранении;
   API списка читает готовые записи.
5. Мероприятие, происхождение и аудит сохраняются одной Serializable-транзакцией.
   Для конфликтов сериализации, deadlock и уникальности — до 3 попыток,
   паузы 50 и 100 мс. Уникальные индексы защищают от конкурентных дублей.
6. Старый исходник не заменяет новую проверку. Неизменившиеся данные источника
   сохраняют ручные исправления. Основные поля объединённой карточки заменяются
   источником с не меньшим приоритетом. Отмена / перенос у второстепенного источника
   также снимают карточку с публикации и добавляют `source_status_conflict`;
   наблюдение записывается как `source_observed`.
7. `confidence = clamp(0.95 - 0.1 × число уникальных issues, 0.1, 0.95)`.
   Автопубликация требует полных данных без проблем и `confidence >= 0.9`.
   Подтверждённая отмена даёт `cancelled`, неполные / спорные данные — `draft`.

### Таблицы и миграции

| Таблица | Содержимое | Миграция |
| --- | --- | --- |
| `parser_raw_documents` | Исходные HTTP-ответы и хеши | `0002_parser_raw_documents` |
| `parser_events` | Нормализованные мероприятия | `0003_parser_pipeline` |
| `parser_event_sources` | Происхождение, идентичность, проверки | `0003_parser_pipeline` |
| `parser_event_audit` | Изменения и модерация | `0003_parser_pipeline` |
| `parser_collection_runs` | Очередь, аренды, отчёты | `0003_parser_pipeline` |

Сервис не вызывает `EnsureCreated` / EF migrations. Схему обновляет только
`backend/db`. Автоматической очистки исходников и истории нет. Документы,
на которые ссылаются мероприятия, защищены внешними ключами.
API удаления мероприятий и исходников отсутствует.

## Проверки

```powershell
dotnet test backend/parser/parser.slnx
```

Без `PARSER_TEST_CONNECTION_STRING` PostgreSQL-тесты пропускаются.
Полный прогон в отдельной случайной схеме с Alembic и удалением схемы после тестов:

```powershell
./backend/parser/scripts/Test-Parser.ps1
```

Миграционный сервис и его тесты: [backend/db/README.md](../db/README.md).

## Сводная таблица endpoint’ов

| Метод | Endpoint | Краткое описание |
| --- | --- | --- |
| GET | `/health` | Проверить, что приложение работает, без обращения к БД |
| GET | `/health/ready` | Проверить доступность БД и таблицы очереди |
| GET | `/openapi/v1.json` | Получить спецификацию OpenAPI |
| GET | `/api/v1/parser/sources` | Получить список настроенных источников |
| POST | `/api/v1/parser/runs/{sourceId}` | Поставить сбор источника в очередь; необязательный query `maxPages` ограничивает число карточек |
| GET | `/api/v1/parser/runs` | Получить последние 50 заданий сбора |
| GET | `/api/v1/parser/runs/{id}` | Получить состояние задания и итоговый отчёт |
| DELETE | `/api/v1/parser/runs/{id}` | Отменить активное задание, сохранив историю |
| GET | `/api/v1/parser/events` | Получить мероприятия с пагинацией и фильтрами по статусу и городу |
| GET | `/api/v1/parser/events/published` | Получить опубликованные, свежие и ещё актуальные мероприятия |
| GET | `/api/v1/parser/events/{id}` | Получить карточку мероприятия, её версию и источники |
| PATCH | `/api/v1/parser/events/{id}/review` | Исправить поля и изменить статус публикации с проверкой версии |
| GET | `/api/v1/parser/events/{id}/audit` | Получить последние 100 изменений мероприятия |
| GET | `/api/v1/parser/raw-documents/{id}` | Получить сохранённый HTTP-ответ источника, включая HTML |
| POST | `/api/v1/parser/raw-documents/{id}/reparse` | Повторно разобрать исходник и сохранить мероприятия без новой загрузки |
| GET | `/api/v1/parser/stats` | Получить счётчики исходников, мероприятий и заданий |
| POST | `/parser/preview` | Разобрать переданный HTML карточки ГКЗ без записи в БД; только Development |
| GET | `/parser/preview/gkz` | Загрузить и разобрать фиксированную карточку ГКЗ, сохранив исходник; только Development |
| GET | `/parser/preview/gkz/links` | Загрузить афишу ГКЗ и получить ссылки на карточки, сохранив исходник; только Development |
| GET | `/parser/preview/gkz/collect` | Синхронно собрать до 3 карточек ГКЗ с записью в БД; только Development |
