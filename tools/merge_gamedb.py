"""Merges Data/research/*.json (one file per emulator) into Data/gamedb.json."""
import datetime
import json
import pathlib

root = pathlib.Path(__file__).resolve().parent.parent
research = root / "Data" / "research"
games = []
for f in sorted(research.glob("*.json")):
    data = json.loads(f.read_text(encoding="utf-8"))
    emu = data.get("emulator") or f.stem
    for g in data.get("games", []):
        g["emulator"] = g.get("emulator") or emu
        g.setdefault("settings", {})
        g.setdefault("tierSettings", {})
        games.append(g)
    print(f"{f.name}: {len(data.get('games', []))} games")

out = {"version": datetime.date.today().strftime("%Y.%m.%d"), "games": games}
(root / "Data" / "gamedb.json").write_text(json.dumps(out, indent=2, ensure_ascii=False), encoding="utf-8")
print(f"gamedb.json: {len(games)} games")
