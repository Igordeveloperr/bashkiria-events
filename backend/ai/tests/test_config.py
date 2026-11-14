import os
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

from pydantic import SecretStr, ValidationError

from config import ApiConfig, Config, load_config


class ConfigTest(unittest.TestCase):
    def setUp(self):
        environment = patch.dict(os.environ, {
            "GIGACHAT_CREDENTIALS": "test-model-key",
            "LANGFUSE_PUBLIC_KEY": "pk-test",
            "LANGFUSE_SECRET_KEY": "sk-test",
            "DATABASE_URL": "postgresql://test:database-secret@localhost/test",
        }, clear=True)
        environment.start()
        self.addCleanup(environment.stop)
        # Never use the developer's .env as a fallback for missing test settings.
        dotenv = patch.dict(Config.model_config, {"env_file": None})
        dotenv.start()
        self.addCleanup(dotenv.stop)

    def test_window_configuration_and_secret_repr(self):
        self.assertEqual(load_config().chat_history_turns, 5)
        os.environ["CHAT_HISTORY_TURNS"] = "0"
        config = load_config()
        self.assertEqual(config.chat_history_turns, 0)
        self.assertNotIn("database-secret", repr(config))
        self.assertNotIn("test-model-key", repr(config))

    def test_credentials_are_secret_fields_and_masked_in_serialization(self):
        config = load_config()
        for field in (
            "gigachat_credentials", "langfuse_public_key", "langfuse_secret_key", "database_url"
        ):
            with self.subTest(field=field):
                value = getattr(config, field)
                self.assertIsInstance(value, SecretStr)
                self.assertEqual(value.get_secret_value(), os.environ[field.upper()])
                for output in (repr(config), str(config), str(config.model_dump()), config.model_dump_json()):
                    self.assertNotIn(value.get_secret_value(), output)

    def test_invalid_window_or_missing_database_is_rejected(self):
        for value in ("-1", "101", "five", "1.5", ""):
            with self.subTest(value=value), patch.dict(os.environ, {"CHAT_HISTORY_TURNS": value}):
                with self.assertRaisesRegex(ValueError, "CHAT_HISTORY_TURNS"):
                    load_config()
        del os.environ["DATABASE_URL"]
        with self.assertRaisesRegex(ValueError, "DATABASE_URL"):
            load_config()

    def test_required_secrets_reject_missing_empty_and_whitespace_values(self):
        for key in (
            "GIGACHAT_CREDENTIALS", "LANGFUSE_PUBLIC_KEY", "LANGFUSE_SECRET_KEY", "DATABASE_URL"
        ):
            for value in (None, "", " \t "):
                with self.subTest(key=key, value=value), patch.dict(os.environ):
                    if value is None:
                        del os.environ[key]
                    else:
                        os.environ[key] = value
                    with self.assertRaisesRegex(ValidationError, key):
                        load_config()

    def test_secret_whitespace_is_trimmed(self):
        os.environ["GIGACHAT_CREDENTIALS"] = "  test-model-key \t"
        self.assertEqual(load_config().gigachat_credentials.get_secret_value(), "test-model-key")

    def test_validation_errors_hide_input_values(self):
        del os.environ["DATABASE_URL"]
        os.environ["CHAT_HISTORY_TURNS"] = "invalid-private-value"
        with self.assertRaises(ValidationError) as raised:
            load_config()
        error = str(raised.exception)
        self.assertIn("DATABASE_URL", error)
        self.assertIn("CHAT_HISTORY_TURNS", error)
        for value in os.environ.values():
            self.assertNotIn(value, error)

    def test_dotenv_loading_and_environment_precedence(self):
        with TemporaryDirectory() as directory:
            dotenv = Path(directory) / ".env"
            dotenv.write_text(
                "GIGACHAT_CREDENTIALS=dotenv-key\n"
                "LANGFUSE_PUBLIC_KEY=pk-dotenv\n"
                "LANGFUSE_SECRET_KEY=sk-dotenv\n"
                "DATABASE_URL=postgresql://dotenv/test\n"
                "GIGACHAT_MODEL=dotenv-model\n"
                "LANGFUSE_TRACING_ENVIRONMENT=testing\n"
                "CHAT_HISTORY_TURNS=10\n"
                "CHAT_USER_ID=пользователь\n"
                "LANGFUSE_TRACING_ENABLED=false\n",
                encoding="utf-8",
            )
            with patch.dict(os.environ, {"GIGACHAT_CREDENTIALS": "shell-key"}, clear=True):
                config = Config(_env_file=dotenv)
            self.assertEqual(config.gigachat_credentials.get_secret_value(), "shell-key")
            self.assertEqual(config.langfuse_secret_key.get_secret_value(), "sk-dotenv")
            self.assertEqual(config.gigachat_model, "dotenv-model")
            self.assertEqual(config.langfuse_environment, "testing")
            self.assertEqual(config.chat_history_turns, 10)
            self.assertEqual(config.chat_user_id, "пользователь")

    def test_ca_bundle_path_is_resolved_from_config_directory(self):
        with TemporaryDirectory() as directory:
            certificate = Path(directory) / "test-ca.crt"
            certificate.write_text("test certificate", encoding="utf-8")
            with patch("config.BASE_DIR", Path(directory)), patch.dict(
                os.environ, {"GIGACHAT_CA_BUNDLE_FILE": " test-ca.crt "}
            ):
                self.assertEqual(load_config().gigachat_ca_bundle_file, str(certificate.resolve()))
            with patch.dict(os.environ, {"GIGACHAT_CA_BUNDLE_FILE": str(certificate)}):
                self.assertEqual(load_config().gigachat_ca_bundle_file, str(certificate.resolve()))
            with patch.dict(os.environ, {"GIGACHAT_CA_BUNDLE_FILE": str(certificate.parent)}):
                with self.assertRaisesRegex(ValidationError, "GIGACHAT_CA_BUNDLE_FILE"):
                    load_config()
        for value in ("", " \t "):
            with self.subTest(value=value), patch.dict(os.environ, {"GIGACHAT_CA_BUNDLE_FILE": value}):
                self.assertIsNone(load_config().gigachat_ca_bundle_file)

    def test_config_is_immutable(self):
        config = load_config()
        with self.assertRaises(ValidationError):
            config.chat_history_turns = 10

    def test_cors_origins_load_from_json_without_model_credentials(self):
        with patch.dict(os.environ, {"API_CORS_ORIGINS": '["https://events.example"]'}, clear=True):
            self.assertEqual(
                ApiConfig(_env_file=None).api_cors_origins, ["https://events.example"]
            )
