FROM python:3.12-slim
COPY --from=ghcr.io/astral-sh/uv:0.13.8 /uv /uvx /bin/

ENV PYTHONUNBUFFERED=1 \
    UV_PYTHON_DOWNLOADS=never \
    PATH="/app/.venv/bin:$PATH"
WORKDIR /app

COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev
COPY . .

EXPOSE 8080
CMD ["uvicorn", "api:app", "--host", "0.0.0.0", "--port", "8080"]
