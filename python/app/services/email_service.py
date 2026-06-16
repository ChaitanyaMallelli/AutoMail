"""EmailService — SMTP send with optional open-tracking pixel + resume attachment.

Port of EmailService.cs. Uses stdlib smtplib (Gmail SMTP, STARTTLS on 587).
"""
from __future__ import annotations

import html as html_lib
import logging
import smtplib
import ssl
import uuid
from email.mime.application import MIMEApplication
from email.mime.multipart import MIMEMultipart
from email.mime.text import MIMEText
from pathlib import Path

from sqlalchemy.orm import Session

from ..config import config
from ..models import GeneratedEmail, UserProfile
from ..utils import utcnow

logger = logging.getLogger(__name__)


class EmailService:
    def send_email(
        self,
        email: GeneratedEmail,
        profile: UserProfile,
        attachment_path: str | None = None,
        tracking_base_url: str | None = None,
        db: Session | None = None,
    ) -> tuple[bool, str | None]:
        """Returns (success, error_message). Mirrors SendEmailAsync."""
        try:
            smtp_server = config.get("Gmail:SmtpServer") or "smtp.gmail.com"
            port = int(config.get("Gmail:Port") or "587")
            sender_email = config.get("Gmail:SenderEmail") or profile.Email
            app_password = config.get("Gmail:AppPassword")
            if not app_password:
                raise RuntimeError("Gmail App Password is not configured")

            msg = MIMEMultipart("mixed")
            msg["From"] = f"{profile.FullName} <{sender_email}>"
            msg["To"] = email.RecipientEmail
            msg["Subject"] = email.Subject

            body_text = email.Body
            is_html = False

            # Generate tracking token + embed pixel
            if tracking_base_url:
                email.TrackingToken = str(uuid.uuid4())
                if db is not None:
                    db.commit()
                pixel_url = f"{tracking_base_url.rstrip('/')}/track/open/{email.TrackingToken}"
                encoded_body = html_lib.escape(email.Body).replace("\n", "<br/>")
                body_text = (
                    f'<html><body><div style="font-family:sans-serif;font-size:14px;">{encoded_body}</div>'
                    f'<img src="{pixel_url}" width="1" height="1" alt="" style="display:none;"/></body></html>'
                )
                is_html = True

            msg.attach(MIMEText(body_text, "html" if is_html else "plain", "utf-8"))

            # Attach resume if available
            if attachment_path and Path(attachment_path).exists():
                with open(attachment_path, "rb") as fh:
                    part = MIMEApplication(fh.read(), Name=Path(attachment_path).name)
                part["Content-Disposition"] = f'attachment; filename="{Path(attachment_path).name}"'
                msg.attach(part)
                logger.info("Attached resume to email: %s", attachment_path)

            logger.info("Sending email to %s with subject: %s", email.RecipientEmail, email.Subject)

            context = ssl.create_default_context()
            with smtplib.SMTP(smtp_server, port, timeout=30) as client:
                client.ehlo()
                client.starttls(context=context)
                client.login(sender_email, app_password)
                client.send_message(msg)

            logger.info("Email sent successfully to %s", email.RecipientEmail)
            return True, None
        except Exception as ex:  # noqa: BLE001
            logger.error("Failed to send email to %s: %s", email.RecipientEmail, ex)
            return False, str(ex)
