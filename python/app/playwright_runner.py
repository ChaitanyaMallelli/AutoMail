"""Run Playwright coroutines on a Windows-safe event loop.

On Windows the server's running loop (under uvicorn/APScheduler) is often a
``SelectorEventLoop``, which **cannot spawn subprocesses** (raises
``NotImplementedError``). Playwright launches the browser as a subprocess, so it
needs a ``ProactorEventLoop``. We run each Playwright coroutine in a dedicated
worker thread with its own Proactor loop — fully isolated from the server loop,
and non-blocking (the calling coroutine just awaits the thread).
"""
from __future__ import annotations

import asyncio
import sys
from collections.abc import Awaitable, Callable
from typing import TypeVar

T = TypeVar("T")


def _new_loop() -> asyncio.AbstractEventLoop:
    if sys.platform == "win32":
        return asyncio.ProactorEventLoop()  # subprocess-capable
    return asyncio.new_event_loop()


async def run_playwright(coro_factory: Callable[[], Awaitable[T]]) -> T:
    """Await a Playwright coroutine in a separate thread + Proactor loop.

    ``coro_factory`` must *create* the coroutine when called (e.g.
    ``lambda: scraper.scrape_posts(kw)``) so it's bound to the worker's loop.
    """

    def worker() -> T:
        loop = _new_loop()
        asyncio.set_event_loop(loop)
        try:
            return loop.run_until_complete(coro_factory())
        finally:
            try:
                loop.close()
            finally:
                asyncio.set_event_loop(None)

    return await asyncio.to_thread(worker)
