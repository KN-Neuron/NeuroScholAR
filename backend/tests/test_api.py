import csv
import io
import uuid

import pytest
from fastapi.testclient import TestClient

from app.main import CSV_COLUMNS, app


@pytest.fixture(autouse=True)
def isolated_db(tmp_path, monkeypatch):
    monkeypatch.setenv("NEUROSCHOLAR_DB", str(tmp_path / "test.db"))


@pytest.fixture
def client():
    return TestClient(app)


def summary(**overrides):
    body = {
        "session_id": str(uuid.uuid4()),
        "started_at": "2026-10-17T18:00:00+00:00",
        "duration_s": 600,
        "eeg_enabled": True,
        "learn_time_s": 240,
        "test_time_s": 360,
        "accuracy_pct": 75.0,
        "focus_timeline": [{"t_s": 0, "focus": 50}, {"t_s": 1, "focus": 62}],
        "answers": [
            {"pair_id": "p1", "correct": True, "focus_at_answer": 70},
            {"pair_id": "p2", "correct": False, "focus_at_answer": None},
        ],
    }
    body.update(overrides)
    return body


def read_csv(text):
    return list(csv.DictReader(io.StringIO(text)))


def test_post_then_export_csv(client):
    body = summary()
    created = client.post("/sessions", json=body)
    assert created.status_code == 201
    assert created.json() == {"session_id": body["session_id"]}

    exported = client.get(f"/sessions/{body['session_id']}/export.csv")
    assert exported.status_code == 200
    assert exported.headers["content-type"].startswith("text/csv")
    assert "attachment" in exported.headers["content-disposition"]

    assert exported.text.splitlines()[0] == ",".join(CSV_COLUMNS)
    rows = read_csv(exported.text)
    assert [r["record_type"] for r in rows] == ["focus", "focus", "answer", "answer"]
    assert rows[1]["focus"] == "62"
    assert rows[2]["pair_id"] == "p1" and rows[2]["correct"] == "true"
    assert rows[3]["focus_at_answer"] == ""  # no EEG at that answer
    assert all(r["session_id"] == body["session_id"] for r in rows)
    assert all(r["accuracy_pct"] == "75.0" and r["eeg_enabled"] == "true" for r in rows)


def test_resend_is_idempotent_and_updates(client):
    body = summary()
    assert client.post("/sessions", json=body).status_code == 201
    body["accuracy_pct"] = 80.0
    assert client.post("/sessions", json=body).status_code == 200
    rows = read_csv(client.get(f"/sessions/{body['session_id']}/export.csv").text)
    assert rows[0]["accuracy_pct"] == "80.0"


def test_session_without_eeg_or_answers_still_exports_one_row(client):
    body = summary(eeg_enabled=False, focus_timeline=[], answers=[])
    client.post("/sessions", json=body)
    rows = read_csv(client.get(f"/sessions/{body['session_id']}/export.csv").text)
    assert len(rows) == 1
    assert rows[0]["record_type"] == "session"
    assert rows[0]["eeg_enabled"] == "false"


def test_unknown_session_is_404(client):
    assert client.get(f"/sessions/{uuid.uuid4()}/export.csv").status_code == 404


def test_bad_id_is_422(client):
    assert client.get("/sessions/not-a-uuid/export.csv").status_code == 422


@pytest.mark.parametrize(
    "overrides",
    [
        {"session_id": "nope"},
        {"duration_s": -1},
        {"accuracy_pct": 101},
        {"focus_timeline": [{"t_s": 0, "focus": 150}]},
        {"answers": [{"pair_id": "", "correct": True}]},
        {"started_at": "yesterday"},
    ],
)
def test_invalid_summary_is_rejected(client, overrides):
    assert client.post("/sessions", json=summary(**overrides)).status_code == 422


def test_missing_field_is_rejected(client):
    body = summary()
    del body["eeg_enabled"]
    assert client.post("/sessions", json=body).status_code == 422


def test_csv_escapes_commas_and_quotes(client):
    body = summary(answers=[{"pair_id": 'a,"b"', "correct": True, "focus_at_answer": 10}])
    client.post("/sessions", json=body)
    rows = read_csv(client.get(f"/sessions/{body['session_id']}/export.csv").text)
    assert rows[-1]["pair_id"] == 'a,"b"'
