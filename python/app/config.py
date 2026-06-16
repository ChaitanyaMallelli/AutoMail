"""Configuration loader.

Mirrors ASP.NET Core's IConfiguration: reads ``appsettings.json`` then
``appsettings.local.json`` (override), and finally environment variables
(highest priority). Values are accessed with the same colon-separated keys the
C# code used, e.g. ``config["Telegram:BotToken"]`` or ``config.get("Gmail:Port")``.

Environment override convention: ``Telegram:BotToken`` -> env ``Telegram__BotToken``
(double underscore), exactly like ASP.NET Core.
"""
from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any

_BASE_DIR = Path(__file__).resolve().parent.parent  # the python/ folder


def _flatten(prefix: str, obj: Any, out: dict[str, str]) -> None:
    """Flatten nested JSON into colon-separated keys (IConfiguration style)."""
    if isinstance(obj, dict):
        for k, v in obj.items():
            key = f"{prefix}:{k}" if prefix else k
            _flatten(key, v, out)
    elif isinstance(obj, list):
        for i, v in enumerate(obj):
            key = f"{prefix}:{i}" if prefix else str(i)
            _flatten(key, v, out)
    else:
        out[prefix] = "" if obj is None else str(obj)


class Configuration:
    def __init__(self) -> None:
        self._data: dict[str, str] = {}
        self._load_json(_BASE_DIR / "appsettings.json")
        self._load_json(_BASE_DIR / "appsettings.local.json")

    def _load_json(self, path: Path) -> None:
        if path.exists():
            try:
                raw = json.loads(path.read_text(encoding="utf-8"))
                _flatten("", raw, self._data)
            except Exception as ex:  # noqa: BLE001
                print(f"[config] Failed to parse {path.name}: {ex}")

    def get(self, key: str, default: str | None = None) -> str | None:
        # Environment variable override (ASP.NET Core uses '__' for ':').
        env_key = key.replace(":", "__")
        if env_key in os.environ:
            return os.environ[env_key]
        if key in os.environ:
            return os.environ[key]
        return self._data.get(key, default)

    def __getitem__(self, key: str) -> str | None:
        return self.get(key)

    @property
    def base_dir(self) -> Path:
        return _BASE_DIR


config = Configuration()
