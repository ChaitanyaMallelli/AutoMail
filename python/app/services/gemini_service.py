"""GeminiService — AI extraction, email generation, interview, reply classification.

Faithful port of Services/GeminiService.cs including the app-wide rate limiter
(15 RPM => 4.5s min gap), exponential-backoff retry on 429/503, and a 6-hour
in-memory cache for text/URL extractions.
"""
from __future__ import annotations

import asyncio
import base64
import hashlib
import json
import logging
import time

import httpx

from ..config import config
from ..models import JobPost, Resume, UserProfile
from ..schemas import EmailDraftDto, JobExtractionResult, ResumeExtractionResult, ResumeMatchResult

logger = logging.getLogger(__name__)

_MIN_GAP_MS = 4500
_MAX_RETRIES = 5
_RETRY_DELAYS_MS = [5_000, 10_000, 20_000, 40_000, 60_000]
_CACHE_TTL_SEC = 6 * 60 * 60

# App-wide rate-limit gate shared across all instances (mirrors the static SemaphoreSlim).
_rate_gate = asyncio.Lock()
_last_call_monotonic = 0.0


class _TtlCache:
    def __init__(self) -> None:
        self._store: dict[str, tuple[float, object]] = {}

    def get(self, key: str):
        item = self._store.get(key)
        if not item:
            return None
        expires, value = item
        if time.monotonic() > expires:
            self._store.pop(key, None)
            return None
        return value

    def set(self, key: str, value: object, ttl: float) -> None:
        self._store[key] = (time.monotonic() + ttl, value)


_cache = _TtlCache()


def _hash(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()[:16].upper()


class GeminiService:
    def __init__(self) -> None:
        api_key = config.get("GeminiApi:ApiKey")
        if not api_key:
            raise ValueError("GeminiApi:ApiKey is not configured")
        self._api_key = api_key
        self._model = config.get("GeminiApi:Model") or "gemini-3.1-flash-lite"

    # ── Public extraction APIs ────────────────────────────────────────────────
    async def extract_job_details_from_text(self, text: str) -> JobExtractionResult:
        key = f"gemini:text:{_hash(text)}"
        cached = _cache.get(key)
        if cached is not None:
            return cached  # type: ignore[return-value]
        prompt = self._build_extraction_prompt(text)
        response = await self._call_gemini(prompt)
        result = self._parse_extraction(response, text)
        if result.IsSuccessful:
            _cache.set(key, result, _CACHE_TTL_SEC)
        return result

    async def extract_job_details_from_image(self, image_bytes: bytes, mime_type: str = "image/png") -> JobExtractionResult:
        prompt = self._build_image_extraction_prompt()
        response = await self._call_gemini_with_file(prompt, image_bytes, mime_type)
        return self._parse_extraction(response, "[Image upload]")

    async def extract_job_details_from_pdf(self, pdf_bytes: bytes) -> JobExtractionResult:
        prompt = self._build_image_extraction_prompt()
        response = await self._call_gemini_with_file(prompt, pdf_bytes, "application/pdf")
        return self._parse_extraction(response, "[PDF upload]")

    async def extract_job_details_from_url_content(self, html_content: str, url: str) -> JobExtractionResult:
        key = f"gemini:url:{_hash(url)}"
        cached = _cache.get(key)
        if cached is not None:
            return cached  # type: ignore[return-value]
        truncated = html_content[:8000]
        prompt = (
            f"Extract job details from this URL content ({url}). Return ONLY JSON (no markdown):\n"
            '{"companyName":"","role":"","requiredSkills":"","recruiterEmail":null,"experienceRequired":null,"location":null}\n'
            "Rules: ignore nav/footer/ads; null if not found; never fabricate.\n\nContent:\n" + truncated
        )
        response = await self._call_gemini(prompt)
        result = self._parse_extraction(response, url)
        if result.IsSuccessful:
            _cache.set(key, result, _CACHE_TTL_SEC)
        return result

    async def extract_resume_details_from_pdf(self, pdf_bytes: bytes) -> ResumeExtractionResult:
        try:
            prompt = """You are a resume/CV parser. Analyze this PDF resume and extract structured information.
Return ONLY a valid JSON object with these exact fields (no markdown, no code fences):

{
  "fullText": "complete text content of the resume",
  "skills": ["skill1", "skill2", "skill3"],
  "experience": "brief summary of work experience including companies, roles, and durations",
  "education": "education details including degrees, institutions, and years"
}

Rules:
- Extract ALL technical skills, tools, frameworks, and languages mentioned
- Include soft skills if explicitly mentioned
- For experience, summarize each role briefly
- For education, include degree, institution, and graduation year
- Be thorough and accurate. Extract everything visible in the resume."""

            response = await self._call_gemini_with_file(prompt, pdf_bytes, "application/pdf")
            cleaned = self._clean_json(response)
            result = json.loads(cleaned)

            skills = result.get("skills") or []
            if not isinstance(skills, list):
                skills = []

            return ResumeExtractionResult(
                FullText=str(result.get("fullText") or ""),
                Skills=[str(s) for s in skills],
                Experience=result.get("experience"),
                Education=result.get("education"),
                IsSuccessful=True,
            )
        except Exception as ex:  # noqa: BLE001
            logger.error("Failed to extract resume details from PDF via Gemini: %s", ex)
            return ResumeExtractionResult(IsSuccessful=False, ErrorMessage=f"Failed to parse resume: {ex}")

    async def generate_email(
        self,
        job: JobExtractionResult,
        resume: Resume,
        profile: UserProfile,
        match_result: ResumeMatchResult,
        tone: str = "professional",
    ) -> EmailDraftDto:
        try:
            prompt = self._build_email_prompt(job, resume, profile, match_result, tone)
            response = await self._call_gemini(prompt)
            return self._parse_email(response, job.RecruiterEmail or "")
        except Exception as ex:  # noqa: BLE001
            logger.error("Error generating email via Gemini: %s", ex)
            return EmailDraftDto(IsSuccessful=False, ErrorMessage=str(ex))

    async def generate_followup_email(self, job: JobPost, profile: UserProfile) -> EmailDraftDto:
        try:
            days_since = (_dt_utcnow() - job.CreatedAt).days
            prompt = f"""Generate a polite professional follow-up email for a job application. Return ONLY a valid JSON object with these exact fields (no markdown, no code fences):

{{
  "subject": "email subject line",
  "body": "full email body as plain text"
}}

Context:
- Applicant Name: {profile.FullName}
- Applicant Email: {profile.Email}
- Applicant Phone: {profile.Phone or "N/A"}
- Company: {job.CompanyName}
- Role Applied For: {job.Role}
- Original Application Date: {job.CreatedAt:%B %d, %Y}
- Days Since Application: {days_since}

Email rules:
- Polite and professional follow-up tone
- Reference the original application date
- Express continued interest in the role
- Keep it brief (under 120 words)
- Don't be pushy or demanding
- Include contact details in the closing
- Plain text only, no HTML"""
            response = await self._call_gemini(prompt)
            return self._parse_email(response, job.RecruiterEmail or "")
        except Exception as ex:  # noqa: BLE001
            logger.error("Error generating follow-up email via Gemini: %s", ex)
            return EmailDraftDto(IsSuccessful=False, ErrorMessage=str(ex))

    async def process_mock_interview_audio(
        self, audio_bytes: bytes, job: JobPost, profile: UserProfile, conversation_history: str
    ) -> str:
        try:
            prompt = f"""You are a professional corporate recruiter conducting a voice mock interview.
Your goal is to interview the applicant for the role of '{job.Role}' at '{job.CompanyName}'.
The applicant's name is {profile.FullName}.

Here is the job description you are hiring for:
{job.RawContent or job.RequiredSkills}

Here is the conversation history so far:
{conversation_history}

Listen to the applicant's attached voice message.
Respond directly to their voice message as the recruiter.
Keep your response conversational, concise (under 3 sentences), and natural to be spoken aloud.
Do not use markdown, bullet points, or complex formatting. Just plain conversational text.
If they just joined, greet them and ask the first interview question."""
            return await self._call_gemini_with_file(prompt, audio_bytes, "audio/ogg")
        except Exception as ex:  # noqa: BLE001
            logger.error("Error processing mock interview audio via Gemini: %s", ex)
            return "I'm sorry, I encountered an error processing your voice. Could you please try again?"

    async def process_live_interview_audio(self, audio_bytes: bytes, job: JobPost) -> str:
        try:
            prompt = f"""You are a live interview Co-Pilot. You are listening to a live job interview for the role of '{job.Role}' at '{job.CompanyName}'.
Job context: {job.RawContent or job.RequiredSkills}

Listen to the attached audio chunk from the live interview.
If you hear the recruiter asking a question, instantly output 2-3 extremely concise bullet points with suggested talking points for the applicant.
If there is no question, or just casual chatter, return the word 'SILENCE'.
Do not output greetings or conversation. ONLY bullet points. Keep it extremely brief so the applicant can read it instantly."""
            response = await self._call_gemini_with_file(prompt, audio_bytes, "audio/webm")
            if "SILENCE" in response.upper() or len(response) < 10:
                return ""
            return response
        except Exception as ex:  # noqa: BLE001
            logger.error("Error processing live interview audio chunk: %s", ex)
            return ""

    async def is_post_relevant(self, post_text: str) -> bool:
        try:
            prompt = (
                "Is this a developer job requiring 3+ years experience? Reply TRUE or FALSE only.\n\n"
                + post_text[:500]
            )
            response = await self._call_gemini(prompt)
            return response.strip().upper() == "TRUE"
        except Exception as ex:  # noqa: BLE001
            logger.error("Error filtering post via Gemini: %s", ex)
            return False

    async def batch_is_post_relevant(self, posts: list[str]) -> list[bool]:
        if not posts:
            return []
        try:
            numbered = "\n---\n".join(f"[{i + 1}] {p[:400]}" for i, p in enumerate(posts))
            prompt = (
                "For each numbered post below, reply TRUE if it's a developer job requiring 3+ years experience, FALSE otherwise.\n"
                f"Return ONLY a JSON array like [true,false,true] with exactly {len(posts)} values, no other text.\n\n{numbered}"
            )
            response = await self._call_gemini(prompt)
            cleaned = self._clean_json(response)
            results = json.loads(cleaned)
            if isinstance(results, list) and len(results) == len(posts):
                return [bool(x) for x in results]
        except Exception as ex:  # noqa: BLE001
            logger.warning("Batch relevance check failed, falling back to individual calls: %s", ex)
        fallback = []
        for p in posts:
            fallback.append(await self.is_post_relevant(p))
        return fallback

    async def classify_reply(self, reply_text: str, company: str, role: str) -> str:
        prompt = f"""You are an AI assistant classifying recruiter email replies.

A job seeker applied to {role} at {company}. The recruiter replied:

---
{reply_text[:1500]}
---

Classify this reply into EXACTLY ONE of these categories (return only the category word, nothing else):
- interview (recruiter wants to schedule an interview or call)
- rejected (application was declined)
- interested (recruiter is interested but no interview yet — asking for more info, portfolio, etc.)
- other (anything else — OOO, acknowledgement, etc.)

Return only the single category word."""
        try:
            result = await self._call_gemini(prompt)
            cleaned = result.strip().lower().split("\n")[0].strip()
            return cleaned if cleaned in ("interview", "rejected", "interested", "other") else "other"
        except Exception:  # noqa: BLE001
            return "other"

    async def call_gemini_public(self, prompt: str) -> str:
        return await self._call_gemini(prompt)

    # ── Prompt builders ───────────────────────────────────────────────────────
    def _build_extraction_prompt(self, text: str) -> str:
        return (
            "Extract job details. Return ONLY JSON (no markdown):\n"
            '{"companyName":"","role":"","requiredSkills":"","recruiterEmail":null,"experienceRequired":null,"location":null}\n'
            "Rules: null if missing; never fabricate; list all skills.\n\nText:\n" + text
        )

    def _build_image_extraction_prompt(self) -> str:
        return """You are a job posting analyzer. Look at this image which contains a job posting, LinkedIn hiring post, or job description.
Extract the following details from the image.
Return ONLY a valid JSON object with these exact fields (no markdown, no code fences):

{
  "companyName": "extracted company name or 'Unknown'",
  "role": "job title/role",
  "requiredSkills": "comma-separated list of required skills",
  "recruiterEmail": "email address if found, or null",
  "experienceRequired": "experience requirement e.g. '2-4 years' or null",
  "location": "job location or 'Remote' or null"
}

Rules:
- Read all text visible in the image carefully.
- Handle noisy screenshots, LinkedIn posts, and various formats.
- Extract recruiter/HR email addresses if visible.
- If a field is not clearly visible, use null.
- Never fabricate or guess information not in the image."""

    def _build_email_prompt(
        self,
        job: JobExtractionResult,
        resume: Resume,
        profile: UserProfile,
        match_result: ResumeMatchResult,
        tone: str = "professional",
    ) -> str:
        matching_skills_text = ", ".join(match_result.MatchingSkills) if match_result.MatchingSkills else "general professional skills"

        tone_lower = (tone or "").lower()
        if tone_lower == "enthusiastic":
            tone_instruction = """
Tone rules:
- Show genuine enthusiasm and passion for the role
- Use energetic but professional language
- Express excitement about the company and opportunity
- Be warm and personable while remaining professional
- Use active, dynamic verbs"""
        elif tone_lower == "concise":
            tone_instruction = """
Tone rules:
- Be extremely brief and to-the-point
- Maximum 80 words for the body
- No filler phrases or unnecessary pleasantries
- State qualifications directly
- Get straight to the value proposition"""
        else:
            tone_instruction = """
Tone rules:
- Professional and formal tone
- Warm but business-appropriate language
- Structured and clear communication
- Standard corporate email format"""

        return f"""Generate a professional job application email. Return ONLY a valid JSON object with these exact fields (no markdown, no code fences):

{{
  "subject": "email subject line",
  "body": "full email body as plain text"
}}

Use these REAL details about the applicant:
- Name: {profile.FullName}
- Email: {profile.Email}
- Phone: {profile.Phone or "N/A"}
- LinkedIn: {profile.LinkedInUrl or "N/A"}

Job details:
- Company: {job.CompanyName}
- Role: {job.Role}
- Required Skills: {job.RequiredSkills}
- Experience Required: {job.ExperienceRequired or "Not specified"}
- Location: {job.Location or "Not specified"}

Applicant's MATCHING skills (use ONLY these, never fabricate):
{matching_skills_text}

Match score: {match_result.MatchPercentage}%
{tone_instruction}

Email format rules:
- Professional subject line mentioning the role
- Proper greeting (Dear Hiring Manager/HR Team)
- Brief introduction (1-2 sentences)
- Mention ONLY the matching skills listed above
- NEVER claim skills or experience the applicant doesn't have
- Professional closing with contact details
- Keep it concise (under 200 words)
- Plain text only, no HTML"""

    # ── HTTP / rate limiting ──────────────────────────────────────────────────
    async def _call_gemini(self, prompt: str) -> str:
        url = f"https://generativelanguage.googleapis.com/v1beta/models/{self._model}:generateContent?key={self._api_key}"
        body = {
            "contents": [{"parts": [{"text": prompt}]}],
            "generationConfig": {"temperature": 0.3, "maxOutputTokens": 2048},
        }
        return await self._send_with_rate_limit(url, body, "text")

    async def _call_gemini_with_file(self, prompt: str, file_bytes: bytes, mime_type: str) -> str:
        url = f"https://generativelanguage.googleapis.com/v1beta/models/{self._model}:generateContent?key={self._api_key}"
        b64 = base64.b64encode(file_bytes).decode("ascii")
        body = {
            "contents": [
                {
                    "parts": [
                        {"text": prompt},
                        {"inline_data": {"mime_type": mime_type, "data": b64}},
                    ]
                }
            ],
            "generationConfig": {"temperature": 0.3, "maxOutputTokens": 2048},
        }
        return await self._send_with_rate_limit(url, body, mime_type)

    async def _send_with_rate_limit(self, url: str, body: dict, call_type: str) -> str:
        global _last_call_monotonic
        async with _rate_gate:
            elapsed_ms = (time.monotonic() - _last_call_monotonic) * 1000
            if elapsed_ms < _MIN_GAP_MS:
                wait_ms = _MIN_GAP_MS - elapsed_ms
                logger.info("Rate limiter: waiting %dms before next Gemini call.", int(wait_ms))
                await asyncio.sleep(wait_ms / 1000)

            async with httpx.AsyncClient(timeout=100.0) as client:
                for attempt in range(_MAX_RETRIES + 1):
                    retry_note = f" [retry {attempt}/{_MAX_RETRIES}]" if attempt > 0 else ""
                    logger.info("Calling Gemini API (%s)%s...", call_type, retry_note)
                    try:
                        response = await client.post(url, json=body)
                    except httpx.TimeoutException:
                        logger.warning("Gemini API timeout on attempt %d.", attempt + 1)
                        if attempt < _MAX_RETRIES:
                            await asyncio.sleep(_RETRY_DELAYS_MS[min(attempt, len(_RETRY_DELAYS_MS) - 1)] / 1000)
                            continue
                        raise

                    _last_call_monotonic = time.monotonic()

                    if response.status_code < 400:
                        return self._extract_text(response.text)

                    if response.status_code in (429, 503) and attempt < _MAX_RETRIES:
                        retry_after = response.headers.get("Retry-After")
                        if retry_after and retry_after.isdigit():
                            delay_ms = int(retry_after) * 1000
                        else:
                            delay_ms = _RETRY_DELAYS_MS[min(attempt, len(_RETRY_DELAYS_MS) - 1)]
                        logger.warning(
                            "Gemini API returned %d. Retrying in %dms (attempt %d/%d).",
                            response.status_code, delay_ms, attempt + 1, _MAX_RETRIES,
                        )
                        await asyncio.sleep(delay_ms / 1000)
                        continue

                    logger.error("Gemini API error: %d - %s", response.status_code, response.text)
                    raise RuntimeError(f"Gemini API returned {response.status_code}: {response.text}")

            raise RuntimeError("Gemini API: all retry attempts exhausted.")

    # ── Response parsing ──────────────────────────────────────────────────────
    def _extract_text(self, response_json: str) -> str:
        try:
            obj = json.loads(response_json)
            return obj["candidates"][0]["content"]["parts"][0]["text"]
        except Exception as ex:  # noqa: BLE001
            logger.error("Failed to parse Gemini response: %s", response_json)
            raise RuntimeError("No text content in Gemini response") from ex

    def _clean_json(self, response: str) -> str:
        cleaned = response.strip()
        if cleaned.startswith("```json"):
            cleaned = cleaned[7:]
        elif cleaned.startswith("```"):
            cleaned = cleaned[3:]
        if cleaned.endswith("```"):
            cleaned = cleaned[:-3]
        return cleaned.strip()

    def _parse_extraction(self, response: str, raw_content: str) -> JobExtractionResult:
        try:
            result = json.loads(self._clean_json(response))
            return JobExtractionResult(
                CompanyName=result.get("companyName") or "Unknown",
                Role=result.get("role") or "Unknown Role",
                RequiredSkills=result.get("requiredSkills") or "",
                RecruiterEmail=result.get("recruiterEmail"),
                ExperienceRequired=result.get("experienceRequired"),
                Location=result.get("location"),
                RawContent=raw_content,
                IsSuccessful=True,
            )
        except Exception as ex:  # noqa: BLE001
            logger.error("Failed to parse job extraction result from Gemini response: %s", ex)
            return JobExtractionResult(
                IsSuccessful=False, ErrorMessage=f"Failed to parse AI response: {ex}", RawContent=raw_content
            )

    def _parse_email(self, response: str, recipient_email: str) -> EmailDraftDto:
        try:
            result = json.loads(self._clean_json(response))
            return EmailDraftDto(
                Subject=result.get("subject") or "Job Application",
                Body=result.get("body") or "",
                RecipientEmail=recipient_email,
                IsSuccessful=True,
            )
        except Exception as ex:  # noqa: BLE001
            logger.error("Failed to parse email generation result from Gemini response: %s", ex)
            return EmailDraftDto(IsSuccessful=False, ErrorMessage=f"Failed to parse AI response: {ex}")


def _dt_utcnow():
    from .. import utils

    return utils.utcnow()
