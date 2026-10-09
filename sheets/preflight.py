"""Preflight: lay every sheet over each other and list what is unfinished.

Reports empty cells, rows marked unverified/not done, and references
(sheet.rowId) that don't resolve. Exit code 1 if anything is open.
"""
import json
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).parent
REF = re.compile(r"\b([a-z_]+)\.([a-z0-9_*]+)\b")


def tables(sheet):
    """Yield (tableName, rows) for flat sheets and sheets with sub-tables."""
    if "rows" in sheet:
        yield sheet["sheet"], sheet["rows"]
    for key, val in sheet.items():
        if isinstance(val, dict) and "rows" in val:
            yield key, val["rows"]


sheets = {p.stem: json.loads(p.read_text()) for p in HERE.glob("*.json")}
ids = {}  # name -> set of row ids, for both sheet names and sub-table names
for name, sheet in sheets.items():
    for table, rows in tables(sheet):
        ids.setdefault(name, set()).update(r["id"] for r in rows)
        ids.setdefault(table, set()).update(r["id"] for r in rows)

subtables = {n: [(n, t) for t, _ in tables(s)] for n, s in sheets.items()}
problems = []
for name, sheet in sheets.items():
    for table, rows in tables(sheet):
        for row in rows:
            where = f"{name}/{table}/{row['id']}"
            for col, val in row.items():
                if val == "" or val is None:
                    problems.append(f"EMPTY      {where}.{col}")
                if col == "verified" and val is False:
                    problems.append(f"UNVERIFIED {where}")
                if col == "done" and val is False:
                    problems.append(f"NOT DONE   {where}")
                if col == "status" and val in ("waiting for user", "not run", "planned"):
                    problems.append(f"OPEN       {where} ({val})")
                if isinstance(val, str) and col not in ("bundle", "rustShortname", "rustPrefab", "map", "tagPath"):
                    for sheet_ref, row_ref in REF.findall(val):
                        if sheet_ref not in ids:
                            continue
                        if any(t == row_ref for _, t in subtables.get(sheet_ref, [])):
                            continue  # reference to a whole sub-table
                        if row_ref.endswith("*"):
                            ok = any(i.startswith(row_ref[:-1]) for i in ids[sheet_ref])
                        else:
                            ok = row_ref in ids[sheet_ref]
                        if not ok:
                            problems.append(f"BROKEN REF {where}.{col} -> {sheet_ref}.{row_ref}")

for p in problems:
    print(p)
print(f"\n{len(problems)} open item(s)")
sys.exit(1 if problems else 0)
