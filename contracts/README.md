# HTTP-контракты сервисов

Контракты хранятся в OpenAPI 3.1: их можно использовать из любого языка для
генерации HTTP-клиентов, моделей запросов и ответов, документации и проверок.

| Сервис-владелец | Контракт | Состояние |
| --- | --- | --- |
| Parser (.NET) | [parser/openapi.json](parser/openapi.json) | Реализованный HTTP API `/api/v1/parser/*` |
| AI (Python) | Пока отсутствует | Запускается через CLI; `ChatService` — внутренний Python-интерфейс |
| Searcher | Пока отсутствует | Реализация HTTP API ещё не добавлена |

Сейчас в коде нет HTTP-вызовов между этими сервисами. Здесь выделена существующая
граница Parser API, которую смогут использовать потребители. Контракты AI и
Searcher следует добавлять вместе с их HTTP API.

## Что входит в контракт Parser

| Операции | Модели данных |
| --- | --- |
| `GET /api/v1/parser/events/published` | `PageResponseOfEventResponse`, `EventResponse`, `EventSourceResponse` — опубликованные мероприятия для потребителей |
| `GET /api/v1/parser/events`, `GET /api/v1/parser/events/{id}` | Те же модели, включая черновики, версию, статусы и сведения об источниках |
| `PATCH /api/v1/parser/events/{id}/review` | `ReviewEventRequest` — модерация с проверкой версии |
| `GET /api/v1/parser/events/{id}/audit` | `EventAuditResponse` — история изменений |
| `GET /api/v1/parser/sources` | `SourceResponse` — настройки доступных источников |
| `POST /api/v1/parser/runs/{id}`, `GET /api/v1/parser/runs`, `GET /api/v1/parser/runs/{id}`, `DELETE /api/v1/parser/runs/{id}` | `CollectionRunResponse`, `CollectionResult`, `EventCandidate`, `CollectionError` — управление сбором и отчёты |
| `GET /api/v1/parser/raw-documents/{id}`, `POST /api/v1/parser/raw-documents/{id}/reparse` | `RawDocumentResponse`, `ReparseResponse`, `EventSaveResponse` — исходники и повторный разбор |
| `GET /api/v1/parser/stats` | `ParserStatsResponse` — счётчики |
| Ошибки | `MessageResponse`, `ParserProblemDetails` |

Точные имена схем и их поля находятся в `components.schemas`. Описание параметров,
HTTP-статусов и заголовков находится в `paths`. Включены `X-Parser-Key`, пагинация,
строковые enum и nullable-поля. Preview, health и сама выдача OpenAPI не входят
в межсервисный контракт.

У `/runs/{id}` значение `id` в POST — строковый ID источника (`gkz`, `culture`),
а в GET/DELETE — UUID задания. Имя placeholder едино для OpenAPI; фактические
URL существующих запросов сохранены.

Для обычной выдачи актуальных мероприятий потребителю следует использовать
`GET /api/v1/parser/events/published`. Остальные методы предназначены также для
управления и диагностики. Чтение `/events/{id}` не проверяет публикацию и свежесть.
Все эти операции используют один ключ доступа; отдельных прав чтения/модерации нет.

## Источник истины и обновление

Используется code-first: модели HTTP и метаданные маршрутов принадлежат парсеру,
а `parser/openapi.json` — сохраняемый в Git результат генерации. Вручную файл
контракта не редактируется.

- [Модели запросов и ответов](../backend/parser/parser/Contracts).
- [Маршруты и типы ответов](../backend/parser/parser/Api/ParserEndpoints.cs).
- [Общие правила OpenAPI, авторизация и ошибки](../backend/parser/parser/Api/ParserOpenApi.cs).

Из корня репозитория с .NET SDK 10:

```powershell
# Обновить контракт после изменения API.
./contracts/Export-ParserContract.ps1

# Проверить соответствие коду без изменения сохранённого контракта; подходит для CI.
./contracts/Export-ParserContract.ps1 -Check
```

Можно указать путь к SDK через `-Dotnet C:/path/to/dotnet.exe`. Экспорт выполняет
build-time генерацию OpenAPI без запуска HTTP-сервера, worker, подключения к БД
и загрузки внешних сайтов. Ключи API и локальные `.env` для экспорта не нужны.
Промежуточные файлы остаются в игнорируемом каталоге `artifacts/contracts`.

Если Windows блокирует запуск `.ps1`, можно выполнить команду в отдельном процессе
без изменения системной политики:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File contracts/Export-ParserContract.ps1
```

На работающем сервисе этот же контракт доступен через `GET /openapi/v1.json`.
Адрес сервиса потребитель настраивает отдельно: адрес конкретного окружения
в контракт не записывается.

## Использование и совместимость

Потребитель берёт контракт определённой ревизии Git и генерирует клиент/DTO
для своего языка. Например, в OpenAPI Generator используются генераторы `python`,
`go`, `java`, `typescript-fetch`. Внутренние модели парсера и его EF-сущности
не нужно подключать в другие сервисы.

- HTTP JSON использует `camelCase`, enum передаются строками `camelCase`.
- Числа описаны как JSON numbers. Серверная терпимость к числам в строках
  не является частью межсервисного контракта.
- Локальные даты и время мероприятия относятся к `Asia/Yekaterinburg`.
  Временные метки передаются как ISO 8601 с UTC-смещением.
- Неизвестные значения передаются как `null`. Неизвестная цена не означает
  бесплатный вход; неизвестное время не заменяется полуночью.
- В запросе модерации отсутствующее поле и `null` сохраняют прежнее значение.
- Потребитель должен терпимо относиться к дополнительным полям ответа.
- Смена типа или смысла поля, удаление поля, новое обязательное поле запроса
  и новые значения enum требуют проверки совместимости с потребителями.
- При несовместимом изменении добавляется новый контракт и версия API
  с периодом совместной поддержки прежней версии.

`-Check` обнаруживает расхождение файла с реализацией, но не заменяет проверку
обратной совместимости: изменения контракта нужно просматривать в diff.
