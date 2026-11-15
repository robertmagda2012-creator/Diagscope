# -*- coding: utf-8 -*-
"""DiagScope: local case memory. Analysis != confirmed repair."""
from __future__ import annotations

from contextlib import closing
import argparse
import hashlib
import json
import re
import sqlite3
import sys
from datetime import datetime
from pathlib import Path

DEFAULT_DB = Path(r"C:\DiagScope\BazaWiedzy\diag_cases.sqlite3")
SCHEMA = """
CREATE TABLE IF NOT EXISTS cases (
 id INTEGER PRIMARY KEY,
 log_sha TEXT NOT NULL,
 has_lpg INTEGER NOT NULL,
 first_seen TEXT NOT NULL,
 last_seen TEXT NOT NULL,
 log_path TEXT NOT NULL,
 vehicle TEXT NOT NULL DEFAULT '',
 symptom TEXT NOT NULL DEFAULT '',
 status TEXT NOT NULL DEFAULT 'DO SPRAWDZENIA',
 cause TEXT NOT NULL DEFAULT '',
 verification TEXT NOT NULL DEFAULT '',
 confirmed_at TEXT,
 UNIQUE(log_sha, has_lpg)
);
CREATE TABLE IF NOT EXISTS analyses (
 id INTEGER PRIMARY KEY,
 case_id INTEGER NOT NULL REFERENCES cases(id) ON DELETE CASCADE,
 created_at TEXT NOT NULL,
 report_path TEXT NOT NULL,
 report_sha TEXT NOT NULL UNIQUE,
 channels INTEGER NOT NULL,
 rows_count INTEGER NOT NULL,
 findings TEXT NOT NULL,
 next_tests TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS events (
 id INTEGER PRIMARY KEY,
 case_id INTEGER NOT NULL REFERENCES cases(id) ON DELETE CASCADE,
 timestamp TEXT NOT NULL,
 action TEXT NOT NULL,
 detail TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_cases_status ON cases(status);
CREATE INDEX IF NOT EXISTS ix_analyses_case ON analyses(case_id);
"""

def now() -> str:
    return datetime.now().astimezone().isoformat(timespec="seconds")

def db_connect(path: Path = DEFAULT_DB) -> sqlite3.Connection:
    path.parent.mkdir(parents=True, exist_ok=True)
    con = sqlite3.connect(str(path), timeout=15)
    con.row_factory = sqlite3.Row
    con.execute("PRAGMA foreign_keys=ON")
    con.execute("PRAGMA busy_timeout=15000")
    con.executescript(SCHEMA)
    return con

def sha256file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().upper()

def record(con: sqlite3.Connection, log_path: Path, report_path: Path, has_lpg: bool) -> tuple[int, bool]:
    if not log_path.is_file() or not report_path.is_file():
        raise FileNotFoundError("Nie znaleziono logu lub raportu.")
    report = json.loads(report_path.read_text(encoding="utf-8-sig"))
    if not isinstance(report, dict):
        raise ValueError("Niepoprawny format raportu.")
    hash_log = sha256file(log_path)
    claimed = str(report.get("Sha256") or "").upper()
    if claimed and claimed != hash_log:
        raise ValueError("Suma SHA256 logu nie zgadza sie z raportem; przerwano zapis.")
    findings = report.get("Findings") or []
    next_tests = report.get("NextTests") or []
    if not isinstance(findings, list) or not isinstance(next_tests, list):
        raise ValueError("Niepoprawne listy ustalen/testow.")
    hash_report = sha256file(report_path)
    timestamp = now()
    with con:
        con.execute(
            """INSERT INTO cases(log_sha, has_lpg, first_seen, last_seen, log_path)
               VALUES(?,?,?,?,?)
               ON CONFLICT(log_sha,has_lpg)
               DO UPDATE SET last_seen=excluded.last_seen, log_path=excluded.log_path""",
            (hash_log, int(has_lpg), timestamp, timestamp, str(log_path))
        )
        row = con.execute("SELECT id FROM cases WHERE log_sha=? AND has_lpg=?",
                          (hash_log, int(has_lpg))).fetchone()
        case_id = int(row["id"])
        cur = con.execute(
            """INSERT OR IGNORE INTO analyses
                (case_id,created_at,report_path,report_sha,channels,rows_count,findings,next_tests)
                VALUES(?,?,?,?,?,?,?,?)""",
            (case_id, timestamp, str(report_path), hash_report,
             int(report.get("Channels") or 0), int(report.get("Rows") or 0),
             json.dumps(findings, ensure_ascii=False),
             json.dumps(next_tests, ensure_ascii=False))
        )
        inserted = cur.rowcount > 0
        if inserted:
            con.execute("INSERT INTO events(case_id,timestamp,action,detail) VALUES(?,?,?,?)",
                        (case_id, timestamp, "ANALIZA", "Zapisano raport bez potwierdzania diagnozy"))
    return case_id, inserted

def annotate(con: sqlite3.Connection, case_id: int, vehicle: str, symptom: str) -> None:
    with con:
        cur = con.execute("UPDATE cases SET vehicle=?,symptom=? WHERE id=?",
                          (vehicle.strip()[:200], symptom.strip()[:500], case_id))
        if not cur.rowcount:
            raise ValueError("Nie ma takiego przypadku.")
        con.execute("INSERT INTO events(case_id,timestamp,action,detail) VALUES(?,?,?,?)",
                    (case_id, now(), "OPIS", "Uzytkownik zmienil opis pojazdu/objawu"))

def confirm(con: sqlite3.Connection, case_id: int, cause: str, verification: str) -> None:
    if len(cause.strip()) < 5 or len(verification.strip()) < 5:
        raise ValueError("Podaj przyczyne i sposob potwierdzenia (min. 5 znakow).")
    stamp = now()
    with con:
        cur = con.execute(
            """UPDATE cases SET status='NAPRAWA POTWIERDZONA',
               cause=?, verification=?, confirmed_at=? WHERE id=?""",
            (cause.strip()[:2000], verification.strip()[:2000], stamp, case_id))
        if not cur.rowcount:
            raise ValueError("Nie ma takiego przypadku.")
        con.execute("INSERT INTO events(case_id,timestamp,action,detail) VALUES(?,?,?,?)",
                    (case_id, stamp, "POTWIERDZENIE", "Zatwierdzono naprawe przez uzytkownika"))

def matches(con: sqlite3.Connection, case_id: int, limit: int = 8) -> list[tuple[int, int, str, str]]:
    current = con.execute("SELECT * FROM cases WHERE id=?", (case_id,)).fetchone()
    if not current:
        return []
    def words(s: str) -> set[str]:
        return {w for w in re.findall(r"[\w-]{3,}", s.casefold())
                if w not in {"auto", "samochod", "usterka", "silnik", "blad", "brak", "inne", "test"}}
    current_words = words(current["vehicle"] + " " + current["symptom"])
    if not current_words:
        return []
    out = []
    rows = con.execute(
        "SELECT * FROM cases WHERE status='NAPRAWA POTWIERDZONA' AND id<>?", (case_id,))
    for row in rows:
        shared = current_words & words(row["vehicle"] + " " + row["symptom"])
        if shared:
            out.append((len(shared), int(row["id"]), row["vehicle"], row["cause"]))
    out.sort(reverse=True)
    return out[:limit]

def gui(db_path: Path) -> None:
    import tkinter as tk
    from tkinter import ttk, messagebox, simpledialog, scrolledtext

    con = db_connect(db_path)
    root = tk.Tk()
    root.title("DiagScope - lokalna baza wiedzy")
    root.geometry("1050x680")
    container = ttk.Frame(root, padding=12)
    container.pack(fill="both", expand=True)
    ttk.Label(container, text="Pamięć diagnostyczna DiagScope", font=("Segoe UI", 16, "bold")).pack(anchor="w")
    ttk.Label(container, text="Analizy są zapamiętywane automatycznie. Naprawę potwierdza wyłącznie użytkownik.").pack(anchor="w", pady=(2, 8))

    cols = ("id", "when", "vehicle", "log", "lpg", "status")
    table = ttk.Treeview(container, columns=cols, show="headings", height=11)
    widths = (60, 145, 170, 360, 55, 180)
    labels = ("ID", "Ostatnia analiza", "Pojazd", "Plik logu", "LPG", "Status")
    for c, w, label in zip(cols, widths, labels):
        table.heading(c, text=label)
        table.column(c, width=w, stretch=(c == "log"))
    table.pack(fill="both", expand=False)
    details = scrolledtext.ScrolledText(container, height=14, font=("Consolas", 10), wrap="word")
    details.pack(fill="both", expand=True, pady=(8, 6))

    def selected_id() -> int | None:
        sel = table.selection()
        return int(sel[0]) if sel else None

    def refresh(select: int | None = None) -> None:
        for item in table.get_children():
            table.delete(item)
        for row in con.execute("SELECT * FROM cases ORDER BY last_seen DESC,id DESC"):
            table.insert("", "end", iid=str(row["id"]), values=(
                row["id"], row["last_seen"][:16].replace("T", " "),
                row["vehicle"] or "(nieopisany)", Path(row["log_path"]).name,
                "tak" if row["has_lpg"] else "nie", row["status"]))
        if select is not None and table.exists(str(select)):
            table.selection_set(str(select))
        display()

    def display(event=None) -> None:
        details.delete("1.0", "end")
        cid = selected_id()
        if cid is None:
            details.insert("end", "Wybierz przypadek z listy. Nowe analizy będą dodawane po uruchomieniu agenta.\n")
            return
        row = con.execute("SELECT * FROM cases WHERE id=?", (cid,)).fetchone()
        data = con.execute("SELECT * FROM analyses WHERE case_id=? ORDER BY id DESC LIMIT 1", (cid,)).fetchone()
        fields = [
            f"Przypadek #{cid} | {row['status']}",
            f"Pojazd: {row['vehicle'] or '(uzupełnij opis)'}",
            f"Objawy: {row['symptom'] or '(uzupełnij opis)'}",
            f"Log: {row['log_path']}",
            f"SHA256: {row['log_sha']}",
            f"Potwierdzona przyczyna: {row['cause'] or 'BRAK'}",
            f"Weryfikacja po naprawie: {row['verification'] or 'BRAK'}",
            ""
        ]
        if data:
            findings = json.loads(data["findings"])
            tests = json.loads(data["next_tests"])
            fields += [f"Ostatni raport: {data['report_path']}",
                       f"Wiersze: {data['rows_count']}; kanały: {data['channels']}",
                       f"Ustalenia ({len(findings)}):"]
            for finding in findings[:12]:
                fields.append(f" - [{finding.get('Status','')}] {finding.get('Title','')}")
            fields.append("")
            fields.append("Zalecane testy:")
            for test in tests[:10]:
                fields.append(f" - {test.get('Title','')}")
        fields += ["", "Ustalenia w raporcie NIE oznaczają potwierdzonej przyczyny awarii."]
        details.insert("end", "\n".join(fields))

    def edit() -> None:
        cid = selected_id()
        if cid is None:
            return
        row = con.execute("SELECT * FROM cases WHERE id=?", (cid,)).fetchone()
        vehicle = simpledialog.askstring("Opis pojazdu", "Pojazd (np. Audi A6 C5 2.4 ALF):",
                                         initialvalue=row["vehicle"], parent=root)
        if vehicle is None:
            return
        symptom = simpledialog.askstring("Objawy", "Objawy i kody DTC:",
                                         initialvalue=row["symptom"], parent=root)
        if symptom is None:
            return
        annotate(con, cid, vehicle, symptom)
        refresh(cid)

    def mark_confirmed() -> None:
        cid = selected_id()
        if cid is None:
            return
        cause = simpledialog.askstring("Potwierdzenie diagnozy", "Co było rzeczywistą przyczyną usterki?",
                                       parent=root)
        if not cause:
            return
        verification = simpledialog.askstring("Dowód naprawy", "Jak sprawdzono, że problem został usunięty?",
                                              parent=root)
        if not verification:
            return
        if not messagebox.askyesno("Potwierdź", "Czy naprawa i jej wynik zostały faktycznie zweryfikowane?",
                                   parent=root):
            return
        try:
            confirm(con, cid, cause, verification)
            refresh(cid)
        except ValueError as exc:
            messagebox.showerror("Błąd", str(exc), parent=root)

    def similar() -> None:
        cid = selected_id()
        if cid is None:
            return
        found = matches(con, cid)
        if not found:
            messagebox.showinfo("Podobne przypadki", "Brak podobnych, potwierdzonych napraw w lokalnej bazie.\n"
                                "Najpierw opisz pojazd i objawy, a po naprawie potwierdź przyczynę.", parent=root)
            return
        result = "\n\n".join(f"#{key}: {vehicle}\nPodobieństwo słów: {score}\nNaprawa: {cause}"
                             for score, key, vehicle, cause in found)
        messagebox.showinfo("Podobne potwierdzone naprawy", result[:3500], parent=root)

    btns = ttk.Frame(container)
    btns.pack(fill="x")
    ttk.Button(btns, text="Odśwież", command=refresh).pack(side="left", padx=(0, 8))
    ttk.Button(btns, text="Opisz pojazd i objawy", command=edit).pack(side="left", padx=(0, 8))
    ttk.Button(btns, text="Potwierdź naprawę", command=mark_confirmed).pack(side="left", padx=(0, 8))
    ttk.Button(btns, text="Podobne potwierdzone", command=similar).pack(side="left")
    table.bind("<<TreeviewSelect>>", display)
    root.protocol("WM_DELETE_WINDOW", lambda: (con.close(), root.destroy()))
    refresh()
    root.mainloop()

def selftest() -> None:
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        root = Path(d)
        log = root / "log.csv"
        log.write_text("czas,obr\n1,800\n", encoding="utf-8")
        report = root / "raport.json"
        report.write_text(json.dumps({"Sha256":sha256file(log),"Channels":1,"Rows":1,
                                      "Findings":[{"Status":"HIPOTEZA","Title":"Test"}],
                                      "NextTests":[]}), encoding="utf-8")
        with closing(db_connect(root / "test.sqlite3")) as con:
            id1, inserted1 = record(con, log, report, True)
            id2, inserted2 = record(con, log, report, True)
            assert id1 == id2 and inserted1 and not inserted2
            assert con.execute("SELECT status FROM cases WHERE id=?", (id1,)).fetchone()[0] == "DO SPRAWDZENIA"
            annotate(con, id1, "Audi A6 C5", "P0130")
            confirm(con, id1, "Uszkodzony przewod testowy", "Kontrola przed i po naprawie")
            assert con.execute("SELECT status FROM cases WHERE id=?", (id1,)).fetchone()[0] == "NAPRAWA POTWIERDZONA"
            assert con.execute("SELECT count(*) FROM analyses").fetchone()[0] == 1
            assert con.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    print("SELFTEST=PASS")

def main() -> None:
    parser = argparse.ArgumentParser(description="Lokalna baza przypadkow DiagScope")
    parser.add_argument("--db", type=Path, default=DEFAULT_DB)
    sub = parser.add_subparsers(dest="action", required=True)
    rec = sub.add_parser("record", help="Zapisz wyniki agenta bez potwierdzania naprawy")
    rec.add_argument("log", type=Path)
    rec.add_argument("report", type=Path)
    rec.add_argument("--lpg", action="store_true")
    sub.add_parser("gui")
    sub.add_parser("stats")
    sub.add_parser("selftest")
    args = parser.parse_args()
    if args.action == "selftest":
        selftest()
    elif args.action == "gui":
        gui(args.db)
    elif args.action == "record":
        with closing(db_connect(args.db)) as con:
            case_id, added = record(con, args.log, args.report, args.lpg)
            print(f"MEMORY_CASE_ID={case_id}; REPORT_ADDED={added}; DB={args.db}")
    elif args.action == "stats":
        with closing(db_connect(args.db)) as con:
            count = con.execute("SELECT count(*) FROM cases").fetchone()[0]
            confirmed = con.execute("SELECT count(*) FROM cases WHERE status='NAPRAWA POTWIERDZONA'").fetchone()[0]
            analyses = con.execute("SELECT count(*) FROM analyses").fetchone()[0]
            health = con.execute("PRAGMA integrity_check").fetchone()[0]
            print(f"CASES={count}; ANALYSES={analyses}; CONFIRMED_REPAIRS={confirmed}; INTEGRITY={health}")

if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"MEMORY_ERROR: {exc}", file=sys.stderr)
        raise