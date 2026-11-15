# -*- coding: utf-8 -*-
"""Local document knowledge for DiagScope. Sources are cited by page and group."""
from __future__ import annotations
import argparse
import json
import re
import sqlite3
import sys
from pathlib import Path
from contextlib import closing

DEFAULT_DB = Path(r"C:\DiagScope\BazaWiedzy\diag_manuals.sqlite3")
SCHEMA = """
PRAGMA foreign_keys=ON;
CREATE TABLE IF NOT EXISTS sources(
  id INTEGER PRIMARY KEY,
  title TEXT NOT NULL,
  url TEXT NOT NULL UNIQUE,
  author TEXT NOT NULL,
  kind TEXT NOT NULL,
  applicability TEXT NOT NULL,
  page_count INTEGER NOT NULL,
  imported_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE TABLE IF NOT EXISTS sections(
  id INTEGER PRIMARY KEY,
  source_id INTEGER NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
  page INTEGER NOT NULL,
  group_number TEXT NOT NULL DEFAULT '',
  content TEXT NOT NULL,
  UNIQUE(source_id, page, group_number)
);
CREATE VIRTUAL TABLE IF NOT EXISTS sections_fts USING fts5(
  title, group_number, content,
  tokenize='unicode61 remove_diacritics 2'
);
"""

def connect(db: Path = DEFAULT_DB):
    db.parent.mkdir(parents=True, exist_ok=True)
    con=sqlite3.connect(str(db), timeout=20)
    con.row_factory=sqlite3.Row
    con.executescript(SCHEMA)
    return con

def sections_of_page(page_no: int, body: str):
    # Break on explicitly labeled measuring groups, never infer missing group numbers.
    matches=list(re.finditer(r"\bGrupa\s+(\d{3})\b", body, re.I))
    if not matches:
        return [(page_no, "", body.strip())]
    out=[]
    if body[:matches[0].start()].strip():
        out.append((page_no, "", body[:matches[0].start()].strip()))
    for i, found in enumerate(matches):
        end=matches[i+1].start() if i+1<len(matches) else len(body)
        out.append((page_no, found.group(1), body[found.start():end].strip()))
    return out

def import_json(path: Path, db: Path=DEFAULT_DB):
    payload=json.loads(path.read_text(encoding="utf-8-sig"))
    pages=payload["pages"]
    if len(pages)!=payload["page_count"] or len({x["page"] for x in pages})!=len(pages):
        raise ValueError("Niekompletny PDF albo powtarzajace sie numery stron")
    if not all(isinstance(x["text"], str) and x["text"].strip() for x in pages):
        raise ValueError("Brak tekstu ktorejs strony")
    with closing(connect(db)) as con:
        with con:
            source=con.execute("SELECT id FROM sources WHERE url=?", (payload["url"],)).fetchone()
            if source:
                # Reimport is idempotent, original source is left untouched.
                src_id=source["id"]
                n=con.execute("SELECT count(*) FROM sections WHERE source_id=?", (src_id,)).fetchone()[0]
                print(f"ALREADY_INDEXED source_id={src_id} sections={n}")
                return
            cur=con.execute("""INSERT INTO sources(title,url,author,kind,applicability,page_count)
                               VALUES(?,?,?,?,?,?)""",(
                payload["title"], payload["url"], payload["author"],
                payload["document_kind"], ", ".join(payload["applicable_engines"]),
                payload["page_count"]))
            src_id=cur.lastrowid
            count=0
            for p in pages:
                for number, group, text in sections_of_page(int(p["page"]),p["text"]):
                    row=con.execute(
                        "INSERT INTO sections(source_id,page,group_number,content) VALUES(?,?,?,?)",
                        (src_id,number,group,text))
                    con.execute(
                        "INSERT INTO sections_fts(rowid,title,group_number,content) VALUES(?,?,?,?)",
                        (row.lastrowid,payload["title"],group,text))
                    count+=1
    print(f"IMPORTED source_id={src_id} pages={len(pages)} sections={count}")

def search(query: str, db: Path=DEFAULT_DB, limit: int=12):
    with closing(connect(db)) as con:
        if re.fullmatch(r"\s*(?:grupa\s*)?\d{1,3}\s*", query, re.I):
            group=str(int(re.search(r"\d+",query).group())).zfill(3)
            rows=con.execute("""SELECT s.page,s.group_number,s.content,d.title,d.url,d.kind
                                FROM sections s JOIN sources d ON d.id=s.source_id
                                WHERE s.group_number=? ORDER BY s.page LIMIT ?""",(group,limit)).fetchall()
        else:
            tokens=re.findall(r"[\wąćęłńóśźżĄĆĘŁŃÓŚŹŻ]{2,}", query)
            if not tokens:
                return []
            escaped=['"'+t.replace('"','')+'"' for t in tokens[:8]]
            syntax=" OR ".join(escaped)
            rows=con.execute("""SELECT s.page,s.group_number,s.content,d.title,d.url,d.kind
                                FROM sections_fts f JOIN sections s ON s.id=f.rowid
                                JOIN sources d ON d.id=s.source_id
                                WHERE sections_fts MATCH ?
                                ORDER BY bm25(sections_fts),s.page LIMIT ?""",(syntax,limit)).fetchall()
        return [dict(r) for r in rows]

def gui(db: Path=DEFAULT_DB):
    import tkinter as tk
    from tkinter import ttk, scrolledtext, messagebox
    root=tk.Tk()
    root.title("DiagScope - wiedza techniczna z dokumentacji")
    root.geometry("1030x690")
    frame=ttk.Frame(root,padding=12);frame.pack(fill="both",expand=True)
    ttk.Label(frame,text="DiagScope: dokumentacja techniczna",font=("Segoe UI",16,"bold")).pack(anchor="w")
    ttk.Label(frame,text="Źródła pomocnicze. Wyniki zawsze weryfikuj dla konkretnego ECU i warunków pracy.").pack(anchor="w")
    line=ttk.Frame(frame);line.pack(fill="x",pady=9)
    var=tk.StringVar(value="031")
    entry=ttk.Entry(line,textvariable=var,width=48);entry.pack(side="left",fill="x",expand=True)
    result=scrolledtext.ScrolledText(frame,wrap="word",font=("Consolas",10))
    result.pack(fill="both",expand=True)
    def find(event=None):
        result.delete("1.0","end")
        try:
            found=search(var.get(),db)
            if not found:
                result.insert("end","Brak wyników. Spróbuj numeru grupy, np. 031, lub słowa 'lambda'.")
            for i,r in enumerate(found,1):
                result.insert("end",f"{i}. {r['title']}\nStrona: {r['page']}, grupa: {r['group_number'] or '(wstęp)'}\n{r['url']}\n")
                result.insert("end","Źródło: materiał społecznościowy (nie dokument producenta).\n")
                result.insert("end",r["content"][:3500]+"\n"+"-"*85+"\n\n")
        except Exception as exc:
            messagebox.showerror("Błąd wyszukiwania",str(exc),parent=root)
    ttk.Button(line,text="Szukaj",command=find).pack(side="left",padx=(8,0))
    entry.bind("<Return>",find)
    ttk.Label(frame,text="Wpisz numer grupy VCDS (001, 002, 031, 032, 033) lub hasło. Każdy wynik podaje stronę.").pack(anchor="w",pady=(6,0))
    find()
    root.mainloop()

def selftest():
    from tempfile import TemporaryDirectory
    with TemporaryDirectory() as folder:
        p=Path(folder)
        d={"title":"Próba","url":"local://sample","author":"test","document_kind":"test",
           "applicable_engines":["ALF"],"page_count":2,
           "pages":[{"page":1,"text":"Wstęp. Grupa 031 Napięcie sondy lambda 0-1 V. Grupa 033 Korekta sondy lambda"},
                    {"page":2,"text":"Grupa 002 Czas wtrysku w ms."}]}
        f=p/"source.json";f.write_text(json.dumps(d),encoding="utf-8")
        db=p/"knowledge.db"
        import_json(f,db);import_json(f,db)
        assert len(search("031",db))==1
        assert search("033",db)[0]["page"]==1
        assert search("wtrysku",db)[0]["group_number"]=="002"
        with closing(connect(db)) as con:
            assert con.execute("select count(*) from sources").fetchone()[0]==1
            assert con.execute("PRAGMA integrity_check").fetchone()[0]=="ok"
    print("SELFTEST=PASS")

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--db",type=Path,default=DEFAULT_DB)
    sub=parser.add_subparsers(dest="command",required=True)
    imp=sub.add_parser("import");imp.add_argument("source",type=Path)
    qry=sub.add_parser("search");qry.add_argument("query");qry.add_argument("--limit",type=int,default=8)
    sub.add_parser("stats");sub.add_parser("selftest");sub.add_parser("gui")
    opts=parser.parse_args()
    if opts.command=="selftest":selftest()
    elif opts.command=="import":import_json(opts.source,opts.db)
    elif opts.command=="search":
        for item in search(opts.query,opts.db,opts.limit):
            print(f"{item['title']} | str. {item['page']} | grupa {item['group_number']}\n{item['content'][:1200]}\nSOURCE: {item['url']}\n")
    elif opts.command=="gui":gui(opts.db)
    elif opts.command=="stats":
        with closing(connect(opts.db)) as con:
            print(f"SOURCES={con.execute('select count(*) from sources').fetchone()[0]}; PAGES={con.execute('select count(distinct source_id || \'-\' || page) from sections').fetchone()[0]}; SECTIONS={con.execute('select count(*) from sections').fetchone()[0]}; INTEGRITY={con.execute('PRAGMA integrity_check').fetchone()[0]}")

if __name__=="__main__":
    try:main()
    except Exception as exc:
        print(f"KNOWLEDGE_ERROR: {exc}",file=sys.stderr)
        raise