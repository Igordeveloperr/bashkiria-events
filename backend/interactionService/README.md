# Interaction API

Микросервис подписок, уведомлений и обратной связи. ASP.NET Core / .NET 10,
асинхронный EF Core 10 + PostgreSQL. Модели находятся в `interactionService/Models`.
Миграции выполняет только `backend/db` через Alembic. Сервис не вызывает модель,
не меняет каталог парсера и не хранит токен Telegram-бота.

## Запуск

Из корня репозитория:

```powershell
docker compose up -d postgres
docker compose run --rm --build migrate
Copy-Item backend/interactionService/.env.example backend/interactionService/.env
dotnet run --project backend/interactionService/interactionService --launch-profile http
```

Перед запуском укажи действительные реквизиты БД в `.env` и замени три ключа
разными случайными значениями. `.env` читается только в Development из
`backend/interactionService/.env`, существующие переменные окружения не затираются.
Профиль `http`: **http://localhost:5266**. Для запуска только API:
`--Interaction:WorkerEnabled=false`. OpenAPI 3.1: `/openapi/v1.json`, страница `/docs`.
Живость: `/health`; готовность БД и используемых таблиц: `/health/ready`.

Миграция `0006_interaction_service` объединяет ветки `0005_parser_references`
и `0004_event_search`, затем создаёт шесть таблиц сервиса. На рабочей базе
существующие события и источники сохраняются. Откат 0006 удаляет данные этого
сервиса, но сохраняет таблицы парсера и поиска.

Для Docker заполни в корневом `.env`: INTERACTION_API_KEY,
INTERACTION_ADMIN_API_KEY, INTERACTION_DELIVERY_API_KEY. Затем:

```powershell
docker compose --profile interaction up -d --build interaction
```

Внешний порт задаёт INTERACTION_HTTP_PORT (5266), worker — INTERACTION_WORKER_ENABLED
(true). Внутри сети Compose адрес `http://interaction:8080`. Если существующий
стек запущен с именем bashkiria-db, используй тот же проект:
`docker compose -p bashkiria-db ...`. Это сохраняет существующие сеть и volume.

## Границы доступа

Все рабочие маршруты начинаются с `/api/v1/interaction`.

| Группа | Заголовки | Кто вызывает |
| --- | --- | --- |
| `/me/*` | X-Interaction-Key, X-User-Id | Доверенный API Gateway или backend бота |
| `/admin/*` | X-Interaction-Admin-Key | Backend админки |
| `/deliveries/*` | X-Interaction-Delivery-Key | Worker Telegram-бота |

X-User-Id — стабильный непрозрачный идентификатор, 1..128 символов ASCII:
буквы, цифры, `_`, `.`, `:`, `-`. Например `telegram:12345` или ID учётной записи.
Шлюз обязан аутентифицировать пользователя и сам выставлять X-User-Id, удаляя
одноимённый заголовок клиента. Ключи передаются только между backend-компонентами,
не браузеру и не модели. Собственной регистрации, JWT и связывания Telegram/web
профилей сервис не реализует: это обязанности шлюза/бота.

В Production обязательны разные ключи не короче 32 символов. В Development
проверка конкретного ключа отключается, если он не задан; X-User-Id остаётся
обязательным. User-ключ не открывает admin/delivery, delivery-ключ не даёт читать
обращения. Служебные health/docs/OpenAPI доступны без ключа.

JSON: camelCase, Content-Type application/json; неизвестные поля отклоняются.
Enum — строки camelCase. Даты yyyy-MM-dd, временные метки ISO 8601 UTC.
Максимальное тело — 64 КиБ. Лимит API — 60 запросов/минуту на пользователя;
для admin и delivery — по отдельному общему лимиту на экземпляр. Долговременный
лимит обращений хранится в БД и действует между перезапусками/репликами.
Логи не содержат идентификаторов пользователей, текстов обращений и SQL-параметров.

## Ошибки

Все ошибки приложения — application/problem+json:

```json
{
  "type": "urn:bashkiria-events:error:validation_error",
  "title": "Проверьте поля запроса.",
  "status": 400,
  "instance": "/api/v1/interaction/me/feedback",
  "code": "validation_error",
  "traceId": "request-trace",
  "errors": [{"field":"subject","code":"invalid_value","message":"Укажите текст до 200 символов."}]
}
```

errors — всегда массив, instance — путь без query. Внутренние исключения в ответ
не попадают. Коды: 400 validation_error/consent_required, 401 unauthorized,
404 not_found, 405 method_not_allowed, 409 conflict/subscription_limit,
413 payload_too_large, 415 unsupported_media_type, 429 rate_limit_exceeded/
feedback_rate_limit, 500 internal_error, 503 service_unavailable.
429 содержит Retry-After в секундах. Чужая запись возвращает 404; отписка
идемпотентна и возвращает 204 даже для отсутствующего ID.

## Подписки

### POST /me/subscriptions; PUT /me/subscriptions/{id}

| Поле | Тип | Правило |
| --- | --- | --- |
| cityId | UUID | Обязательный существующий ID из parser_cities |
| category | EventCategory / null | null — все категории |
| dateFrom, dateTo | date / null | Необязательный период; dateTo не раньше начала и сегодняшней даты; верхняя граница не далее года |
| channel | NotificationChannel | web по умолчанию; web или telegram |
| consentConfirmed | boolean | Обязательно true после подтверждения пользователем |
| expectedVersion | int64 / null | Обязательно для PUT: текущая версия |

EventCategory: other, concert, theatre, exhibition, festival, cinema, excursion,
workshop, lecture. Канал существующей подписки не изменяется: нужно отписаться
и создать другую. Фильтры объединяются AND. Точное совпадение фильтров и канала
при повторном POST возвращает ту же запись (200), новый POST — 201 + Location.
Максимум 20 подписок на пользователя, суммарно для двух каналов.
PUT возвращает 200, устаревшая версия или совпадение с другой подпиской — 409.
Подтверждение запрашивает интерфейс: агент должен вызвать инструмент записи
только после явного согласия. Сам текст диалога не считается подтверждением API.

Ответ Subscription:
`{id, cityId, category, dateFrom, dateTo, channel, createdAt, updatedAt, version}`.
GET /me/subscriptions/{id} доступен только владельцу. GET /me/subscriptions —
список с пагинацией. DELETE /me/subscriptions/{id} удаляет одну подписку;
DELETE /me/subscriptions — одна операция для отписки от всех каналов/фильтров.

## Уведомления

Worker раз в 60 секунд ищет опубликованные, свежие, актуальные события подписок
на ближайшие 30 дней. Используются cityId, category, период, dateStart/dateEnd,
sourceStatus и verifiedAt. В начальную подборку входят уже существующие подходящие
события. Неподтверждённые площадки, черновики, отменённые/перенесённые, устаревшие
и завершённые карточки не включаются. Каталог читается напрямую как контракт БД,
только через AsNoTracking; записи в его таблицы запрещены DbContext.

На пользователя и канал — до одной подборки за календарный день, до 5 событий.
Окно формирования: 09:00–21:00 Asia/Yekaterinburg (час начала настраивается).
Пустые подборки не создаются. Пересекающиеся подписки не дублируют события;
уже включённое событие повторно не анонсируется этому получателю, пока сохраняется
хотя бы одна соответствующая подписка с его историей. Это анонсы, а не отдельные
уведомления об отменах, переносах и напоминания: последние — последующее расширение.

### GET /me/notifications; PATCH /me/notifications/{id}/read

Query списка: page=1, pageSize=30, unreadOnly=false.
Сортировка createdAt DESC, id ASC. Ответ PageResponse&lt;NotificationResponse&gt;.

NotificationResponse: `{id, channel, status, createdAt, deliveredAt, readAt, events}`.
events — массив NotificationCard:
`{id, title, category, startDate, startTime, city, venue, address, priceMin, priceMax, ageLimit}`.
Неизвестные значения — null. Источник полной карточки — существующий API каталога
по id. Перед чтением списка и выдачей боту события проверяются по текущему каталогу;
поэтому events может быть пустым у ранее созданного уведомления.

Для web запись сразу доступна в inbox и имеет статус delivered; это означает
доступность на сайте, а не прочтение. PATCH read без тела ставит readAt, возвращает
204; повторный вызов не меняет дату. Бот также может показать пользователю его
личный список с user-ключом, но для общей очереди использует delivery-ключ.

### POST /deliveries/claim?limit=10

Тело отсутствует; limit 1..20. Ответ 200 — массив:
`{notificationId, userId, leaseToken, leaseExpiresAt, events}`.
Пустой массив — готовых сообщений нет. Возвращаются только Telegram-уведомления.
Аренда до 2 минут, но не позднее 21:00 по региональному времени;
конкурентные workers не получают одну действующую аренду.
Ночью claim возвращает пустой массив: повторные попытки тоже соблюдают тихие часы.
После истечения сообщение выдаётся с новым токеном. До выдачи повторно проверяются
актуальность и статусы событий; полностью неактуальные подборки получают suppressed.

Бот связывает userId со своим проверенным chat ID. Он отправляет **одно сообщение
на подборку** и подтверждает результат. Сервис не принимает произвольный chat ID
и не отправляет Telegram-запросы самостоятельно.

### POST /deliveries/{id}/ack

```json
{"leaseToken":"UUID","success":true}
```

Для ошибки: success=false, permanentFailure=false, errorCode="transport_unavailable".
errorCode — 1..64 символа [a-z0-9_], без текста исключения/персональных данных.
204 — результат принят; повторный успешный ACK с тем же токеном также 204.
Неверная/истёкшая аренда — 409, удалённое при отписке сообщение — 404.
После временной ошибки повтор через 30, 60, 120, 240 секунд; максимум 5 выдач.
Постоянная ошибка или исчерпание попыток дают failed. Статусы: pending, leased,
delivered, suppressed, failed. Бот не должен отправлять сообщение после истечения
аренды и должен учитывать notificationId при повторной обработке.

Гарантия доставки — at least once: сбой после Telegram send, но до ACK может
дать повтор. Exactly once внешней отправки не обещается. Отписка удаляет ожидающие
сообщения; уже забранное ботом сообщение может находиться в процессе отправки.

## Обратная связь

### POST /me/feedback

Обязателен заголовок Idempotency-Key: UUID. Ответ 201 + Location подтверждает
приём обращения; повтор с тем же ключом и содержимым — 200 с тем же ID.
Другой текст под тем же ключом — 409; модерация при повторе не сбрасывается.

| Поле | Тип | Правило |
| --- | --- | --- |
| kind | FeedbackKind | question, correction, organizerApplication |
| subject | string | Обязательно, 1..200 символов после trim |
| message | string | Обязательно, 1..5000 символов после trim |
| eventId | UUID / null | Существующее событие; обязательно для correction |
| website | string / null | Honeypot: отсутствует или пустая строка |

До 5 новых обращений/час/пользователь в БД; повтор идемпотентного запроса квоту
не расходует. Дополнительно действует минутный лимит API. Телефон, email,
домашний адрес и произвольные поля не принимаются. Текст обращения не нужно
заполнять персональными данными. Ответ Feedback:
`{id, kind, eventId, subject, message, status, createdAt, updatedAt, version}`.
GET /me/feedback — свои обращения с пагинацией; GET /me/feedback/{id} — своё
обращение. Начальный status=new. Для организатора kind=organizerApplication:
это заявка модератору, публикация события автоматически не выполняется.

### Модерация

GET /admin/feedback: page/pageSize, необязательные status и kind. Сортировка
createdAt ASC, id ASC. Статусы: new, inProgress, resolved, rejected.
PATCH /admin/feedback/{id}/review:

```json
{"expectedVersion":1,"status":"inProgress","note":"Передано ответственному"}
```

status — inProgress, resolved или rejected; note обязателен, до 2000 символов.
204 увеличивает version и сохраняет историю; старая версия — 409.
Опциональный X-Actor-Id задаёт непрозрачный ID модератора (1..128 символов,
формат как X-User-Id); по умолчанию admin-api. Его выставляет доверенная админка.
GET /admin/feedback/{id}/history — последние 100 записей:
`{id, previousStatus, status, note, actor, createdAt}`; неизвестное обращение — 404.

## Пагинация, обслуживание и хранение

Списки: `{total, page, pageSize, items}`; page 1..100000, pageSize 1..100,
по умолчанию 1/30. total считается до пагинации; за пределами списка items=[].

POST /admin/notifications/generate без тела: `{created: int}` — ручной цикл,
который также соблюдает время суток, суточный интервал, размер и дедупликацию.
POST /admin/retention/cleanup: `{deleted: int}` — очистка; число основных удалённых
записей, без каскадных дочерних строк. Worker выполняет очистку перед каждым циклом.
GET /admin/metrics: `{requests, errors, meanDurationMs}` с запуска процесса;
ошибка — HTTP >=400. .NET Meter Bashkiria.Events.Interaction также предоставляет
interaction.http.requests и гистограмму interaction.http.duration в миллисекундах.

Подписка хранит только непрозрачный ID, канал и фильтры. При полной отписке
recipient, подписки, уведомления и история дедупликации удаляются каскадно.
При удалении одной подписки очищаются ожидающие подборки её канала; уже доставленные
записи остаются до retention, пока есть другие подписки. Истёкшие подписки удаляет
worker. Уведомления хранятся 30 дней, обращения и история модерации — 90 дней
от создания обращения. Ключи идемпотентности обращений живут столько же.
Идентификаторы уже анонсированных событий остаются до удаления подписки, чтобы
retention уведомлений не вызывал повторных анонсов. Эти сроки — настройки проекта,
которые надо согласовать с заказчиком; автоматическая очистка действует только
при работающем worker либо ручном вызове cleanup.

Каждая запись бизнес-операции — отдельная Serializable-транзакция. При конфликте
сериализации/уникальности/deadlock — максимум 6 попыток с экспоненциальной паузой
50..800 мс + jitter. Version защищает изменения от устаревших запросов.

## Настройки Interaction

| Параметр | По умолчанию | Ограничения |
| --- | --- | --- |
| ApiKey, AdminApiKey, DeliveryApiKey | пусто | В Production три разных значения >=32 символов |
| WorkerEnabled | true | false — только HTTP API |
| PollSeconds | 60 | 5..3600 |
| DigestSize | 5 | 1..20 |
| DigestHour | 9 | 9..20, Asia/Yekaterinburg; конец окна 21:00 |
| HorizonDays | 30 | 1..365 |
| FreshnessHours | 24 | 1..168 |
| MaxSubscriptions | 20 | 1..50 |
| FeedbackPerHour | 5 | 1..50 |
| NotificationRetentionDays | 30 | 1..365 |
| FeedbackRetentionDays | 90 | 1..365 |

Переменные окружения используют двойное подчёркивание: Interaction__DigestSize=5.
Подключение: ConnectionStrings__InteractionDatabase, либо PGHOST, PGPORT,
PGDATABASE, PGUSER, PGPASSWORD. При втором варианте PGPORT по умолчанию 5432,
connect timeout 5 с, command timeout 15 с.

## Пример PowerShell

```powershell
$base = 'http://localhost:5266/api/v1/interaction'
$headers = @{ 'X-Interaction-Key'='КЛЮЧ_ШЛЮЗА'; 'X-User-Id'='telegram:12345' }
# cityId возьми из GET /api/v1/parser/reference/cities у парсера.
$body = @{cityId='UUID_ГОРОДА';category='concert';channel='telegram';consentConfirmed=$true} | ConvertTo-Json
Invoke-RestMethod "$base/me/subscriptions" -Method Post -Headers $headers `
    -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body))
Invoke-RestMethod "$base/me/subscriptions" -Headers $headers | ConvertTo-Json -Depth 8
# Отписаться от всего:
Invoke-RestMethod "$base/me/subscriptions" -Method Delete -Headers $headers
```

## Проверки

`dotnet test backend/interactionService/interactionService.slnx` выполняет модульные
тесты расписания; без INTERACTION_TEST_CONNECTION_STRING PostgreSQL-тесты пропускаются.
Полный прогон с миграцией отдельной случайной схемы и удалением только этой схемы:

```powershell
$env:COMPOSE_PROJECT_NAME='bashkiria-db' # только если существующий стек использует это имя
./backend/interactionService/scripts/Test-Interaction.ps1
```

Тесты используют EF-модели парсера только для подготовки фикстур каталога
в тестовой схеме. Рабочие карточки и настоящие Telegram-пользователи не затрагиваются.

## Сводная таблица endpoint’ов

| Метод | Путь | Назначение |
| --- | --- | --- |
| GET | `/health` | Живость процесса |
| GET | `/health/ready` | Готовность БД |
| GET | `/docs` | Страница API |
| GET | `/openapi/v1.json` | OpenAPI 3.1 |
| GET | `/api/v1/interaction/me/subscriptions` | Свои подписки с пагинацией |
| GET | `/api/v1/interaction/me/subscriptions/{id}` | Своя подписка |
| POST | `/api/v1/interaction/me/subscriptions` | Создать подтверждённую подписку |
| PUT | `/api/v1/interaction/me/subscriptions/{id}` | Изменить фильтры с проверкой версии |
| DELETE | `/api/v1/interaction/me/subscriptions/{id}` | Удалить подписку |
| DELETE | `/api/v1/interaction/me/subscriptions` | Отписаться от всего |
| GET | `/api/v1/interaction/me/notifications` | Свои уведомления |
| PATCH | `/api/v1/interaction/me/notifications/{id}/read` | Отметить прочтение |
| POST | `/api/v1/interaction/me/feedback` | Подать обращение с Idempotency-Key |
| GET | `/api/v1/interaction/me/feedback` | Свои обращения |
| GET | `/api/v1/interaction/me/feedback/{id}` | Статус своего обращения |
| GET | `/api/v1/interaction/admin/feedback` | Очередь обращений модератора |
| PATCH | `/api/v1/interaction/admin/feedback/{id}/review` | Обработать обращение |
| GET | `/api/v1/interaction/admin/feedback/{id}/history` | История обработки |
| POST | `/api/v1/interaction/admin/notifications/generate` | Сформировать очередные подборки |
| POST | `/api/v1/interaction/admin/retention/cleanup` | Применить сроки хранения |
| GET | `/api/v1/interaction/admin/metrics` | Запросы, ошибки и средняя задержка |
| POST | `/api/v1/interaction/deliveries/claim` | Зарезервировать сообщения для бота |
| POST | `/api/v1/interaction/deliveries/{id}/ack` | Подтвердить или повторить доставку |
