"""Database setup — SQLAlchemy engine + session, mirroring AppDbContext.

The original used EF Core + Npgsql against Supabase Postgres. We parse the same
Npgsql-style connection string so this app can point at the *same* database, and
we keep identical table/column names (see models.py).
"""
from __future__ import annotations

import logging
from collections.abc import Iterator

from sqlalchemy import create_engine
from sqlalchemy.orm import DeclarativeBase, Session, sessionmaker

from .config import config

logger = logging.getLogger(__name__)


def _npgsql_to_sqlalchemy_url(conn: str) -> str:
    """Convert "Host=...;Port=...;Database=...;Username=...;Password=...;SSL Mode=Require"
    into a SQLAlchemy postgresql+psycopg2 URL."""
    parts: dict[str, str] = {}
    for segment in conn.split(";"):
        segment = segment.strip()
        if not segment or "=" not in segment:
            continue
        key, _, value = segment.partition("=")
        parts[key.strip().lower()] = value.strip()

    host = parts.get("host", "localhost")
    port = parts.get("port", "5432")
    database = parts.get("database", "postgres")
    username = parts.get("username", "postgres")
    password = parts.get("password", "")

    from urllib.parse import quote_plus

    user_enc = quote_plus(username)
    pass_enc = quote_plus(password)

    query = ""
    ssl_mode = parts.get("ssl mode") or parts.get("sslmode")
    if ssl_mode:
        query = f"?sslmode={ssl_mode.lower()}"

    return f"postgresql+psycopg2://{user_enc}:{pass_enc}@{host}:{port}/{database}{query}"


_conn_string = config.get("ConnectionStrings:DefaultConnection") or ""
DATABASE_URL = _npgsql_to_sqlalchemy_url(_conn_string) if _conn_string else "sqlite:///./jobflow.db"

engine = create_engine(DATABASE_URL, pool_pre_ping=True, future=True)
SessionLocal = sessionmaker(bind=engine, autoflush=False, expire_on_commit=False, future=True)


class Base(DeclarativeBase):
    pass


def get_db() -> Iterator[Session]:
    """FastAPI dependency — yields a session and closes it afterwards."""
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
