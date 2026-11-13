# Сервис ИИ

GigaChat (Сбер) → LangGraph → Langfuse.
История хранится в PostgreSQL. `main.py` содержит граф и CLI, `chat.py` — сервис
диалогов, `storage.py` — работу с БД, `history.py` — скользящее окно,
`config.py` — параметры и загрузку `.env`, `prompts.py` — системный промпт.
Конфигурация валидируется через Pydantic Settings; ключи и `DATABASE_URL` имеют
тип `SecretStr` и маскируются при печати и JSON-сериализации. Перед передачей
клиентам их значения извлекаются методом `get_secret_value()`.
Граф пока состоит из одного узла `generate-response`; поиск ещё не подключён.

1. В кабинете [GigaChat API](https://developers.sber.ru/docs/ru/gigachat/individuals-quickstart)
   получи **ключ авторизации**. Он пойдёт в `GIGACHAT_CREDENTIALS`;
   временный access token SDK получает сам.
2. Создай проект в [Langfuse](https://cloud.langfuse.com), выпусти project API keys.
3. Из корня репозитория:

   ```powershell
   cd backend/ai
   uv sync --frozen
   if (-not (Test-Path .env)) { Copy-Item .env.example .env }
   ```

4. В `.env` заполни `GIGACHAT_CREDENTIALS`, `LANGFUSE_PUBLIC_KEY`, `LANGFUSE_SECRET_KEY`
   и `DATABASE_URL` (пример есть в `.env.example`).
   `LANGFUSE_BASE_URL` должен указывать на регион твоего проекта или свой сервер.
   Scope для личного API — `GIGACHAT_API_PERS`; для корпоративного укажи свой.
5. Подними PostgreSQL и примени миграции Alembic из корня репозитория:

   ```powershell
   docker compose up -d --build
   docker compose wait migrate
   docker compose logs migrate
   ```

   Одноразовый сервис `migrate` ждёт готовности PostgreSQL, выполняет
   `alembic upgrade head` и завершается. Повторный запуск применяет только новые
   ревизии. Схема меняется миграциями, а не при каждом сообщении.
6. Запускай из `backend/ai`:

   ```powershell
   uv run --frozen python main.py --user-id user-001 --session-key chat-001 "Помоги подобрать концерт в Уфе"
   uv run --frozen python main.py --user-id user-001 --session-key chat-001 "А что есть бесплатного?"
   ```

В Langfuse открой трассы и найди `recommend-events` по напечатанному Trace ID.
Будут видны узел графа, запрос/ответ, модель, токены, время и ошибки.
Стоимость GigaChat потребует настройки цены модели в Langfuse, если её нет в каталоге.
Запросы группируются в Langfuse по внутреннему UUID диалога из PG; передаётся также
`user_id`. CLI принимает `CHAT_USER_ID` и `CHAT_SESSION_ID` как значения по умолчанию
для `--user-id` и `--session-key`.

Интерактивный чат из `backend/ai`:

```powershell
uv run --frozen python chat.py --user-id user-001 --session-key chat-001
```

Можно запустить просто `uv run --frozen python chat.py`: недостающие ID будут
запрошены в терминале. Пустой ключ диалога создаёт новый UUID. Сообщения вводятся
по одному; `/exit`, `/quit`, Ctrl+C или конец ввода завершают чат. После ошибки
отдельного запроса можно продолжить переписку. Для продолжения истории после
перезапуска используй ту же пару `user-id` и `session-key`.

## История и скользящее окно

В PG сохраняются все сообщения, включая вопросы с неудачной генерацией ответа.
В модель передаются системный промпт, последние `CHAT_HISTORY_TURNS` завершённых
пар «пользователь — ассистент» и текущий вопрос. По умолчанию это 5 пар
(до 11 сообщений диалога плюс системный промпт). При `CHAT_HISTORY_TURNS=0`
модель получает только системный промпт и текущий вопрос. Обрезка происходит на
границе вызова модели и не удаляет строки из БД. Суммаризация не выполняется.

Это окно по числу ходов, а не жёсткий лимит токенов: один очень длинный вопрос
всё ещё может превысить контекст модели. Неуспешные и незавершённые ходы в контекст
следующего запроса не включаются. Размер истории, загружаемой из PG, также ограничен
окном; полную переписку для интерфейса можно получать отдельно с пагинацией.

## Подключение к backend для фронта

HTTP API и авторизация в этом репозитории пока не реализованы. Готовый сервис
принимает идентификаторы от вызывающего backend:

```python
from uuid import UUID
from chat import ChatService
from storage import ChatStore
from langfuse.langchain import CallbackHandler

service = ChatService(
    ChatStore(config.database_url.get_secret_value()),
    graph,
    history_turns=config.chat_history_turns,
    callback_factory=lambda: CallbackHandler(
        public_key=config.langfuse_public_key.get_secret_value()
    ),
)
reply = service.send_message(
    user_id=verified_user_id,
    session_key=payload.session_key,
    request_id=UUID(payload.request_id),
    content=payload.content,
)
```

`user_id` здесь — строковый ID из проверенной авторизации. Нельзя без проверки
подставлять ID из тела запроса. `session_key` обозначает отдельный диалог,
а не сессию входа; его уникальность ограничена пользователем. Поэтому одинаковые
ключи разных пользователей создают разные внутренние UUID.

Для каждого нового сообщения фронт генерирует `request_id` (UUID) и сохраняет его
при сетевых повторах. Успешный повтор возвращает прежний ответ с `cached=True`,
не вызывает модель и не добавляет строки. Повтор с другим текстом вызывает
`RequestConflictError`. Неудачный запрос сохраняет статус `failed`; повтор с тем же
UUID возвращает `ChatRunFailedError`. Чтобы заново запустить генерацию после ошибки,
нужен новый `request_id`. CLI печатает UUID до вызова и принимает `--request-id`.

На время генерации отдельное соединение удерживает advisory lock диалога.
Параллельный запрос в этот диалог получает `SessionBusyError` и может повториться
позже; другие диалоги работают независимо. Открытой транзакции во время вызова
GigaChat нет. Нужен прямой доступ к PG или PgBouncer в режиме session pooling:
transaction pooling с такими блокировками несовместим. После разрыва соединения
блокировка освобождается; при следующем обращении оставшийся `running` переводится
в `failed`. Ответ и статус `completed` сохраняются одной транзакцией.

Чтение для интерфейса также ограничено владельцем:

```python
store.sessions(verified_user_id, limit=50)
store.history(verified_user_id, session_key, limit=50)
store.history(verified_user_id, session_key, limit=50, before_seq=oldest_seq)
store.run(verified_user_id, session_key, request_id)
```

Страница истории возвращается в порядке возрастания `seq`; для предыдущей страницы
передай минимальный `seq` текущей страницы. `run()` возвращает статус, ошибку и
`trace_id` запуска. Формат таблиц описан в [backend/db](../db/README.md).

## Сертификаты и трассировка

Ошибка `httpx.ConnectError: [SSL: CERTIFICATE_VERIFY_FAILED] ... self-signed certificate
in certificate chain` означает, что Python не доверяет сертификату в цепочке сервера.
Для GigaChat требуется корневой сертификат Минцифры — это описано в
[инструкции Сбера](https://developers.sber.ru/docs/ru/gigachat/certificates).
`During task with name 'generate-response'` указывает на узел графа, где вызов модели
завершился этой сетевой ошибкой.

Скачай корневой сертификат в формате PEM по инструкции Сбера, сохрани его в
`backend/ai/certs/russian_trusted_root_ca_pem.crt` и добавь в `backend/ai/.env`:

```dotenv
GIGACHAT_CA_BUNDLE_FILE=certs/russian_trusted_root_ca_pem.crt
```

Затем перезапусти `main.py`. Путь может быть абсолютным; относительный путь считается
от `backend/ai`, независимо от каталога запуска. Если файл не найден, конфигурация
сообщит об этом до обращения к API. Проверка TLS остаётся включённой.
Если соединение проходит через корпоративный прокси или антивирус с подменой
сертификатов, в CA bundle понадобится также доверенный корневой сертификат этого ПО.

Ключи хранятся только в `.env` (он исключён из Git). Текст запросов и ответов,
включая email и телефоны, записывается в Langfuse целиком, без дополнительного
маскирования. В метаданные передаются внутренний ID диалога, ID пользователя,
запроса и запуска, а также служебные данные графа и модели.
Конфигурация с ключами в метаданные не передаётся.

## Проверки

Проверка окна, конфигурации, графа и экспорта без внешних API:

```powershell
uv run --frozen python -m unittest discover -s tests -v
```

Для интеграционных проверок укажи `TEST_DATABASE_URL` отдельной тестовой БД
в окружении или `.env` и выполни ту же команду. Тесты создают случайную схему,
применяют миграцию и удаляют только эту схему после завершения. Без этой переменной
проверки PG пропускаются. Проверяются сохранность полной истории, размер окна,
изоляция пользователей, повторные запросы, пагинация, блокировки, сбой процесса
и атомарность сохранения ответа.

Для self-hosted Langfuse SDK 5 требует сервер версии 4.59.0+.
