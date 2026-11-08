"""
Generates ontology-intels.json: the ontology objects (PUT /ontology) cited by all the intels of the search engine. Each
included entity, related entity and location of an intel that is not yet in the ontology (name or synonym, case and
accents ignored) becomes a node, under a class of its entity type, under the root "Objets des intels".

The entity type of each name comes from ontology-intels-types.json (made by classify_ontology_objects.py with the local
model); the names typed NONE (dates, numbers, vague words) are left out. For each object the node keeps what the intels
say of it: how many intels cite it and which (description), up to three of their instructions (sentencesExemples), the
objects cited with it most often (relatedEntities), the most frequent tags, events and signals of these intels, the
other spellings met (synonyms), and the emotion levels from their signals (!FEAR → FearLevel, !SAD → SadLevel, !HAPPY →
HappyLevel, !DISGUST → DisgustLevel, !ANGER → AngryLevel), in percent of the intels citing it. The ids are stable
(intels-o-<slug>): running it again overwrites the same nodes. Nodes already in the ontology are never changed.

Usage: python make_ontology_from_intels.py [--send]
"""
import collections
import json
import pathlib
import re
import sys
import unicodedata
import urllib.request

HERE = pathlib.Path(__file__).parent
ES = "http://localhost:9200"
API = "http://localhost:49166"

ROOT = "intels-racine"
CLASSES = {
    "PERSON": "Personnes citées par les intels",
    "ORGANISATION": "Organisations",
    "COUNTRY": "Pays",
    "CITY": "Villes",
    "LOCATION": "Lieux et zones",
    "EVENT": "Événements",
    "LAW": "Lois, règlements et accords",
    "PROGRAMME": "Programmes, plans et dispositifs",
    "SOFTWARE": "Logiciels, modèles et plateformes",
    "HARDWARE": "Matériels et équipements",
    "MATERIAL": "Matières, substances et énergies",
    "PRODUCT": "Produits, indices et instruments",
    "PROFESSION": "Métiers et catégories de personnes",
    "NORP": "Nationalités et groupes",
    "DOMAIN": "Concepts et domaines",
}
EMOTIONS = {"!FEAR": "FearLevel", "!SAD": "SadLevel", "!HAPPY": "HappyLevel", "!DISGUST": "DisgustLevel", "!ANGER": "AngryLevel"}


def fold(s):
    return "".join(c for c in unicodedata.normalize("NFD", s.lower()) if unicodedata.category(c) != "Mn").strip()


def slug(s):
    return re.sub(r"[^a-z0-9]+", "-", fold(s)).strip("-")[:80] or "x"


def get(url, body):
    req = urllib.request.Request(url, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.load(r)


def put(node):
    req = urllib.request.Request(API + "/ontology", data=json.dumps(node, ensure_ascii=False).encode("utf-8"),
                                 method="PUT", headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


def names_of(intel):
    for e in intel.get("includedEntities") or []:
        yield (e.get("entityName") or "").strip()
    for key in ("relatedEntities", "locations"):
        for v in intel.get(key) or []:
            yield (v or "").strip()


def main(send):
    intels = [h["_source"] for h in get(ES + "/intel/_search", {"size": 10000, "query": {"match_all": {}}})["hits"]["hits"]]
    intels = [i for i in intels if not i.get("isDeleted")]
    onto = [h["_source"] for h in get(ES + "/ontology/_search", {"size": 10000, "_source": ["id", "name", "synonyms"], "query": {"match_all": {}}})["hits"]["hits"]]
    known = set()
    for o in onto:
        if not (o.get("id") or "").startswith("intels-"):
            known.add(fold(o.get("name") or ""))
            known.update(fold(s) for s in o.get("synonyms") or [])
    types = json.loads((HERE / "ontology-intels-types.json").read_text(encoding="utf-8"))
    types = {fold(k): v for k, v in types.items()}

    spellings = collections.defaultdict(collections.Counter)
    cited = collections.defaultdict(list)
    for intel in intels:
        seen = set()
        for n in names_of(intel):
            if not n or n == "-":
                continue
            k = fold(n)
            spellings[k][n] += 1
            if k not in seen:
                seen.add(k)
                cited[k].append(intel)

    objects = {k: v for k, v in cited.items() if k not in known and types.get(k) in CLASSES}
    nodes = [{"id": ROOT, "name": "Objets des intels", "title": "Objets des intels", "EntityType": "MISC", "isOntologyClass": True,
              "nodeparentId": None, "description": "Objets cités par les intels (entités incluses, entités liées et lieux), classés par type.",
              "Tags": ["#INTEL", "#ONTOLOGIE"]}]
    for t, label in CLASSES.items():
        nodes.append({"id": f"intels-c-{t.lower()}", "name": label, "title": label, "EntityType": t, "isOntologyClass": True,
                      "nodeparentId": "ontology-root-persons" if t == "PERSON" else ROOT,
                      "description": f"{label} : objets de type {t} cités par les intels.", "Tags": ["#INTEL", "#" + t]})

    used = set()
    for k, its in sorted(objects.items()):
        name = spellings[k].most_common(1)[0][0]
        t = types[k]
        node_id = f"intels-o-{slug(name)}"
        while node_id in used:  # two names with the same slug ("I.A." and "IA"): the second gets a suffix
            node_id += "-b"
        used.add(node_id)
        co = collections.Counter()
        tags, events, signals = collections.Counter(), collections.Counter(), collections.Counter()
        for i in its:
            for n in set(fold(x) for x in names_of(i) if x and x != "-") - {k}:
                co[n] += 1
            tags.update(i.get("tags") or [])
            events.update(i.get("events") or [])
            signals.update(i.get("signals") or [])
        titles = [i.get("title") or i.get("id") for i in its]
        node = {
            "id": node_id, "name": name, "title": name, "EntityType": t, "isOntologyClass": False,
            "nodeparentId": f"intels-c-{t.lower()}",
            "description": f"Cité par {len(its)} intel{'s' if len(its) > 1 else ''} : " + " ; ".join(titles[:5]) + (" ; …" if len(titles) > 5 else "") + ".",
            "synonyms": [s for s, _ in spellings[k].most_common() if s != name],
            "sentencesExemples": list(dict.fromkeys(i.get("instructions") for i in its if i.get("instructions")))[:3],
            "relatedEntities": [spellings[n].most_common(1)[0][0] for n, _ in co.most_common(10)],
            "relatedLinks": [],
            "Tags": [x for x, _ in tags.most_common(6)],
            "Events": [x for x, _ in events.most_common(3)],
            "Signals": [x for x, _ in signals.most_common(3)],
            "Confidentiality": "PUBLIC(PU)",
        }
        for signal, field in EMOTIONS.items():
            if signals[signal]:
                node[field] = round(100 * signals[signal] / len(its))
        nodes.append(node)

    ids = [n["id"] for n in nodes]
    dup = [i for i, c in collections.Counter(ids).items() if c > 1]
    if dup:
        raise SystemExit(f"duplicate ids: {dup[:10]}")
    out = HERE / "ontology-intels.json"
    out.write_text(json.dumps(nodes, ensure_ascii=False, indent=1), encoding="utf-8")
    by_type = collections.Counter(n["EntityType"] for n in nodes if not n["isOntologyClass"])
    print(f"{len(intels)} intels, {len(cited)} objects cited, {len(cited) - len(objects)} already in the ontology or typed NONE")
    print(f"{len(nodes)} nodes written to {out}: " + ", ".join(f"{t} {c}" for t, c in by_type.most_common()))
    if send:
        failed = 0
        for n in nodes:
            r = put(n)
            if str(r.get("returnedId")) in ("-1", ""):
                failed += 1
                print("failed", n["id"], r.get("message"))
        print(f"{len(nodes) - failed} nodes saved, {failed} failed")


if __name__ == "__main__":
    main("--send" in sys.argv)
