"""
The common part of the generators of symbolic patterns (PUT /symbolicpattern): from lines
"symbol | tags | comment | regex bodies separated by ';'" it makes the entries: the symbol is the first word; each
body becomes a case-insensitive, whole-word regex where an accented letter also matches its plain form (é -> [ée]),
a space any spacing and an apostrophe ' or ’; the id is stable (<prefix>-<symbol>), so sending a file again
overwrites the same documents; the date, machine and user are set (without a date the server kept year 1, which the
chat cannot read).
"""
import datetime
import getpass
import json
import pathlib
import platform
import re
import unicodedata

NOW = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")

ACCENTS = {"é": "[ée]", "è": "[èe]", "ê": "[êe]", "à": "[àa]", "â": "[âa]", "î": "[îi]", "ï": "[ïi]",
           "ô": "[ôo]", "û": "[ûu]", "ù": "[ùu]", "ü": "[üu]", "ë": "[ëe]", "ç": "[çc]", "œ": "(œ|oe)"}


def tolerant(body: str) -> str:
    """The body with each accented letter also matching its plain form, and the spaces matching any spacing."""
    out = "".join(ACCENTS.get(c, c) for c in body)
    return out.replace(" ", r"\s+").replace("'", "['’]")


def regex(body: str) -> str:
    """A whole-word, case-insensitive regex of the body."""
    return r"(?i)(?<![\w-])(?:" + tolerant(body.strip()) + r")(?![\w-])"


def slug(word: str) -> str:
    plain = unicodedata.normalize("NFD", word).encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z0-9]+", "-", plain.lower()).strip("-")


def entries(data: str, id_prefix: str, signal: str, language: str = "FR"):
    """The entries of the lines of <paramref name="data"/>, with the signal <paramref name="signal"/> and no event."""
    for line in filter(None, (l.strip() for l in data.splitlines())):
        symbol, tags, comment, bodies = line.split("|", 3)
        parts = [p for p in bodies.split(";") if p.strip()]
        yield {
            "id": f"{id_prefix}-{slug(symbol)}",
            "appName": "KAONOMI.SymbolicPatterns",
            "date": NOW,
            "machineName": platform.node(),
            "userName": getpass.getuser(),
            "title": symbol[0].upper() + symbol[1:],
            "symbol": symbol,
            "regexPatterns": [regex(p) for p in parts],
            "event": None,
            "events": [],
            "signals": [signal],
            "tags": tags.split(),
            "language": language,
            "comment": comment,
            "isDeleted": False,
        }


def write(data: str, id_prefix: str, signal: str, expected: int, target: pathlib.Path):
    """Checks the entries (their number, no symbol twice, 1 to 6 regexes each) and writes them as JSON."""
    items = list(entries(data, id_prefix, signal))
    symbols = [e["symbol"] for e in items]
    doubles = sorted({s for s in symbols if symbols.count(s) > 1})
    assert not doubles, f"symbols twice in the list: {doubles}"
    assert len(items) == expected, f"{len(items)} entries instead of {expected}"
    for e in items:
        assert 1 <= len(e["regexPatterns"]) <= 6, (e["symbol"], len(e["regexPatterns"]))
    target.write_text(json.dumps(items, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"{len(items)} entries written to {target}")
