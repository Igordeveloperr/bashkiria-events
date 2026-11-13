# Сервис ИИ

GigaChat (Сбер) → LangGraph → Langfuse. Весь код — в `main.py`.
Граф пока состоит из одного узла `generate-response`; поиск и БД ещё не подключены.

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

4. В `.env` заполни `GIGACHAT_CREDENTIALS`, `LANGFUSE_PUBLIC_KEY`, `LANGFUSE_SECRET_KEY`.
   `LANGFUSE_BASE_URL` должен указывать на регион твоего проекта или свой сервер.
   Scope для личного API — `GIGACHAT_API_PERS`; для корпоративного укажи свой.
5. Запускай:

   ```powershell
   uv run --frozen python main.py "Помоги подобрать концерт в Уфе"
   ```

В Langfuse открой трассы и найди `recommend-events` по напечатанному Trace ID.
Будут видны узел графа, запрос/ответ, модель, токены, время и ошибки.
Стоимость GigaChat потребует настройки цены модели в Langfuse, если её нет в каталоге.
Для группировки запросов задай `CHAT_SESSION_ID`; историю диалога это не сохраняет.

Если возникает `CERTIFICATE_VERIFY_FAILED`, скачай сертификат по
[инструкции Сбера](https://developers.sber.ru/docs/ru/gigachat/certificates)
и укажи путь в `GIGACHAT_CA_BUNDLE_FILE`. Проверка TLS включена.

Ключи хранятся только в `.env` (он исключён из Git). Перед отправкой трасс маскируются
email, типовые российские номера телефонов и ключи из окружения.
Остальной текст запросов и ответов записывается в Langfuse.

Проверка графа и экспорта без внешних API: `uv run --frozen python -m unittest -v`.
Для self-hosted Langfuse SDK 5 требует сервер версии 4.59.0+.
