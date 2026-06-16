"""Internal DTOs — port of DTOs/*.cs. Plain dataclasses (used between services)."""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class JobExtractionResult:
    CompanyName: str = ""
    Role: str = ""
    RequiredSkills: str = ""
    RecruiterEmail: str | None = None
    ExperienceRequired: str | None = None
    Location: str | None = None
    RawContent: str | None = None
    IsSuccessful: bool = False
    ErrorMessage: str | None = None


@dataclass
class ResumeExtractionResult:
    Skills: list[str] = field(default_factory=list)
    Experience: str | None = None
    Education: str | None = None
    FullText: str = ""
    IsSuccessful: bool = False
    ErrorMessage: str | None = None


@dataclass
class ResumeMatchResult:
    MatchingSkills: list[str] = field(default_factory=list)
    MissingSkills: list[str] = field(default_factory=list)
    MatchPercentage: int = 0
    AtsScore: int = 0
    Summary: str = ""


@dataclass
class EmailDraftDto:
    Subject: str = ""
    Body: str = ""
    RecipientEmail: str = ""
    IsSuccessful: bool = False
    ErrorMessage: str | None = None
