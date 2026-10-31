"""MV-17: the data center's daily +11 price of each card (POST /datacenter/PlayerPriceGraph). The price only moves on days a card sold,
so the number of changes says whether its price is real. One request per two seconds, most used and highest cards first, saved as it
goes, so it can be stopped and resumed.
    python tools/market/mv17_harvest.py --db <copy of fchelper.db> --out artifacts/mv17_graph.json
"""
import argparse, json, os, re, sys, time, urllib.parse, urllib.request
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
from mv12 import load
from mv12_pool import unique_cards

VALUE = re.compile(r'"value"\s*:\s*\[(.*?)\]', re.S)


def fetch(sp_id, grade):
    body = urllib.parse.urlencode({"spid": sp_id, "n1strong": grade}).encode()
    req = urllib.request.Request("https://fconline.nexon.com/datacenter/PlayerPriceGraph", data=body,
                                 headers={"X-Requested-With": "XMLHttpRequest", "User-Agent": "FcHelper/0.5 (personal use)"})
    with urllib.request.urlopen(req, timeout=30) as res:
        html = res.read().decode("utf-8", "replace")
    at = html.find("json1")
    m = VALUE.search(html, at) if at >= 0 else None
    if not m: return []
    return [int(v.strip().strip('"')) for v in m.group(1).split(",") if v.strip().strip('"').lstrip("-").isdigit()]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--db", default=os.path.expandvars(r"%LOCALAPPDATA%\FcHelper\fchelper.db"))
    ap.add_argument("--out", required=True)
    ap.add_argument("--grade", type=int, default=11)
    ap.add_argument("--limit", type=int, default=0)
    a = ap.parse_args()
    m = load(a.db)
    data = unique_cards(m, a.grade, 115)
    use = m["use_card"]
    order = sorted(data, key=lambda c: (-use[c["sp_id"]], -c["ovr1"]))
    if a.limit: order = order[:a.limit]
    path = Path(a.out)
    done = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    fails = 0
    for i, c in enumerate(order):
        key = str(c["sp_id"])
        if key in done: continue
        started = time.time()
        try:
            done[key] = fetch(c["sp_id"], a.grade)
            fails = 0
        except OSError:
            fails += 1
            if fails >= 5: break
            time.sleep(30)
            continue
        if len(done) % 20 == 0: path.write_text(json.dumps(done), encoding="utf-8")
        time.sleep(max(0, 2 - (time.time() - started)))
    path.write_text(json.dumps(done), encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
