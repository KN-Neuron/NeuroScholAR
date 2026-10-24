"""Minimal export backend for NeuroScholAR.

The app sends a session summary only when the user presses export (EEG-derived data is sensitive),
and this service turns it into a CSV. Storage is one SQLite table holding the validated JSON:
summaries are write-once documents, so an ORM or a normalised schema would add nothing.
"""

import csv
import io
import os
import sqlite3
from contextlib import closing
from datetime import datetime
from uuid import UUID

from fastapi import FastAPI, HTTPException, Response, status
from pydantic import BaseModel, Field

MAX_POINTS = 20_000  # ~5.5 h of 1 Hz focus samples: far above a 25-minute session, bounds abuse


class FocusPoint(BaseModel):
    t_s: int = Field(ge=0)
    focus: int = Field(ge=0, le=100)


class Answer(BaseModel):
    pair_id: str = Field(min_length=1, max_length=300)
    correct: bool
    focus_at_answer: int | None = Field(default=None, ge=0, le=100)


class SessionSummary(BaseModel):
    """Mirrors SessionSummary.cs on the device (spec schema)."""

    session_id: UUID
    started_at: datetime
    duration_s: int = Field(ge=0)
    eeg_enabled: bool
    learn_time_s: int = Field(ge=0)
    test_time_s: int = Field(ge=0)
    accuracy_pct: float = Field(ge=0, le=100)
    focus_timeline: list[FocusPoint] = Field(default_factory=list, max_length=MAX_POINTS)
    answers: list[Answer] = Field(default_factory=list, max_length=MAX_POINTS)


CSV_COLUMNS = [
    "session_id",
    "started_at",
    "duration_s",
    "eeg_enabled",
    "learn_time_s",
    "test_time_s",
    "accuracy_pct",
    "record_type",
    "t_s",
    "focus",
    "pair_id",
    "correct",
    "focus_at_answer",
]


def db_path() -> str:
    # Read per call so tests (and deployments) can point at another file via the environment.
    default = os.path.join(os.path.dirname(__file__), "..", "neuroscholar.db")
    return os.environ.get("NEUROSCHOLAR_DB", default)


def connect() -> sqlite3.Connection:
    conn = sqlite3.connect(db_path())
    conn.execute("CREATE TABLE IF NOT EXISTS sessions (id TEXT PRIMARY KEY, body TEXT NOT NULL)")
    return conn


app = FastAPI(title="NeuroScholAR export", version="1.0.0")


@app.post("/sessions", status_code=status.HTTP_201_CREATED)
def create_session(summary: SessionSummary, response: Response) -> dict[str, str]:
    """Store a summary. Re-sending the same id is accepted (the app's outbox retries); last wins."""
    session_id = str(summary.session_id)
    with closing(connect()) as conn, conn:
        existed = conn.execute("SELECT 1 FROM sessions WHERE id = ?", (session_id,)).fetchone()
        conn.execute(
            "INSERT OR REPLACE INTO sessions (id, body) VALUES (?, ?)",
            (session_id, summary.model_dump_json()),
        )
    if existed:
        response.status_code = status.HTTP_200_OK
    return {"session_id": session_id}


@app.get("/sessions/{session_id}/export.csv")
def export_csv(session_id: UUID) -> Response:
    with closing(connect()) as conn:
        row = conn.execute("SELECT body FROM sessions WHERE id = ?", (str(session_id),)).fetchone()
    if row is None:
        raise HTTPException(status_code=404, detail="Session not found")
    summary = SessionSummary.model_validate_json(row[0])
    return Response(
        content=to_csv(summary),
        media_type="text/csv; charset=utf-8",
        headers={"Content-Disposition": f'attachment; filename="session-{session_id}.csv"'},
    )


def to_csv(summary: SessionSummary) -> str:
    """One tidy table: session fields on every row, then one row per focus sample or answer.

    A single flat file opens directly in Excel/pandas without joining two files.
    """
    base = [
        str(summary.session_id),
        summary.started_at.isoformat(),
        summary.duration_s,
        str(summary.eeg_enabled).lower(),
        summary.learn_time_s,
        summary.test_time_s,
        f"{summary.accuracy_pct:.1f}",
    ]
    out = io.StringIO()
    writer = csv.writer(out, lineterminator="\n")
    writer.writerow(CSV_COLUMNS)
    if not summary.focus_timeline and not summary.answers:
        writer.writerow([*base, "session", "", "", "", "", ""])
    for point in summary.focus_timeline:
        writer.writerow([*base, "focus", point.t_s, point.focus, "", "", ""])
    for answer in summary.answers:
        focus = "" if answer.focus_at_answer is None else answer.focus_at_answer
        correct = str(answer.correct).lower()
        writer.writerow([*base, "answer", "", "", answer.pair_id, correct, focus])
    return out.getvalue()
