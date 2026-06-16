"""ResumeMatchingService — skill matching + ATS scoring (port of ResumeMatchingService.cs)."""
from __future__ import annotations

import json
import logging

from ..models import Resume
from ..schemas import JobExtractionResult, ResumeMatchResult

logger = logging.getLogger(__name__)

_ALIASES: dict[str, list[str]] = {
    "javascript": ["js", "es6", "es2015"],
    "typescript": ["ts"],
    "c#": ["csharp", "c sharp", ".net"],
    "asp.net": ["aspnet", "asp.net core", "aspnetcore"],
    "sql server": ["mssql", "ms sql", "t-sql", "tsql"],
    "postgresql": ["postgres", "psql"],
    "react": ["reactjs", "react.js"],
    "angular": ["angularjs", "angular.js"],
    "node": ["nodejs", "node.js"],
    "python": ["py"],
    "machine learning": ["ml"],
    "artificial intelligence": ["ai"],
    "amazon web services": ["aws"],
    "google cloud platform": ["gcp"],
    "continuous integration": ["ci/cd", "cicd"],
}


class ResumeMatchingService:
    def match(self, job: JobExtractionResult, resume: Resume) -> ResumeMatchResult:
        try:
            resume_skills = self._parse_skills(resume.Skills)
            job_skills = self._parse_job_skills(job.RequiredSkills)

            matching: list[str] = []
            missing: list[str] = []
            for skill in job_skills:
                if any(self._is_skill_match(rs, skill) for rs in resume_skills):
                    matching.append(skill)
                else:
                    missing.append(skill)

            match_pct = round(len(matching) / len(job_skills) * 100) if job_skills else 0
            ats = self._calculate_ats(match_pct, resume, job)

            return ResumeMatchResult(
                MatchingSkills=matching,
                MissingSkills=missing,
                MatchPercentage=match_pct,
                AtsScore=ats,
                Summary=self._summary(matching, missing, match_pct),
            )
        except Exception as ex:  # noqa: BLE001
            logger.error("Error during resume matching: %s", ex)
            return ResumeMatchResult(MatchPercentage=0, AtsScore=0, Summary="Unable to perform skill matching.")

    def _parse_skills(self, skills_json: str | None) -> list[str]:
        if not skills_json or not skills_json.strip():
            return []
        try:
            skills = json.loads(skills_json)
            if isinstance(skills, list):
                return [str(s).strip() for s in skills if str(s).strip()]
        except Exception:  # noqa: BLE001
            pass
        return [s.strip() for s in skills_json.split(",") if s.strip()]

    def _parse_job_skills(self, required: str | None) -> list[str]:
        if not required or not required.strip():
            return []
        return [s.strip() for s in required.split(",") if s.strip()]

    def _is_skill_match(self, resume_skill: str, job_skill: str) -> bool:
        rs = resume_skill.lower().strip()
        js = job_skill.lower().strip()
        if rs == js:
            return True
        if rs in js or js in rs:
            return True
        for key, values in _ALIASES.items():
            all_forms = [v.lower() for v in values] + [key.lower()]
            if rs in all_forms and js in all_forms:
                return True
            if any(f in rs for f in all_forms) and any(f in js for f in all_forms):
                return True
        return False

    def _calculate_ats(self, skill_match_pct: int, resume: Resume, job: JobExtractionResult) -> int:
        score = 0.0
        score += skill_match_pct * 0.60

        completeness = 0
        if resume.FullText and resume.FullText.strip():
            completeness += 25
        if resume.Skills and resume.Skills.strip():
            completeness += 25
        if resume.Experience and resume.Experience.strip():
            completeness += 25
        if resume.Education and resume.Education.strip():
            completeness += 25
        score += completeness * 0.20

        if resume.Experience and resume.Experience.strip() and job.ExperienceRequired and job.ExperienceRequired.strip():
            score += 20
        elif resume.Experience and resume.Experience.strip():
            score += 10

        return min(100, round(score))

    def _summary(self, matching: list[str], missing: list[str], match_pct: int) -> str:
        parts: list[str] = []
        if matching:
            parts.append(f"Matching skills: {', '.join(matching)}.")
        if missing:
            parts.append(f"Missing skills: {', '.join(missing)}.")
        parts.append(f"Overall match: {match_pct}%.")
        if match_pct >= 70:
            parts.append("Strong match! Recommended to apply.")
        elif match_pct >= 40:
            parts.append("Moderate match. Consider applying.")
        else:
            parts.append("Low match. Review the requirements carefully.")
        return " ".join(parts)
