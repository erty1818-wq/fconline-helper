"""Collect player list data from the official FC Online data center for price analysis (personal use).

The list endpoint (POST /datacenter/PlayerList) returns at most 200 players per query and has no paging, so every
query is a narrow slice: position group x one OVR value (x salary band when a slice is full). Each row carries the
price at every enhancement grade, weak foot, salary, OVR per position and four chosen stats. Traits, skill moves and
body type are not in the row, so they are tagged with filtered queries.

Politeness: one request every DELAY seconds, and every finished query is recorded in data/done.txt so a rerun
resumes instead of starting over. Data goes to data/ (git-ignored; the API terms ask for refreshes within 30 days).

    python harvest.py            # everything below that is not done yet
    python harvest.py --groups ST W --only tags
"""
import argparse, json, re, sys, time, urllib.parse, urllib.request
from pathlib import Path

DATA = Path(__file__).parent / "data"
URL = "https://fconline.nexon.com/datacenter/PlayerList"
HEADERS = {"X-Requested-With": "XMLHttpRequest", "User-Agent": "FcHelper/0.5 (personal use)",
           "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8"}
DELAY = 2.0
OVR_RANGE = range(105, 131)  # OVR at +1; below this the market is mostly floor prices
SALARY_BANDS = [(4, 14), (15, 18), (19, 21), (22, 24), (25, 27), (28, 99)]

ATTACK_TRAITS = ["라인 브레이커", "트릭스터", "스피드스터", "타이탄", "프레데터", "레이저 슈터", "아크로바틱 피니셔",
                 "크로스 포쳐", "파이터", "2개의 심장"]
MID_TRAITS = ["2개의 심장", "파이터", "체이서", "블로커", "커맨더", "타이탄", "레이저 슈터", "스피드스터", "트릭스터"]
DEF_TRAITS = ["블로커", "와일드 태클러", "커맨더", "체이서", "타이탄", "파이터", "스피드스터"]
GK_TRAITS = ["GK 데드아이", "GK 빠른 반응", "GK 공중볼 장악"]

# Position codes are the API's spposition values, as the data center's own position filter sends them.
GROUPS = {
    "ST":  dict(pos=",24,25,26,", name="스트라이커", traits=ATTACK_TRAITS,
                stats=[["sprintspeed", "acceleration", "strength", "finishing"], ["dribbling", "agility", "shotpower", "composure"]]),
    "W":   dict(pos=",23,27,", name="윙어", traits=ATTACK_TRAITS,
                stats=[["sprintspeed", "acceleration", "strength", "finishing"], ["dribbling", "agility", "shotpower", "composure"]]),
    "CF":  dict(pos=",20,21,22,", name="중앙 공격수(CF)", traits=ATTACK_TRAITS,
                stats=[["sprintspeed", "acceleration", "strength", "finishing"], ["dribbling", "agility", "shotpower", "composure"]]),
    "SM":  dict(pos=",12,16,", name="측면 미드필더", traits=ATTACK_TRAITS,
                stats=[["sprintspeed", "acceleration", "crossing", "dribbling"], ["stamina", "agility", "shortpassing", "composure"]]),
    "CAM": dict(pos=",17,18,19,", name="공격형 미드필더", traits=ATTACK_TRAITS,
                stats=[["sprintspeed", "acceleration", "dribbling", "shortpassing"], ["agility", "vision", "longshots", "composure"]]),
    "CM":  dict(pos=",13,14,15,", name="중앙 미드필더", traits=MID_TRAITS,
                stats=[["shortpassing", "longpassing", "vision", "stamina"], ["strength", "interceptions", "composure", "sprintspeed"]]),
    "CDM": dict(pos=",9,10,11,", name="수비형 미드필더", traits=MID_TRAITS,
                stats=[["interceptions", "standingtackle", "strength", "shortpassing"], ["stamina", "marking", "sprintspeed", "composure"]]),
    "CB":  dict(pos=",1,4,5,6,", name="센터백", traits=DEF_TRAITS,
                stats=[["sprintspeed", "strength", "marking", "standingtackle"], ["headingaccuracy", "jumping", "interceptions", "acceleration"]]),
    "FB":  dict(pos=",2,3,7,8,", name="풀백", traits=DEF_TRAITS,
                stats=[["sprintspeed", "acceleration", "stamina", "crossing"], ["standingtackle", "marking", "interceptions", "strength"]]),
    "GK":  dict(pos=",0,", name="골키퍼", traits=GK_TRAITS,
                stats=[["gkdiving", "gkhandling", "gkreflexes", "gkpositioning"], ["gkkicking", "reactions", "height", "composure"]]),
}
BODY = {"thin": ",1,4,7,11,", "heavy": ",3,6,9,13,"}  # 마름 / 건장; the rest are 보통
BODY_GROUPS = ["ST", "W"]
SKILL_TAGS = [5, 6]  # the filter is an exact star count

requests_made = 0


def log(msg):
    line = f"{time.strftime('%H:%M:%S')} [{requests_made}] {msg}"
    print(line, flush=True)
    with open(DATA / "harvest.log", "a", encoding="utf-8") as f:
        f.write(line + "\n")


def done_keys():
    p = DATA / "done.txt"
    return set(p.read_text(encoding="utf-8").split("\n")) if p.exists() else set()


def mark_done(key):
    with open(DATA / "done.txt", "a", encoding="utf-8") as f:
        f.write(key + "\n")


def query(pos, ovr_min, ovr_max, stats, pay=(4, 99), trait="", skill=0, body=""):
    global requests_made
    form = {
        "n8PlayerGrade1Min": 0, "n8PlayerGrade1Max": 999900000, "n1Confederation": 0, "n4LeagueId": 0, "strSeason": "",
        "strPosition": pos, "strPhysical": body, "preferredfoot": 0, "n1FootAblity": 0, "n1SkillMove": skill,
        "n1InterationalRep": 0, "n4BirthMonth": 0, "n4BirthDay": 0, "n4TeamId": 0, "n4NationId": 0,
        "strAbility1": "", "strAbility2": "", "strAbility3": "", "strTrait1": trait, "strTrait2": "", "strTrait3": "",
        "strTraitNon1": "", "strTraitNon2": "", "strTraitNon3": "", "n1Strong": 1, "n1Grow": 0, "n1TeamColor": 0,
        "strSkill1": stats[0], "strSkill2": stats[1], "strSkill3": stats[2], "strSkill4": stats[3],
        "strSearchStatus": "off", "strOrderby": "", "teamcolorid": 0, "strTeamColorCategory": "", "n1History": 0,
        "n4PlayYear": 0, "IsSummaryPlayer": 0, "strPlayerName": "", "strTeamName": "", "strNationName": "",
        "strTeamColorName": "", "n4OvrMin": ovr_min, "n4OvrMax": ovr_max, "n4SalaryMin": pay[0], "n4SalaryMax": pay[1],
        "n1Ability1Min": 40, "n1Ability1Max": 200, "n1Ability2Min": 40, "n1Ability2Max": 200, "n1Ability3Min": 40,
        "n1Ability3Max": 200, "n4BirthYearMin": 1900, "n4BirthYearMax": 2010, "n4HeightMin": 140, "n4HeightMax": 250,
        "n4WeightMin": 40, "n4WeightMax": 200, "n4AvgPointMin": 0, "n4AvgPointMax": 10, "n4PageNo": 1,
    }
    if requests_made:
        time.sleep(DELAY)
    requests_made += 1
    data = urllib.parse.urlencode(form).encode()
    for attempt in range(4):
        try:
            req = urllib.request.Request(URL, data=data, headers=HEADERS, method="POST")
            with urllib.request.urlopen(req, timeout=90) as res:
                return parse_rows(res.read().decode("utf-8"))
        except Exception as e:  # noqa: BLE001 - network hiccups: back off, then give up
            if attempt == 3:
                raise
            log(f"retry after {e!r}")
            time.sleep(15 * (attempt + 1))


RE = {k: re.compile(v, re.S) for k, v in {
    "spid": r"^(\d+)", "season": r"/season/([A-Za-z0-9_]+)\.png", "name": r'<div class="name">([^<]+)</div>',
    "pay": r'<span class="pay">(\d+)</span>', "foot": r'class="foot">(.*?)</div>',
    "pos": r'<span class="txt">(\w+)</span><span class="skillData_\d+">(\d+)</span>',
    "stat": r'data-type="(\w+)">\s*(\d+)', "price": r'span_bp(\d+)"[^>]*title="([\d,]+)"',
    "score": r'td_ar_score"><span>([\d.]+)\s*<em>\((\d+)\)',
}.items()}


def parse_rows(html):
    rows = []
    for c in html.split('<div id="area_playerunit_')[1:]:
        foot = (RE["foot"].search(c) or [None, ""])[1]
        fl, fr = re.search(r"L(\d)", foot), re.search(r"R(\d)", foot)
        score = RE["score"].search(c)
        rows.append({
            "spid": int(RE["spid"].search(c)[1]),
            "season": (RE["season"].search(c) or [None, ""])[1],
            "name": (RE["name"].search(c) or [None, ""])[1].strip(),
            "pay": int((RE["pay"].search(c) or [None, 0])[1]),
            "footL": int(fl[1]) if fl else 0, "footR": int(fr[1]) if fr else 0,
            "positions": {m[0]: int(m[1]) for m in RE["pos"].findall(c)},
            "stats": {m[0]: int(m[1]) for m in RE["stat"].findall(c)},
            "prices": {m[0]: int(m[1].replace(",", "")) for m in RE["price"].findall(c)},
            "rating": float(score[1]) if score else None, "ratingCount": int(score[2]) if score else 0,
        })
    return rows


def append(name, rows, **tags):
    with open(DATA / name, "a", encoding="utf-8") as f:
        for r in rows:
            f.write(json.dumps({**r, **tags}, ensure_ascii=False) + "\n")


def harvest_lists(group, cfg, done):
    for pass_no, stats in enumerate(cfg["stats"]):
        for ovr in OVR_RANGE:
            key = f"list|{group}|{pass_no}|{ovr}"
            if key in done: continue
            rows = query(cfg["pos"], ovr, ovr, stats)
            if len(rows) >= 200:
                rows = []
                for band in SALARY_BANDS:
                    part = query(cfg["pos"], ovr, ovr, stats, pay=band)
                    if len(part) >= 200: log(f"WARNING {key} pay {band} still full; some players missing")
                    rows += part
            append("list.jsonl", rows, group=group, stats_pass=pass_no)
            mark_done(key)
            log(f"{key}: {len(rows)}")


def tag_query(group, cfg, key, tag, done, **filters):
    """Membership query over the whole OVR range, split in halves while a slice is full."""
    if key in done: return
    found, stack = [], [(OVR_RANGE.start, OVR_RANGE.stop - 1)]
    while stack:
        lo, hi = stack.pop()
        rows = query(cfg["pos"], lo, hi, cfg["stats"][0], **filters)
        if len(rows) >= 200 and lo < hi:
            mid = (lo + hi) // 2
            stack += [(lo, mid), (mid + 1, hi)]
        else:
            if len(rows) >= 200: log(f"WARNING {key} OVR {lo} still full")
            found += rows
    append("tags.jsonl", [{"spid": r["spid"]} for r in found], group=group, tag=tag)
    mark_done(key)
    log(f"{key}: {len(found)}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--groups", nargs="*", default=list(GROUPS))
    ap.add_argument("--only", choices=["lists", "tags"])
    args = ap.parse_args()
    DATA.mkdir(exist_ok=True)
    done = done_keys()
    for g in args.groups:
        cfg = GROUPS[g]
        if args.only != "tags":
            harvest_lists(g, cfg, done)
        if args.only != "lists":
            for t in cfg["traits"]:
                tag_query(g, cfg, f"trait|{g}|{t}", f"trait:{t}", done, trait=t)
            for s in SKILL_TAGS:
                tag_query(g, cfg, f"skill|{g}|{s}", f"skill:{s}", done, skill=s)
            if g in BODY_GROUPS:
                for b, code in BODY.items():
                    tag_query(g, cfg, f"body|{g}|{b}", f"body:{b}", done, body=code)
    log("finished")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    main()
