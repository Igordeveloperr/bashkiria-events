import os
import unittest
from unittest.mock import patch

from config import load_config


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

    def test_window_configuration_and_secret_repr(self):
        self.assertEqual(load_config().chat_history_turns, 5)
        os.environ["CHAT_HISTORY_TURNS"] = "0"
        config = load_config()
        self.assertEqual(config.chat_history_turns, 0)
        self.assertNotIn("database-secret", repr(config))
        self.assertNotIn("test-model-key", repr(config))

    def test_invalid_window_or_missing_database_is_rejected(self):
        for value in ("-1", "101", "five", "1.5", ""):
            with self.subTest(value=value), patch.dict(os.environ, {"CHAT_HISTORY_TURNS": value}):
                with self.assertRaisesRegex(ValueError, "CHAT_HISTORY_TURNS"):
                    load_config()
        del os.environ["DATABASE_URL"]
        with self.assertRaisesRegex(ValueError, "DATABASE_URL"):
            load_config()
