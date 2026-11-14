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

Через Gateway: `http://localhost:8080/api/v1/parser/...`. Пути и X-Parser-Key
сохраняются. PARSER_API_KEY корневого `.env` передаётся контейнеру как Parser__ApiKey;
для локального запуска настроен тот же ключ в `backend/parser/.env`.
OpenAPI: `http://localhost:8080/parser/openapi/v1.json`.
[Запуск и маршруты Gateway](../gateway/README.md).

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

Ошибки приложения возвращаются в формате ProblemDetails с Content-Type
`application/problem+json`: исключения, проверки endpoint’ов, неверные JSON/query/
заголовки, авторизация, неизвестные маршруты, неподдерживаемый метод и лимит запросов.
Формат сохраняется и при Accept: text/html. Пример:

```json
{
  "type": "urn:bashkiria-events:error:service_unavailable",
  "title": "База данных недоступна или схема не обновлена.",
  "status": 503,
  "instance": "/api/v1/parser/events",
  "code": "service_unavailable",
  "traceId": "идентификатор запроса",
  "errors": []
}
```

| HTTP | code | Причина |
| --- | --- | --- |
| `400` | `validation_error` | Неверные параметры, JSON, заголовки или данные модерации |
| `401` | `unauthorized` | Отсутствует или неверен X-Parser-Key |
| `403` | `forbidden` | Недостаточно прав |
| `404` | `not_found` | Запись, источник или маршрут отсутствует |
| `405` | `method_not_allowed` | Метод HTTP не поддерживается; допустимые методы в Allow |
| `409` | `conflict` | Устаревшая версия, конфликт уникальности, Idempotency-Key или аренды |
| `413` | `payload_too_large` | Превышен допустимый размер тела |
| `415` | `unsupported_media_type` | Неподдерживаемый Content-Type или кодировка |
| `429` | `rate_limit_exceeded` | Превышен лимит API; Retry-After содержит время ожидания в секундах |
| `500` | `internal_error` | Необработанная ошибка сервиса |
| `502` | `source_unavailable` | Ошибка загрузки внешнего источника |
| `503` | `service_unavailable` | БД недоступна или схема не обновлена |
| `504` | `source_timeout` | Таймаут внешнего источника |

type, title, status, instance, code, traceId и errors присутствуют всегда.
instance содержит путь без query; traceId связывает ответ с серверным журналом.
code предназначен для программной обработки; текст title может уточняться.
errors — массив `{field, code, message}` при наличии ошибок полей, иначе [].
Внутренние исключения, SQL и стек вызовов в ответ не попадают. Пустое тело 204 —
успешный ответ. Ошибки строк частичного импорта остаются внутри отчёта 200;
ошибки фонового сбора — в задании и не меняют уже возвращённый HTTP 202.

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
| `cityId` | UUID | без фильтра | Идентификатор населённого пункта из справочника |
| `venueId` | UUID | без фильтра | Идентификатор площадки из справочника |

`200`: `PageResponse<EventResponse>`. Сортировка: `startDate`, затем `id`
по возрастанию. `total` учитывает фильтры до пагинации. За пределами списка
возвращается `items: []`. `400`: неверные параметры.
Без `status` возвращаются все статусы, включая черновики и отменённые записи.

### GET /api/v1/parser/events/published

Query: только `page` / `pageSize`; значения, ограничения и сортировка — как у
`/events`. `200`: `PageResponse<EventResponse>`. `400`: неверная пагинация.

Условия включения записи:

- `status = published`, `sourceStatus` не `cancelled` и не `postponed`;
- `cityId` и `venueId` определены по справочникам;
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
| `cityId`, `venueId` | UUID / null | Нет | Явный выбор из справочников; venueId определяет канонические город, площадку и адрес |
| `startDate`, `startTime` | date / null, time / null | Нет | Локальное начало мероприятия |
| `priceMin`, `priceMax` | decimal / null | Нет | Неотрицательные цены, максимум не меньше минимума |
| `ageLimit` | integer / null | Нет | `0`, `6`, `12`, `16`, `18` |
| `confirmedSourceStatus` | SourceEventStatus / null | Нет | Подтверждённый модератором статус источника |

Пропущенные опциональные поля и `null` сохраняют прежнее значение. Очистить поле
через `null` нельзя. Категория, теги, окончание, ссылки и координаты этим методом
не редактируются.

При исправлении `city`, `venue` или `address` прежняя связь с площадкой сбрасывается
и выполняется новое сопоставление. Новый `cityId` также сбрасывает прежний venueId.
Явные идентификаторы имеют приоритет перед текстом; несовместимые cityId/venueId
дают `400`. Неизвестную или неоднозначную площадку опубликовать нельзя.

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
| `action` | string | `collected`, `source_updated`, `source_observed`, `moderated`, `created_manually`, `imported` |
| `actor` | string | `source:{sourceId}` или `api-key` |
| `snapshot` | object | Сбор: `before`, `after`, `rawDocumentId`; модерация: `before`, `review` |

Пагинации нет. Для неизвестного события возвращается `200` и `[]`.

### GET /api/v1/parser/raw-documents/{id}

`200`: `RawDocument`, включая исходный HTML, JSON или CSV в поле `content`.
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
      "status": "draft",
      "issues": []
    }
  ]
}
```

`outcome`: `added`, `updated`, `unchanged`; `status`: PublicationStatus.
`issues`: ParsingIssue[] с результатами проверки, включая сопоставление справочников.
Без кандидатов возвращается `results: []`.
`400`: HTTP-статус страницы вне `200..299` или некорректное содержимое файла.
`404`: нет документа либо источник страницы неизвестен / отключён.
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

### POST /api/v1/parser/events — ручное создание

JSON-тело: `EventInput` (контракт ниже). Обязательный заголовок
`Idempotency-Key` — непустой UUID, который клиент создаёт один раз и сохраняет
для повторов этого запроса. `externalId` в ручном POST не используется и отклоняется.

- `201`: добавлена новая карточка; ответ `CreateEventResponse`, `Location` — путь карточки.
- `200`: повтор запроса или объединение с уже существующей карточкой; тот же контракт ответа.
- `400`: нет ключа, неверный JSON или поля; ошибки полей содержатся в `errors` ProblemDetails.
- `409`: ключ уже использован с другим содержимым. Изменять карточку нужно через `/review` с `expectedVersion`.
- `413`: тело больше 128 KiB; `415`: нужен `application/json`, кодировка UTF-8.

`CreateEventResponse`: `{eventId: UUID, outcome: string, status: PublicationStatus,
rawDocumentId: UUID, issues: ParsingIssue[], requiresReview: boolean}`.
`outcome`: `added`, `updated`, `unchanged`. Новые полные записи также создаются
черновиками; явно отменённые — отменёнными. Публикация выполняется модерацией.
При совпадении с существующей карточкой она возвращается без перезаписи основных
полей; добавляется происхождение ручного ввода.

```powershell
$headers = @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }
# Если ключ доступа настроен: $headers['X-Parser-Key'] = $env:PARSER_API_KEY
# manual-event.json содержит реальное мероприятие в формате EventInput.
Invoke-RestMethod -Method Post -Uri 'http://localhost:5164/api/v1/parser/events' `
    -Headers $headers -ContentType 'application/json; charset=utf-8' -InFile './manual-event.json'
```

### POST /api/v1/parser/imports/json/{datasetId}

Тело — JSON-массив `EventInput[]`. Content-Type: `application/json; charset=utf-8`.
`datasetId` — стабильное имя набора: `^[a-z][a-z0-9_-]{0,63}$`, например `museum-ufa`.
Для обновления того же набора сохраняй `datasetId`; его записи получают источник
`import:{datasetId}`. ID набора не добавляется в очередь обхода внешних сайтов.

`200`: `ImportResponse`, даже если часть строк отклонена. `400`: неверная структура
файла, ID набора, пустой массив или превышение числа строк. `413`: превышен размер;
`415`: неверный Content-Type / кодировка. Общие ошибки БД — `503`.

```powershell
Invoke-RestMethod -Method Post `
    -Uri 'http://localhost:5164/api/v1/parser/imports/json/museum-ufa' `
    -ContentType 'application/json; charset=utf-8' -InFile './events.json' |
    ConvertTo-Json -Depth 10
```

### POST /api/v1/parser/imports/csv/{datasetId}

Тело — сам CSV-файл UTF-8, допускается BOM. Content-Type: `text/csv; charset=utf-8`.
`multipart/form-data` не используется. Ответы и правила `datasetId` — как у JSON.

- Разделитель `,` или `;` определяется автоматически. Кавычки, `""` и переносы
  строк внутри поля обрабатываются CsvHelper.
- Первая запись — заголовки. Допустимы имена EventInput в camelCase или snake_case,
  регистр незначим; обязателен `title`. Неизвестные и повторяющиеся заголовки дают `400`.
- Пустая ячейка означает отсутствие значения. Даты: `yyyy-MM-dd`, время:
  `HH:mm` / `HH:mm:ss`. Enum — как в JSON, boolean — `true` / `false`.
- Числа допускают десятичную точку или запятую; поле с запятой нужно заключить
  в кавычки, если она же служит разделителем. Цены указываются числом без `руб` / `₽`.
- Теги внутри ячейки разделяются `|`. Максимальный размер одного CSV-поля — 65536 символов.
- Нарушенная структура CSV отклоняет весь файл до записи данных. Неверные значения
  отдельных полей отклоняют соответствующую запись и отражаются в отчёте.

```powershell
Invoke-RestMethod -Method Post `
    -Uri 'http://localhost:5164/api/v1/parser/imports/csv/museum-ufa' `
    -ContentType 'text/csv; charset=utf-8' -InFile './events.csv' |
    ConvertTo-Json -Depth 10
```

Пример строки заголовков:

```csv
externalId;title;category;startDate;startTime;city;venue;address;priceMin;priceMax;isFree;ageLimit;sourceUrl;sourceStatus;tags
```

### EventInput — поля ручного ввода и импорта

| Поля | Тип | Правило |
| --- | --- | --- |
| `title` | string | Обязательно, непустое, до 500 символов |
| `externalId` | string / null | Только импорт: стабильный ID события / сеанса внутри набора, до 200 символов |
| `sourceUrl` | URL / null | Ссылка на реальное событие, если есть; HTTP(S), без credentials, до 2048 символов |
| `category` | EventCategory | По умолчанию `other` |
| `tags` | string[] / null | До 50 непустых тегов, каждый до 100 символов |
| `startDate`, `endDate` | date / null | Локальные даты |
| `startTime`, `endTime` | time / null | Локальное время в Asia/Yekaterinburg |
| `city`, `venue`, `address` | string / null | До 200 / 500 / 2000 символов |
| `cityId`, `venueId` | UUID / null | Выбор из справочника; venueId определяет город, площадку и адрес |
| `description` | string / null | До 50000 символов |
| `latitude`, `longitude` | decimal / null | ±90 / ±180, до 7 знаков после точки |
| `priceMin`, `priceMax` | decimal / null | Рубли, 0..9999999999.99, до 2 знаков после точки; максимум не меньше минимума |
| `isFree` | boolean / null | `true` задаёт цены 0/0; противоречие положительной цене отклоняется |
| `ageLimit` | integer / null | `0`, `6`, `12`, `16`, `18`; неизвестное — `null` |
| `language` | string / null | До 100 символов |
| `orgLink`, `imageUrl` | URL / null | HTTP(S), без credentials, до 2048 символов |
| `sourceStatus` | SourceEventStatus | По умолчанию `unknown`; статус у источника, не команда публикации |

Неизвестные JSON-поля и повторяющиеся имена отклоняются. Нулевые символы в тексте
не допускаются. Известное время окончания без `endDate` относится к `startDate`;
для перехода через полночь укажи `endDate` явно. Окончание раньше начала отклоняется.
Город, площадка и адрес могут заполняться по проверенному справочнику. Неизвестные
справочнику данные сохраняются как исходный текст с `issues`. Начало, цена и возраст
не придумываются. Неизвестный cityId/venueId даёт 400 при ручном вводе или rejected
с ошибкой invalid_reference для строки импорта.

### ImportResponse и идемпотентность

| Поле | Тип | Назначение |
| --- | --- | --- |
| `rawDocumentId` | UUID | Сохранённый оригинал файла |
| `datasetId` | string / null | Имя набора; `null` при повторном разборе ручной записи |
| `total` | integer | Всего входных записей |
| `added`, `updated`, `unchanged`, `rejected` | integer | Итоги обработки; сумма равна total |
| `requiresReview` | integer | Результаты со статусом draft |
| `items` | ImportRowResponse[] | Результат каждой записи |

`ImportRowResponse`: `{row: integer, externalId: string|null, eventId: UUID|null,
outcome: string, status: PublicationStatus|null, issues: ParsingIssue[], errors: InputError[]}`.
`outcome`: `added`, `updated`, `unchanged`, `rejected`.
`InputError`: `{field: string, code: string, message: string}`.
`row` — номер записи: в JSON начиная с 1, в CSV начиная с 2 с учётом заголовка;
это не номер физической строки при многострочных ячейках. При отказе `eventId` и
`status` равны `null`. `issues` относятся к входным данным, `status` — к полученной
карточке, включая уже существующую при объединении.

- Повтор того же файла в том же наборе повторно использует исходный документ,
  не создаёт дубли и возвращает `unchanged` для неизменившихся записей.
- Для обновления, включая перенос сеанса, передавай один и тот же `externalId`.
  Повторяющийся явный ID внутри файла отклоняется начиная со второго появления.
- Без `externalId` используется ключ названия, города, площадки, даты и времени.
  Если идентифицирующих данных не хватает — хеш входной записи. Перенос без
  стабильного ID может создать новую карточку; для обновляемых наборов ID обязателен.
- Импорт не перезаписывает основные поля чужой карточки при первом объединении;
  его приоритет ниже сайтов учреждений и ручного ввода. Отмена в файле не снимает
  с публикации карточку, подтверждённую более приоритетным источником.
- Изменившаяся собственная запись импорта снова требует модерации; повтор
  неизменившейся записи сохраняет ручные исправления и статус.
- URL из файла не загружаются. `verifiedAt` исходно означает время загрузки данных;
  публикация модератором подтверждает ручную / импортную карточку заново.
- Исходники ручного ввода и файлов имеют честный внутренний URN
  `urn:bashkiria-events:...`, `httpStatus=null`, тип `manual` / `import` и MIME-тип.
  При отсутствии внешнего `sourceUrl` такой URN также используется в происхождении
  мероприятия. Несуществующие веб-ссылки не генерируются.
- Обработка выполняется в HTTP-запросе, последовательно, с CancellationToken.
  Файл сохраняется до записи мероприятий; каждое мероприятие — отдельная транзакция.
  При обрыве / ошибке БД часть записей может быть сохранена: повтори тот же файл.
  Отчёт возвращается ответом POST; отдельного хранилища отчётов импорта нет.
- `/raw-documents/{id}/reparse` поддерживает файлы и ручные записи без новых
  загрузок. Для них возвращается ImportResponse; исходное время не меняется,
  более новая версия записи не откатывается старым файлом.

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
| `cityId`, `venueId` | UUID / null | Связи со справочниками; null при неустановленном соответствии |
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
| `sourceType` | EventSourceType | Тип основных данных: `page`, `import`, `manual` |
| `sourceHash` | string | SHA-256 нормализованного кандидата основных данных; не хеш HTML |
| `dedupKey` | string | SHA-256 ключа межисточниковой дедупликации |

`EventSourceResponse`: `{sourceId: string, sourceUrl: string, rawDocumentId: UUID,
checkedAt: timestamp, sourceType: EventSourceType}`. Проверки второстепенного источника отражаются в его
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
| `url` | string (URI) | URL HTTP-ответа или внутренний URN ручного ввода / файла |
| `fetchedAt`, `httpStatus` | timestamp, integer / null | Время получения; HTTP-код только для страниц |
| `sourceType`, `contentType` | EventSourceType, string | `page` / `import` / `manual`; MIME-тип исходника |
| `content` | string | Исходный HTML, JSON ручного ввода или JSON/CSV-файл |
| `contentHash` | string | SHA-256 UTF-8 представления `content`, 64 строчных hex-символа |

### Enum

| Тип | Значения |
| --- | --- |
| PublicationStatus | `draft`, `published`, `cancelled` |
| SourceEventStatus | `unknown`, `scheduled`, `cancelled`, `postponed`, `rescheduled`, `soldOut` |
| CollectionRunStatus | `queued`, `running`, `completed`, `completedWithErrors`, `failed`, `cancelled` |
| EventCategory | `other`, `concert`, `theatre`, `exhibition`, `festival`, `cinema`, `excursion`, `workshop`, `lecture` |
| EventSourceType | `page`, `import`, `manual` |

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
| `input_requires_review` | Данные внесены вручную или из файла и требуют модерации |

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
| `WorkerEnabled` | `true` | boolean; false запускает только API, без планирования и обработки очереди |
| `ScheduleIntervalHours` | `6` | `1..168` часов |
| `MaxPagesPerRun` | `200` | `1..500` карточек |
| `MaxListingPages` | `5` | `1..30` страниц списка; ГКЗ и театр оперы обходят одну |
| `RequestTimeoutSeconds` | `20` | `1..60` секунд на HTTP-запрос |
| `RequestDelayMilliseconds` | `500` | `250..10000` мс перед запросом страницы |
| `MaxConsecutiveFailures` | `3` | `1..10` последовательных ошибок загрузки |
| `FreshnessHours` | `24` | `1..168` часов для `/events/published` |
| `Sources` | см. ниже | 1–20 элементов, ID уникальны без учёта регистра |
| `Ingestion:MaxFileBytes` | `5242880` | `1024..20971520` байт, общий предел JSON/CSV-файла |
| `Ingestion:MaxRows` | `1000` | `1..5000` записей на файл |

В окружении параметры импорта задаются как `Parser__Ingestion__MaxFileBytes`
и `Parser__Ingestion__MaxRows`. Ручной POST имеет отдельный фиксированный предел 128 KiB.

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
3. Межисточниковый `dedupKey` — SHA-256 нормализованных названия, города,
   идентификатора площадки, даты и времени начала, соединённых `|`.
   Если площадка ещё не определена, используются её название и адрес;
   без адреса совпадение ограничено идентичностью источника и сеансом.
   Нормализация: Unicode NFKC, нижний регистр, `ё → е`, свёртка пунктуации и пробелов.
   Без даты, времени, города или площадки используется `identityKey`.
   Разные сеансы сохраняются отдельно; нечёткого сравнения названий нет.
4. `sourceHash` / внутренний `dataHash` — SHA-256 сериализованного нормализованного
   кандидата до сопоставления со справочниками, без `rawDocumentId`; определяет
   изменение данных источника. Добавление алиаса не меняет этот хеш. `contentHash`
   относится к исходному тексту. Дубли проверяются при сохранении;
   API списка читает готовые записи.
5. Мероприятие, происхождение и аудит сохраняются одной Serializable-транзакцией.
   Для конфликтов сериализации, deadlock и уникальности — до 6 попыток всей
   транзакции: экспоненциальные паузы 50–800 мс плюс случайные 25–75 мс.
   Уникальные индексы защищают от конкурентных дублей.
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
| `parser_cities`, `parser_city_aliases` | Населённые пункты и варианты названий | `0005_parser_references` |
| `parser_venues`, `parser_venue_aliases` | Площадки с адресами и алиасами внутри города | `0005_parser_references` |

`0004_parser_ingestion` добавляет тип происхождения в события, связи и исходники,
MIME-тип исходника и разрешает внутренний URN с `http_status=NULL` для ручного
ввода / импорта. Существующие строки сохраняются с типом Page. Откат этой ревизии
блокируется, если уже есть ручные / импортные данные: они не удаляются автоматически.

Перед запуском новой версии выполни миграции:

```powershell
docker compose run --rm --build migrate
```

Если существующие контейнеры созданы под именем `bashkiria-db`, используй
`docker compose -p bashkiria-db run --rm --build migrate`.

Сервис не вызывает `EnsureCreated` / EF migrations. Схему обновляет только
`backend/db`. Автоматической очистки исходников и истории нет. Документы,
на которые ссылаются мероприятия, защищены внешними ключами.
API удаления мероприятий и исходников отсутствует.

## Справочники и нормализация площадок

Миграция `0005_parser_references` добавляет nullable city_id/venue_id в parser_events,
внешние ключи и четыре таблицы справочников. Начальное наполнение: 21 город
Башкортостана и три площадки Уфы — ГКЗ «Башкортостан», филармония имени Хусаина
Ахметова и театр оперы и балета. Остальные населённые пункты и площадки добавляются
через API. Категории соответствуют существующему EventCategory.

Все пути ниже начинаются с `/api/v1/parser/reference`, требуют общий API-ключ
и входят в общий лимит запросов. Тела: application/json.

### Чтение справочников

| Метод и путь | Query | Ответ 200 |
| --- | --- | --- |
| `GET /cities` | q — поиск по названию/алиасу; page=1, pageSize=30 | PageResponse&lt;CityResponse&gt; |
| `GET /cities/{id}` | — | CityResponse; 404 при неизвестном ID |
| `GET /venues` | cityId — фильтр города; q — поиск по названию/алиасу; page=1, pageSize=30 | PageResponse&lt;VenueResponse&gt; |
| `GET /venues/{id}` | — | VenueResponse; 404 при неизвестном ID |
| `GET /categories` | — | Массив `{id: EventCategory, name: string}` с русскими названиями |

Пагинация: page 1..100000, pageSize 1..100; неправильная — 400. Сортировка по имени,
затем ID. Поиск q — по вхождению нормализованной строки, включая алиасы.
Сопоставление событий точное, без нечёткого сравнения.

`CityResponse`: `{id, name, aliases: [{id, name, address: null}]}`.
`VenueResponse`: `{id, cityId, name, address, aliases: [{id, name, address}]}`.
Пустой адрес алиаса означает вариант названия; непустой — проверенную пару
«название и вариант адреса».

### Создание записей и алиасов

| Метод и путь | JSON-тело | Правила |
| --- | --- | --- |
| `POST /cities` | `{ "name": "Стерлибашево" }` | Название до 200 символов; нормализованное имя/алиас уникально |
| `POST /venues` | `{ "cityId": "UUID", "name": "Дом культуры", "address": "ул. Ленина, 1" }` | Существующий город; имя до 500, обязательный адрес до 2000 символов |
| `POST /cities/{id}/aliases` | `{ "name": "Вариант названия" }` | Существующий город; имя до 200 символов |
| `POST /venues/{id}/aliases` | `{ "name": "ДК", "address": "Ленина, д. 1" }` | Существующая площадка; имя до 500, опциональный адрес до 2000 символов |

Ответы: 201 с `{id: UUID}` и Location созданной/родительской записи;
400 — невалидный текст, 404 — неизвестный родитель, 409 — конфликт уникальности,
503 — недоступная БД. Пробелы сворачиваются; пустой текст, одна пунктуация
и нулевые символы запрещены. Алиас города не может указывать на два города.
Пара «город, имя алиаса площадки, адрес алиаса» уникальна. Одинаковые названия
площадок разрешены в разных городах и у филиалов с разными адресами.
При создании площадки регистрируется каноническая пара «название, адрес».
API переименования и удаления справочников отсутствует.

### Правила сопоставления

1. Явный venueId имеет приоритет и задаёт канонические город, название и адрес.
   Если cityId также передан, он обязан совпадать с городом площадки.
2. Без ID населённый пункт ищется по точному нормализованному алиасу;
   префиксы г., г, город, регистр и различие е/ё не влияют на сравнение.
3. Площадка ищется только в найденном городе: сначала алиас с точным адресом,
   затем варианты названия и канонические адреса кандидатов. улица/ул. и
   проспект/просп./пр-кт приводятся к общей форме.
4. Единственное совпадение заполняет cityId/venueId и канонические строки.
   Отсутствующий адрес можно заполнить из однозначно найденной площадки.
   Несовпадающий адрес сохраняется: нужен адресный алиас или явный ID.
5. unknown_city, unknown_venue, ambiguous_venue, venue_address_conflict оставляют
   карточку в draft. Исходный документ не меняется; площадки из неизвестного
   текста автоматически не создаются. Правила общие для источников, импорта,
   ручного ввода и модерации.

### POST /api/v1/parser/reference/normalize

Повторное сопоставление сохранённых событий после миграции или добавления алиасов.
Тело отсутствует. Query: page=1, pageSize=30; ограничения как выше.
Сортировка: createdAt, затем id. Для полного прохода обработай страницы от 1 до
ceil(total/pageSize), по возможности при остановленном сборе/импорте.
Для служебного запуска только API используй `--Parser:WorkerEnabled=false`:
одного SchedulerEnabled=false недостаточно, поскольку он отключает только
создание заданий по расписанию, а уже поставленные задания продолжают выполняться.

200: `PageResponse<{eventId: UUID, outcome: "updated"|"unchanged", issues: string[]}>`.
Обновляются связи, канонические город/площадка/адрес, dedupKey, issues/confidence;
при проблемах published снимается в draft. Автопубликации нет. Изменения увеличивают
version и записываются в аудит references_normalized. Название, ручные правки,
источники, исходники, sourceHash и verifiedAt сохраняются. Повторный проход без
изменений возвращает unchanged и не создаёт аудит.

Каждая карточка — отдельная Serializable-транзакция с максимум 6 попытками при
конфликте. При сбое ранее обработанные карточки остаются сохранены; страницу можно
повторить. Ошибки: 400 — пагинация, 409 — конкурентное изменение, 503 — БД.
Неизменившийся повторный сбор тоже обновляет связи со справочниками,
сохраняя ручные исправления.

Если исторические карточки после нормализации получают один dedupKey, добавляется
reference_duplicate; карточка остаётся в draft со старым ключом. Такие дубли
не сливаются автоматически: ID, источники и аудит сохраняются для разбора.
Новые события с разными алиасами известной площадки объединяются обычной
межисточниковой дедупликацией при сохранении.

Проверка PowerShell с уже настроенными `$base` и `$headers`:

```powershell
Invoke-RestMethod "$base/api/v1/parser/reference/cities?q=Уфа" -Headers $headers |
    ConvertTo-Json -Depth 8
Invoke-RestMethod "$base/api/v1/parser/reference/venues?q=Башкортостан" -Headers $headers |
    ConvertTo-Json -Depth 8
Invoke-RestMethod "$base/api/v1/parser/reference/normalize?page=1&pageSize=100" `
    -Method Post -Headers $headers | ConvertTo-Json -Depth 8
```

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
| POST | `/api/v1/parser/events` | Вручную создать карточку с Idempotency-Key; новые данные требуют модерации |
| POST | `/api/v1/parser/imports/json/{datasetId}` | Загрузить JSON-массив и получить построчный отчёт импорта |
| POST | `/api/v1/parser/imports/csv/{datasetId}` | Загрузить UTF-8 CSV и получить построчный отчёт импорта |
| GET | `/api/v1/parser/events/published` | Получить опубликованные, свежие и ещё актуальные мероприятия |
| GET | `/api/v1/parser/events/{id}` | Получить карточку мероприятия, её версию и источники |
| PATCH | `/api/v1/parser/events/{id}/review` | Исправить поля и изменить статус публикации с проверкой версии |
| GET | `/api/v1/parser/events/{id}/audit` | Получить последние 100 изменений мероприятия |
| GET | `/api/v1/parser/raw-documents/{id}` | Получить исходный HTML, JSON ручного ввода или файл импорта |
| POST | `/api/v1/parser/raw-documents/{id}/reparse` | Повторно разобрать исходник и сохранить мероприятия без новой загрузки |
| GET | `/api/v1/parser/stats` | Получить счётчики исходников, мероприятий и заданий |
| GET | `/api/v1/parser/reference/cities` | Получить населённые пункты с поиском, алиасами и пагинацией |
| GET | `/api/v1/parser/reference/cities/{id}` | Получить населённый пункт и его алиасы |
| POST | `/api/v1/parser/reference/cities` | Добавить населённый пункт |
| POST | `/api/v1/parser/reference/cities/{id}/aliases` | Добавить вариант названия населённого пункта |
| GET | `/api/v1/parser/reference/venues` | Получить площадки с фильтром города, поиском и пагинацией |
| GET | `/api/v1/parser/reference/venues/{id}` | Получить площадку с адресом и алиасами |
| POST | `/api/v1/parser/reference/venues` | Создать каноническую площадку |
| POST | `/api/v1/parser/reference/venues/{id}/aliases` | Добавить вариант названия или адреса площадки |
| GET | `/api/v1/parser/reference/categories` | Получить коды и русские названия категорий |
| POST | `/api/v1/parser/reference/normalize` | Нормализовать страницу существующих карточек |
| POST | `/parser/preview` | Разобрать переданный HTML карточки ГКЗ без записи в БД; только Development |
| GET | `/parser/preview/gkz` | Загрузить и разобрать фиксированную карточку ГКЗ, сохранив исходник; только Development |
| GET | `/parser/preview/gkz/links` | Загрузить афишу ГКЗ и получить ссылки на карточки, сохранив исходник; только Development |
| GET | `/parser/preview/gkz/collect` | Синхронно собрать до 3 карточек ГКЗ с записью в БД; только Development |
