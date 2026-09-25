"""Shared loading and the price model for analyze.py and value.py (standard library only).

Model per position group and enhancement grade (hedonic regression):
    ln(price) ~ OVR + OVR^2 + weak foot + salary + (stat - OVR) per collected stat + tags + season
Tags are traits, 5/6-star skill moves and body type. Seasons with few cards share one "other" term.
A coefficient reads as "everything else equal, this changes the price by X%". This is association in today's
market, not a law: a tag can also stand in for things the model does not see.
"""
import json, math, re, statistics
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path

from harvest import DATA, GROUPS

# OVR added by each enhancement grade, from the data center's grade selector. The list shows OVR at +1 (bonus 3).
GRADE_BONUS = {1: 3, 2: 4, 3: 5, 4: 7, 5: 9, 6: 11, 7: 14, 8: 18, 9: 20, 10: 22, 11: 24, 12: 27, 13: 30}
FLOOR = 1200  # prices at or below this are the market floor, not a valuation
STAT_KO = {
    "sprintspeed": "속력", "acceleration": "가속력", "strength": "몸싸움", "finishing": "골 결정력", "dribbling": "드리블",
    "agility": "민첩성", "shotpower": "슛 파워", "composure": "침착성", "crossing": "크로스", "stamina": "스태미너",
    "shortpassing": "짧은 패스", "longpassing": "긴 패스", "vision": "시야", "longshots": "중거리 슛",
    "interceptions": "가로채기", "standingtackle": "태클", "marking": "대인 수비", "headingaccuracy": "헤더",
    "jumping": "점프", "gkdiving": "GK 다이빙", "gkhandling": "GK 핸들링", "gkreflexes": "GK 반응속도",
    "gkpositioning": "GK 위치 선정", "gkkicking": "GK 킥", "reactions": "반응 속도", "height": "키(cm)",
}
TAG_KO = {"skill:5": "개인기 5성", "skill:6": "개인기 6성", "body:thin": "체형 마름", "body:heavy": "체형 건장"}
ABSOLUTE_STATS = {"height"}  # not measured on the OVR scale


@dataclass
class Player:
    spid: int
    group: str
    name: str
    season: str
    pay: int
    ovr1: int
    weak_foot: int
    prices: dict
    stats: dict
    rating: float | None
    rating_count: int
    tags: set = field(default_factory=set)

    def ovr_at(self, grade):
        return self.ovr1 - GRADE_BONUS[1] + GRADE_BONUS[grade]

    def price_at(self, grade):
        return self.prices.get(str(grade), 0)

    @property
    def traded(self):
        return len(set(self.prices.values())) > 1


def load(groups=None):
    """Players per group, merging the stat passes and tags."""
    players = {}
    for line in open(DATA / "list.jsonl", encoding="utf-8"):
        r = json.loads(line)
        g = r["group"]
        if groups and g not in groups or not r["positions"]:
            continue
        key = (g, r["spid"])
        p = players.get(key)
        if p is None:
            players[key] = Player(r["spid"], g, r["name"], r["season"], r["pay"], max(r["positions"].values()),
                                  min(r["footL"], r["footR"]), r["prices"], dict(r["stats"]), r["rating"], r["ratingCount"])
        else:
            p.stats.update(r["stats"])
    if (DATA / "tags.jsonl").exists():
        for line in open(DATA / "tags.jsonl", encoding="utf-8"):
            r = json.loads(line)
            p = players.get((r["group"], r["spid"]))
            if p: p.tags.add(r["tag"])
    by_group = defaultdict(list)
    for p in players.values():
        by_group[p.group].append(p)
    return by_group


def tag_label(tag):
    return TAG_KO.get(tag, tag.replace("trait:", "특성 "))


@dataclass
class Fit:
    group: str
    grade: int
    names: list
    beta: list
    se: list
    r2: float
    n: int
    stats: list
    tags: list
    seasons: list
    ovr_mean: float
    tag_counts: dict

    def features(self, p: Player):
        c = p.ovr1 - self.ovr_mean
        x = [1.0, c, c * c, float(p.weak_foot >= 5), float(p.weak_foot == 4), float(p.pay)]
        for s in self.stats:
            v = p.stats.get(s)
            x.append(0.0 if v is None else float(v - (self.stat_means[s] if s in ABSOLUTE_STATS else p.ovr1)))
        x += [float(t in p.tags) for t in self.tags]
        x += [float(p.season == s) for s in self.seasons]
        x.append(float(p.season not in self.seasons and p.season != self.base_season))
        return x

    def predict(self, p: Player):
        return math.exp(sum(b * v for b, v in zip(self.beta, self.features(p))))

    def effects(self):
        """(label, % effect, low, high, t, count) for everything except the constant, OVR^2 and seasons."""
        out = []
        for i, name in enumerate(self.names):
            if i == 0 or name == "OVR^2" or name.startswith("season:"): continue
            b, s = self.beta[i], self.se[i]
            out.append((name, pct(b), pct(b - 1.96 * s), pct(b + 1.96 * s), b / s if s else 0.0, self.tag_counts.get(name)))
        return out

    def season_effects(self):
        return sorted(((n[7:], pct(b)) for n, b in zip(self.names, self.beta) if n.startswith("season:") and n != "season:기타"),
                      key=lambda t: -t[1])


def pct(b):
    return (math.exp(b) - 1) * 100


def fit(group, players, grade, min_tag=8, min_season=5):
    data = [p for p in players if p.traded and p.price_at(grade) > FLOOR]
    stat_keys = [s for g in GROUPS[group]["stats"] for s in g]
    stats = [s for s in dict.fromkeys(stat_keys) if sum(s in p.stats for p in data) >= 0.9 * len(data)]
    seasons_count = Counter(p.season for p in data)
    base = seasons_count.most_common(1)[0][0]
    seasons = [s for s, c in seasons_count.items() if c >= min_season and s != base]
    tag_counts = Counter(t for p in data for t in p.tags)
    tags = [t for t, c in tag_counts.most_common() if c >= min_tag]
    f = Fit(group, grade, [], [], [], 0.0, len(data), stats, tags, seasons, statistics.mean(p.ovr1 for p in data),
            {tag_label(t): c for t, c in tag_counts.items()})
    f.base_season = base
    f.stat_means = {s: statistics.mean(p.stats[s] for p in data if s in p.stats) for s in stats}
    f.names = (["const", "OVR +1", "OVR^2", "양발 (약발 5)", "약발 4", "급여 +1"]
               + [f"{STAT_KO.get(s, s)} +1" + ("" if s in ABSOLUTE_STATS else " (같은 OVR에서)") for s in stats]
               + [tag_label(t) for t in tags] + [f"season:{s}" for s in seasons] + ["season:기타"])
    f.tag_counts["양발 (약발 5)"] = sum(p.weak_foot >= 5 for p in data)
    f.tag_counts["약발 4"] = sum(p.weak_foot == 4 for p in data)
    rows = [(f.features(p), math.log(p.price_at(grade))) for p in data]
    f.beta, f.se, f.r2 = ols(rows)
    return f, data


def ols(rows, ridge=1e-6):
    k = len(rows[0][0])
    xtx = [[0.0] * k for _ in range(k)]
    xty = [0.0] * k
    for x, y in rows:
        nz = [(i, v) for i, v in enumerate(x) if v != 0]
        for i, xi in nz:
            xty[i] += xi * y
            row = xtx[i]
            for j, xj in nz:
                row[j] += xi * xj
    for i in range(1, k):
        xtx[i][i] += ridge  # keeps an all-zero column (e.g. an unused season) from breaking the solve
    beta, inv = solve(xtx, xty)
    n = len(rows)
    sse = sum((y - sum(b * v for b, v in zip(beta, x))) ** 2 for x, y in rows)
    ybar = sum(y for _, y in rows) / n
    sst = sum((y - ybar) ** 2 for _, y in rows)
    s2 = sse / max(n - k, 1)
    return beta, [math.sqrt(max(inv[i][i] * s2, 0.0)) for i in range(k)], 1 - sse / sst


def solve(a, b):
    n = len(a)
    m = [row[:] + [b[i]] + [1.0 if j == i else 0.0 for j in range(n)] for i, row in enumerate(a)]
    for col in range(n):
        piv = max(range(col, n), key=lambda r: abs(m[r][col]))
        m[col], m[piv] = m[piv], m[col]
        p = m[col][col]
        if abs(p) < 1e-12:
            raise ValueError(f"singular matrix at column {col}")
        inv_p = 1.0 / p
        m[col] = [v * inv_p for v in m[col]]
        pivot_row = m[col]
        for r in range(n):
            f = m[r][col]
            if r != col and f != 0.0:
                m[r] = [v - f * w for v, w in zip(m[r], pivot_row)]
    return [m[i][n] for i in range(n)], [m[i][n + 1:] for i in range(n)]


_UNITS = {"조": 10 ** 12, "억": 10 ** 8, "만": 10 ** 4}


def parse_bp(text):
    """'1억', '5000만', '1.5조', '2억 5000만' or a plain number → BP."""
    text = text.replace(",", "").replace(" ", "")
    if re.fullmatch(r"\d+(\.\d+)?", text):
        return int(float(text))
    total, rest = 0.0, text
    for m in re.finditer(r"(\d+(?:\.\d+)?)(조|억|만)", text):
        total += float(m[1]) * _UNITS[m[2]]
        rest = rest.replace(m[0], "", 1)
    if rest:
        raise ValueError(f"가격을 읽지 못했습니다: {text}")
    return int(total)


def fmt_bp(v):
    for unit, size in (("조", 10 ** 12), ("억", 10 ** 8)):
        if v >= size:
            return f"{v / size:,.1f}{unit}".replace(".0" + unit, unit)
    return f"{v / 10 ** 4:,.0f}만" if v >= 10 ** 4 else f"{v:,}"
