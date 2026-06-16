"""IJobBoardScraper port — a simple Protocol + base class."""
from __future__ import annotations

from typing import Protocol

from ...models import ScoutedJob


class JobBoardScraper(Protocol):
    board_name: str

    async def scrape_posts(self, keywords: list[str]) -> list[ScoutedJob]:
        ...
