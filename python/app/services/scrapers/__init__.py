"""Job-board scrapers (port of Services/*ScraperService.cs)."""
from .base import JobBoardScraper
from .indeed_scraper import IndeedScraperService
from .linkedin_scraper import LinkedInScraperService
from .naukri_scraper import NaukriScraperService

__all__ = [
    "JobBoardScraper",
    "LinkedInScraperService",
    "NaukriScraperService",
    "IndeedScraperService",
]
