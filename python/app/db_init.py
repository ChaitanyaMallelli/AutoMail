"""Database initialization + seeding — ports the startup block in Program.cs.

Creates any missing tables (EF used GenerateCreateScript; SQLAlchemy uses
create_all, which is also create-if-not-exists) and seeds the default
UserProfile (Id=1) and UserJobPreferences (Id=1).
"""
from __future__ import annotations

import logging

from .config import config
from .database import Base, SessionLocal, engine
from .models import UserJobPreferences, UserProfile
from .utils import utcnow

logger = logging.getLogger(__name__)


def init_db() -> None:
    try:
        Base.metadata.create_all(bind=engine)
    except Exception as ex:  # noqa: BLE001
        logger.error("Critical database initialization error: %s", ex)
        return

    with SessionLocal() as db:
        # Seed default user profile (matches OnModelCreating HasData + Program.cs seed)
        try:
            if db.query(UserProfile).count() == 0:
                db.add(
                    UserProfile(
                        Id=1,
                        FullName=config.get("UserProfile:FullName") or "Chaitanya Mallelli",
                        Email=config.get("UserProfile:Email") or "MallelliChaitanya5@gmail.com",
                        Phone=config.get("UserProfile:Phone") or "+91 9390981596",
                        LinkedInUrl=config.get("UserProfile:LinkedInUrl")
                        or "https://www.linkedin.com/in/chaitanya-mallelli-7a9b76204/",
                    )
                )
                db.commit()
        except Exception as ex:  # noqa: BLE001
            logger.error("Failed to seed default user profile: %s", ex)
            db.rollback()

        # Seed default UserJobPreferences (Id=1)
        try:
            if db.query(UserJobPreferences).count() == 0:
                db.add(
                    UserJobPreferences(
                        Id=1,
                        AutoApplyEnabled=True,
                        MinExperienceYears=3,
                        MaxExperienceYears=4,
                        MinAtsScore=30,
                        DailyReportUtcHour=15,
                        UpdatedAt=utcnow(),
                    )
                )
                db.commit()
        except Exception as ex:  # noqa: BLE001
            logger.error("Failed to seed UserJobPreferences: %s", ex)
            db.rollback()
