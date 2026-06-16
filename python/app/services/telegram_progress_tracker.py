"""TelegramProgressTracker — in-memory step tracker (port of the static C# class).

Used by the live "Telegram Processing" page to show step-by-step automation status.
Thread-safe via a lock; state is process-global like the original static dictionaries.
"""
from __future__ import annotations

import threading
import time

_lock = threading.RLock()
_progress: dict[int, list[str]] = {}
_latest_job_ids: dict[int, int] = {}
_last_execution_start = 0.0  # monotonic seconds


class TelegramProgressTracker:
    @staticmethod
    def update_progress(chat_id: int, step: str) -> None:
        with _lock:
            steps = _progress.get(chat_id)
            if steps is None:
                _progress[chat_id] = [step]
                return
            prefix = step.split(":")[0] + ":"
            for i, existing in enumerate(steps):
                if existing.startswith(prefix):
                    steps[i] = step
                    return
            steps.append(step)

    @staticmethod
    def reset_progress(chat_id: int) -> None:
        with _lock:
            _progress.pop(chat_id, None)
            _latest_job_ids.pop(chat_id, None)

    @staticmethod
    def get_progress(chat_id: int) -> list[str]:
        with _lock:
            return list(_progress.get(chat_id, []))

    @staticmethod
    def set_latest_job_id(chat_id: int, job_id: int) -> None:
        with _lock:
            _latest_job_ids[chat_id] = job_id

    @staticmethod
    def get_latest_job_id(chat_id: int) -> int:
        with _lock:
            return _latest_job_ids.get(chat_id, 0)

    @staticmethod
    def record_execution_start() -> None:
        global _last_execution_start
        with _lock:
            _last_execution_start = time.monotonic()

    @staticmethod
    def is_recent_execution_active() -> bool:
        with _lock:
            return (time.monotonic() - _last_execution_start) < 15

    @staticmethod
    def all_progress() -> dict[int, list[str]]:
        with _lock:
            return {k: list(v) for k, v in _progress.items()}
