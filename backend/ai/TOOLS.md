# Прототипы инструментов ИИ

В [tools.py](tools.py) находятся шесть функций с `@tool` и список `TOOLS`.
Схемы аргументов строятся из сигнатур и docstring автоматически; модели данных и
результатов вынесены в [models.py](models.py). Контракты доступны через `GET /api/tools`.
Каждый контракт содержит имя, назначение, JSON Schema аргументов (`parameters`)
и результата (`returns`), флаг `implemented: false`.
Сигнатуры возвращают модели Pydantic, тела функций выбрасывают `NotImplementedError`.
Инструменты пока не подключены к модели и графу; операций с БД и внешними API нет.

| Инструмент | Назначение | Аргументы | Результат |
| --- | --- | --- | --- |
| `search_events` | Поиск по фильтрам | Необязательные `city`, `category`, `period`, `price`, `age`, `text`; `limit=10`, `offset=0` | `events: EventSummary[]`, `total: int`, `limit: int`, `offset: int` |
| `get_event` | Полная карточка | Обязательный `event_id: UUID` | `event: EventCard` или `null`, если не найдено |
| `find_similar` | Похожие события | Обязательный `event_id: UUID`; необязательные `city`, `period`; `limit=10` | `events: {event: EventSummary, score: number}[]`, по убыванию сходства; исходное событие исключается |
| `get_venue_schedule` | Расписание площадки | Обязательные `venue: string`, `period`; необязательный `city`; `limit=10`, `offset=0` | `venue`, `city`, `period`, `events: EventSummary[]`, `total`, `limit`, `offset`; события по времени начала |
| `subscribe` | Подписка на фильтр | Обязательные `city: string`, `period`; необязательный `category` | `subscription_id: UUID`, `status: "active"`, `city`, `category`, `period`, `created_at` |
| `save_feedback` | Обращение к организатору | Обязательные `event_id: UUID`, `text: string` | `feedback_id: UUID`, `event_id: UUID`, `status: "submitted"`, `created_at` |

`user_id` и `session_id` приходят в HTTP API с фронтенда. При реализации инструментов
их нужно брать из контекста запроса; в аргументах для модели этих полей нет.

## Общие типы

| Тип | Формат и смысл |
| --- | --- |
| `period` | `{ "start": "2026-11-14", "end": "2026-11-30" }`; даты включительно, по местному времени события, `end >= start` |
| `price` | `{ "min": 0, "max": 1500 }`; границы в целых рублях, любая может отсутствовать или быть `null`; `max >= min`; `max: 0` — бесплатно |
| `category` | `concert` — концерт, `theatre` — спектакль, `exhibition` — выставка, `festival` — фестиваль, `cinema` — кино, `excursion` — экскурсия, `other` — другое |
| `age` | Возраст посетителя, целое от 0 до 120; допускаются события с возрастным ограничением не выше этого возраста |
| `limit`, `offset` | Размер страницы от 1 до 100, смещение от 0; `total` — все совпадения до пагинации |
| `score` | Число от 0 до 1, больше — ближе к исходному событию |
| Временные метки | ISO 8601 с часовым поясом, например `2026-11-14T19:00:00+05:00` |

Все строковые аргументы должны содержать непробельный символ. Неизвестные поля
аргументов запрещены. Отсутствие необязательного поискового фильтра или `null`
означает отсутствие ограничения. Пустая выдача — `events: []`.

`EventSummary` содержит `id`, `title`, `category`, `tags`, `date_start`, `date_end`,
`city`, `venue`, `price_min`, `price_max`, `is_free`, `age_limit`.
`date_end`, `price_min`, `price_max` могут быть `null`; неизвестная цена не равна нулю.
`age_limit` — одно из значений `0`, `6`, `12`, `16`, `18`.

`EventCard` дополняет краткую карточку полями `description`, `address`, `lat`, `lon`,
`duration_min`, `language`, `org_link`, `source_url`, `verified_at`, `status`.
Координаты, длительность, язык, ссылка на организатора и время проверки могут быть
`null`. `status`: `published`, `draft`, `cancelled`.

`submitted` в результате `save_feedback` означает регистрацию обращения;
доставку организатору контракт отдельно не подтверждает.
