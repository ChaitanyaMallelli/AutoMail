"""SQLAlchemy ORM models — faithful port of Models/*.cs.

Table and column names are kept identical to the EF Core schema (PascalCase,
pluralized table names) so this app can run against the same Postgres/Supabase DB.
Enum columns are stored as their *name* strings, matching EF's HasConversion<string>().
"""
from __future__ import annotations

import enum
from datetime import datetime

# pyrefly: ignore [missing-import]
from sqlalchemy import (
    BigInteger,
    Boolean,
    DateTime,
    Enum as SAEnum,
    ForeignKey,
    Integer,
    String,
    Text,
)
# pyrefly: ignore [missing-import]
from sqlalchemy.orm import Mapped, mapped_column, relationship
# pyrefly: ignore [missing-import]
from sqlalchemy.types import TypeDecorator

from .database import Base
from .utils import utcnow


# ── Enums (mirror Models/*.cs) ────────────────────────────────────────────────
class JobSource(str, enum.Enum):
    Telegram = "Telegram"
    Upload = "Upload"


class SourceType(str, enum.Enum):
    Text = "Text"
    Image = "Image"
    Pdf = "Pdf"
    Url = "Url"


class JobStatus(str, enum.Enum):
    Pending = "Pending"
    EmailGenerated = "EmailGenerated"
    Approved = "Approved"
    Sent = "Sent"
    Failed = "Failed"
    Skipped = "Skipped"
    SavedForLater = "SavedForLater"
    FollowUpSent = "FollowUpSent"
    InterviewScheduled = "InterviewScheduled"
    Rejected = "Rejected"
    Offered = "Offered"
    Ghosted = "Ghosted"


class ScoutedJobStatus(str, enum.Enum):
    New = "New"
    SentToTelegram = "SentToTelegram"
    IgnoredByGemini = "IgnoredByGemini"
    Applied = "Applied"


class AutoApplyStatus(str, enum.Enum):
    Applied = "Applied"
    Skipped = "Skipped"
    Failed = "Failed"


def _enum_col(py_enum, **kwargs):
    """String-backed enum column storing the member name (EF HasConversion<string>)."""
    return mapped_column(
        SAEnum(py_enum, native_enum=False, values_callable=lambda e: [m.value for m in e], length=50),
        **kwargs,
    )


class _IntEnum(TypeDecorator):
    """Integer-backed enum, matching EF Core's *default* enum storage (declaration order:
    0, 1, 2, …). Used for columns the C# AppDbContext did NOT mark HasConversion<string>()
    — i.e. ScoutedJob.Status, which is an integer column in the existing database."""

    impl = Integer
    cache_ok = True

    def __init__(self, enum_cls, order):
        super().__init__()
        self._enum_cls = enum_cls
        self._order = list(order)

    def process_bind_param(self, value, dialect):
        if value is None:
            return None
        if not isinstance(value, self._enum_cls):
            value = self._enum_cls(value)
        return self._order.index(value)

    def process_result_value(self, value, dialect):
        if value is None:
            return None
        return self._order[int(value)]


_SCOUTED_STATUS_ORDER = [
    ScoutedJobStatus.New,
    ScoutedJobStatus.SentToTelegram,
    ScoutedJobStatus.IgnoredByGemini,
    ScoutedJobStatus.Applied,
]


# ── JobPost ───────────────────────────────────────────────────────────────────
class JobPost(Base):
    __tablename__ = "JobPosts"

    Id: Mapped[int] = mapped_column("Id", Integer, primary_key=True, autoincrement=True)
    CompanyName: Mapped[str] = mapped_column("CompanyName", String(200), default="")
    Role: Mapped[str] = mapped_column("Role", String(200), default="")
    RequiredSkills: Mapped[str | None] = mapped_column("RequiredSkills", String(2000), nullable=True)
    RecruiterEmail: Mapped[str | None] = mapped_column("RecruiterEmail", String(200), nullable=True)
    ExperienceRequired: Mapped[str | None] = mapped_column("ExperienceRequired", String(100), nullable=True)
    Location: Mapped[str | None] = mapped_column("Location", String(200), nullable=True)
    Source = _enum_col(JobSource, name="Source")
    SourceType = _enum_col(SourceType, name="SourceType")
    RawContent: Mapped[str | None] = mapped_column("RawContent", Text, nullable=True)
    ImagePath: Mapped[str | None] = mapped_column("ImagePath", String(500), nullable=True)
    AtsScore: Mapped[int] = mapped_column("AtsScore", Integer, default=0)
    SkillMatchPercentage: Mapped[int] = mapped_column("SkillMatchPercentage", Integer, default=0)
    Status = _enum_col(JobStatus, name="Status", default=JobStatus.Pending, index=True)
    CreatedAt: Mapped[datetime] = mapped_column("CreatedAt", DateTime, default=utcnow, index=True)
    UpdatedAt: Mapped[datetime | None] = mapped_column("UpdatedAt", DateTime, nullable=True)
    TelegramChatId: Mapped[int | None] = mapped_column("TelegramChatId", BigInteger, nullable=True, index=True)
    FollowUpReminderSent: Mapped[bool] = mapped_column("FollowUpReminderSent", Boolean, default=False)
    ResponseNotes: Mapped[str | None] = mapped_column("ResponseNotes", String(2000), nullable=True)

    GeneratedEmail: Mapped["GeneratedEmail | None"] = relationship(
        back_populates="JobPost", uselist=False, cascade="all, delete-orphan"
    )


# ── GeneratedEmail ─────────────────────────────────────────────────────────────
class GeneratedEmail(Base):
    __tablename__ = "GeneratedEmails"

    Id: Mapped[int] = mapped_column("Id", Integer, primary_key=True, autoincrement=True)
    JobPostId: Mapped[int] = mapped_column(
        "JobPostId", Integer, ForeignKey("JobPosts.Id", ondelete="CASCADE"), index=True
    )
    Subject: Mapped[str] = mapped_column("Subject", String(300), default="")
    Body: Mapped[str] = mapped_column("Body", Text, default="")
    RecipientEmail: Mapped[str] = mapped_column("RecipientEmail", String(200), default="")
    IsApproved: Mapped[bool] = mapped_column("IsApproved", Boolean, default=False)
    IsSent: Mapped[bool] = mapped_column("IsSent", Boolean, default=False, index=True)
    SentAt: Mapped[datetime | None] = mapped_column("SentAt", DateTime, nullable=True)
    ErrorMessage: Mapped[str | None] = mapped_column("ErrorMessage", String(1000), nullable=True)
    RetryCount: Mapped[int] = mapped_column("RetryCount", Integer, default=0)
    Tone: Mapped[str | None] = mapped_column("Tone", String(50), default="professional", nullable=True)
    CreatedAt: Mapped[datetime] = mapped_column("CreatedAt", DateTime, default=utcnow)
    # Feature 9: Email open tracking
    TrackingToken: Mapped[str | None] = mapped_column("TrackingToken", String(64), nullable=True)
    OpenedAt: Mapped[datetime | None] = mapped_column("OpenedAt", DateTime, nullable=True)
    # Feature 8: Gmail reply detection
    MessageId: Mapped[str | None] = mapped_column("MessageId", String(500), nullable=True)
    RepliedAt: Mapped[datetime | None] = mapped_column("RepliedAt", DateTime, nullable=True)
    ReplySubject: Mapped[str | None] = mapped_column("ReplySubject", String(200), nullable=True)
    ReplySnippet: Mapped[str | None] = mapped_column("ReplySnippet", String(1000), nullable=True)
    ReplyClassification: Mapped[str | None] = mapped_column("ReplyClassification", String(50), nullable=True)

    JobPost: Mapped["JobPost"] = relationship(back_populates="GeneratedEmail")


# ── Resume ─────────────────────────────────────────────────────────────────────
class Resume(Base):
    __tablename__ = "Resumes"

    Id: Mapped[int] = mapped_column("Id", Integer, primary_key=True, autoincrement=True)
    FullText: Mapped[str] = mapped_column("FullText", Text, default="")
    Skills: Mapped[str | None] = mapped_column("Skills", Text, nullable=True)
    Experience: Mapped[str | None] = mapped_column("Experience", Text, nullable=True)
    Education: Mapped[str | None] = mapped_column("Education", String(2000), nullable=True)
    FilePath: Mapped[str | None] = mapped_column("FilePath", String(500), nullable=True)
    IsActive: Mapped[bool] = mapped_column("IsActive", Boolean, default=True, index=True)
    UploadedAt: Mapped[datetime] = mapped_column("UploadedAt", DateTime, default=utcnow)


# ── UserProfile ──────────────────────────────────────────────────────────────
class UserProfile(Base):
    __tablename__ = "UserProfiles"

    Id: Mapped[int] = mapped_column("Id", Integer, primary_key=True, autoincrement=True)
    FullName: Mapped[str] = mapped_column("FullName", String(200), default="")
    Email: Mapped[str] = mapped_column("Email", String(200), default="")
    Phone: Mapped[str | None] = mapped_column("Phone", String(20), nullable=True)
    LinkedInUrl: Mapped[str | None] = mapped_column("LinkedInUrl", String(300), nullable=True)
    TelegramChatId: Mapped[int | None] = mapped_column("TelegramChatId", BigInteger, nullable=True)


# ── ScoutedJob ───────────────────────────────────────────────────────────────
class ScoutedJob(Base):
    __tablename__ = "ScoutedJobs"

    Id: Mapped[int] = mapped_column("Id", Integer, primary_key=True, autoincrement=True)
    LinkedInUrl: Mapped[str] = mapped_column("LinkedInUrl", String(500), default="", index=True)
    RawText: Mapped[str | None] = mapped_column("RawText", String(5000), nullable=True)
    KeywordMatched: Mapped[str | None] = mapped_column("KeywordMatched", String(200), nullable=True)
    # Integer-backed in the DB (EF stored this enum as int, not string).
    Status = mapped_column("Status", _IntEnum(ScoutedJobStatus, _SCOUTED_STATUS_ORDER), default=ScoutedJobStatus.New, index=True)
    CreatedAt: Mapped[datetime] = mapped_column("CreatedAt", DateTime, default=utcnow)
    Board: Mapped[str] = mapped_column("Board", String(50), default="LinkedIn")
    JobId: Mapped[str | None] = mapped_column("JobId", String(100), nullable=True)


# ── UserJobPreferences (single row, Id=1) ────────────────────────────────────
class UserJobPreferences(Base):
    __tablename__ = "UserJobPreferences"

    Id: Mapped[int] = mapped_column("Id", Integer, primary_key=True, autoincrement=True)
    AutoApplyEnabled: Mapped[bool] = mapped_column("AutoApplyEnabled", Boolean, default=True)
    MinExperienceYears: Mapped[int] = mapped_column("MinExperienceYears", Integer, default=3)
    MaxExperienceYears: Mapped[int] = mapped_column("MaxExperienceYears", Integer, default=4)
    PreferredWorkModes: Mapped[str | None] = mapped_column("PreferredWorkModes", String(200), nullable=True)
    PreferredLocations: Mapped[str | None] = mapped_column("PreferredLocations", String(500), nullable=True)
    RequiredSkills: Mapped[str | None] = mapped_column("RequiredSkills", String(1000), nullable=True)
    MinSalaryLpa: Mapped[int | None] = mapped_column("MinSalaryLpa", Integer, nullable=True)
    MaxSalaryLpa: Mapped[int | None] = mapped_column("MaxSalaryLpa", Integer, nullable=True)
    ExcludedCompanies: Mapped[str | None] = mapped_column("ExcludedCompanies", String(1000), nullable=True)
    MinAtsScore: Mapped[int] = mapped_column("MinAtsScore", Integer, default=30)
    NotificationEmail: Mapped[str | None] = mapped_column("NotificationEmail", String(200), nullable=True)
    DailyReportUtcHour: Mapped[int] = mapped_column("DailyReportUtcHour", Integer, default=15)
    UpdatedAt: Mapped[datetime] = mapped_column("UpdatedAt", DateTime, default=utcnow)


# ── AutoApplyLog ─────────────────────────────────────────────────────────────
class AutoApplyLog(Base):
    __tablename__ = "AutoApplyLogs"

    Id: Mapped[int] = mapped_column("Id", Integer, primary_key=True, autoincrement=True)
    CompanyName: Mapped[str] = mapped_column("CompanyName", String(200), default="")
    JobTitle: Mapped[str] = mapped_column("JobTitle", String(200), default="")
    JobId: Mapped[str | None] = mapped_column("JobId", String(100), nullable=True, index=True)
    JobUrl: Mapped[str | None] = mapped_column("JobUrl", String(500), nullable=True, index=True)
    WorkMode: Mapped[str | None] = mapped_column("WorkMode", String(100), nullable=True)
    Location: Mapped[str | None] = mapped_column("Location", String(200), nullable=True)
    ExperienceRequired: Mapped[str | None] = mapped_column("ExperienceRequired", String(100), nullable=True)
    SalaryRange: Mapped[str | None] = mapped_column("SalaryRange", String(100), nullable=True)
    Platform: Mapped[str] = mapped_column("Platform", String(50), default="LinkedIn")
    Status = _enum_col(AutoApplyStatus, name="Status", index=True)
    SkipReason: Mapped[str | None] = mapped_column("SkipReason", String(500), nullable=True)
    EmailSent: Mapped[bool] = mapped_column("EmailSent", Boolean, default=False)
    TelegramNotified: Mapped[bool] = mapped_column("TelegramNotified", Boolean, default=False)
    JobPostId: Mapped[int | None] = mapped_column("JobPostId", Integer, nullable=True)
    AppliedAt: Mapped[datetime] = mapped_column("AppliedAt", DateTime, default=utcnow, index=True)
