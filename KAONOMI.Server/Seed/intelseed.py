"""
The common part of the generators of intels (PUT /intel): from lines

    title | included (Name=weight, …) | excluded (Name, …) | level | fuzzy (0/1) | instructions |
    locations (;) | times (;) | related entities (;) | questions (;) | tags | events | signals

('-' for an empty field) it makes the intels, rules "if … then …", with stable ids (<prefix>-<slug of the title>):
sending a file again overwrites the same documents. The date, machine and user are set.
"""
import datetime
import getpass
import json
import pathlib
import platform
import re
import unicodedata
import urllib.request


def slug(text: str) -> str:
    plain = unicodedata.normalize("NFD", text).encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z0-9]+", "-", plain.lower()).strip("-")[:60]


def _items(field: str, sep: str):
    return [] if field.strip() == '-' else [x.strip() for x in field.split(sep) if x.strip()]


def _entities(field: str):
    result = []
    for part in _items(field, ','):
        name, _, weight = part.partition('=')
        result.append({"entityName": name.strip(), "entityWeight": weight.strip()})
    return result


def build(data: str, id_prefix: str, comment: str):
    """The intels of the lines of <data>."""
    now = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")
    out = []
    for line in filter(None, (l.strip() for l in data.splitlines())):
        f = [x.strip() for x in line.split(' | ')]
        assert len(f) == 13, (len(f), line[:60])
        title, inc, exc, level, fuzzy, instr, locs, times, related, questions, tags, events, signals = f
        out.append({
            "id": f"{id_prefix}-{slug(title)}",
            "appName": "KAONOMI.Intels", "date": now, "machineName": platform.node(), "userName": getpass.getuser(),
            "title": title, "comment": comment,
            "includedEntities": _entities(inc),
            "excludedEntities": [{"entityName": n, "entityWeight": ""} for n in _items(exc, ',')],
            "triggerLevel": int(level), "isFuzzy": fuzzy == '1', "instructions": instr,
            "locations": _items(locs, ';'), "times": _items(times, ';'), "relatedEntities": _items(related, ';'),
            "questions": _items(questions, ';'),
            "tags": _items(tags, ' '), "events": _items(events, ' '), "signals": _items(signals, ' '),
            "isDeleted": False,
        })
    return out


def write(intels, expected: int, target: pathlib.Path, send: bool):
    """Checks the intels (their number, no title twice, 2 to 4 included entities, a level of 0 to 100), writes them as JSON and, when <send>, PUTs them to the server."""
    ids = [i["id"] for i in intels]
    doubles = sorted({i for i in ids if ids.count(i) > 1})
    assert not doubles, f"titles twice in the list: {doubles}"
    assert len(intels) == expected, f"{len(intels)} intels instead of {expected}"
    for i in intels:
        assert 2 <= len(i["includedEntities"]) <= 4, i["title"]
        assert 0 <= i["triggerLevel"] <= 100, i["title"]
    target.write_text(json.dumps(intels, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"{len(intels)} intels written to {target}")
    if send:
        ok = 0
        for i in intels:
            req = urllib.request.Request("http://localhost:49166/intel", data=json.dumps(i).encode("utf-8"), method="PUT",
                                         headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(req) as r:
                ok += json.load(r).get("returnedId") == i["id"]
        print(f"{ok} intels saved")
