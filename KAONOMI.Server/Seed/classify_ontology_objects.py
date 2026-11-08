"""
Types the objects cited by the intels (included entities, related entities, locations) that are not yet in the ontology,
with the local Ollama model (mistral-small3.2), for make_ontology_from_intels.py. The answers are kept in
ontology-intels-types.json ({name: type}); a run only asks for the names not typed yet, so after new intels it is
enough to run it again.

Usage: python classify_ontology_objects.py [batch size, default 60]
"""
import json
import pathlib
import sys
import time
import unicodedata
import urllib.request

HERE = pathlib.Path(__file__).parent
ES = "http://localhost:9200"
OLLAMA = "http://localhost:11434/api/chat"
MODEL = "mistral-small3.2:latest"
CACHE = HERE / "ontology-intels-types.json"
TYPES = ["PERSON", "ORGANISATION", "COUNTRY", "CITY", "LOCATION", "EVENT", "LAW", "PROGRAMME", "SOFTWARE",
         "HARDWARE", "MATERIAL", "PRODUCT", "PROFESSION", "NORP", "DOMAIN", "NONE"]
PROMPT = """Tu classes des termes français extraits d'une base de veille (actualité, géopolitique, finance, cybersécurité, IA, psychologie) en types d'entités d'une ontologie.
Types possibles :
- PERSON : personne réelle nommée (ex. Emmanuel Macron, Jensen Huang)
- ORGANISATION : entreprise, institution, parti, groupe armé ou criminel, agence, ONG, média (ex. BCE, Akira, Hamas, Nvidia)
- COUNTRY : pays ou État
- CITY : ville
- LOCATION : région, mer, détroit, zone géographique, lieu, bâtiment (ex. Donbass, mer Noire, Ormuz)
- EVENT : événement daté ou nommé (ex. élection présidentielle, COP30, sommet de l'OTAN, attentat)
- LAW : loi, règlement, directive, traité, accord, norme (ex. AI Act, NIS2, accord UE-Mercosur, RGPD)
- PROGRAMME : plan, programme, dispositif, fonds, politique publique (ex. SAFE, Mon soutien psy, Readiness 2030)
- SOFTWARE : logiciel, modèle d'IA, application, plateforme, réseau social (ex. ChatGPT, Windows, TikTok)
- HARDWARE : matériel, puce, appareil, arme, véhicule, équipement (ex. GPU, drones, Rafale, VPN)
- MATERIAL : matière première, substance, médicament, drogue, énergie (ex. lithium, pétrole, cannabis, psilocybine)
- PRODUCT : produit, marque, indice boursier, instrument financier, monnaie (ex. CAC 40, bitcoin, OAT, Livret A, euro)
- PROFESSION : métier, fonction, catégorie de personnes (ex. psychiatres, agriculteurs, adolescents, commissaire)
- NORP : nationalité, groupe religieux ou politique (ex. Ukrainiens, démocrates, sociaux-démocrates)
- DOMAIN : concept, domaine, phénomène, notion, technique, maladie, émotion (ex. inflation, rançongiciel, dépression, cryptographie post-quantique)
- NONE : terme qui n'est pas un objet (adjectif seul, mot trop vague, question, date, nombre seul)
Réponds uniquement par un objet JSON {"terme": "TYPE", ...} avec exactement les termes donnés, sans en ajouter.
Termes :
"""


def fold(s):
    return "".join(c for c in unicodedata.normalize("NFD", s.lower()) if unicodedata.category(c) != "Mn").strip()


def post(url, body, timeout=120):
    req = urllib.request.Request(url, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def candidates():
    intels = [h["_source"] for h in post(ES + "/intel/_search", {"size": 10000, "query": {"match_all": {}}})["hits"]["hits"]]
    onto = [h["_source"] for h in post(ES + "/ontology/_search", {"size": 10000, "_source": ["id", "name", "synonyms"], "query": {"match_all": {}}})["hits"]["hits"]]
    known = {fold(o.get("name") or "") for o in onto if not (o.get("id") or "").startswith("intels-")}
    known |= {fold(s) for o in onto if not (o.get("id") or "").startswith("intels-") for s in o.get("synonyms") or []}
    names = {}
    for i in intels:
        if i.get("isDeleted"):
            continue
        for v in [e.get("entityName") for e in i.get("includedEntities") or []] + (i.get("relatedEntities") or []) + (i.get("locations") or []):
            v = (v or "").strip()
            if v and v != "-":
                names.setdefault(fold(v), v)
    return sorted(v for k, v in names.items() if k not in known)


def main(size):
    done = json.loads(CACHE.read_text(encoding="utf-8")) if CACHE.exists() else {}
    todo = [n for n in candidates() if n not in done]
    print(f"{len(todo)} names to type, {len(done)} already typed")
    for start in range(0, len(todo), size):
        batch = todo[start:start + size]
        t = time.time()
        try:
            body = {"model": MODEL, "stream": False, "format": "json", "options": {"temperature": 0, "num_ctx": 8192},
                    "messages": [{"role": "user", "content": PROMPT + "\n".join(batch)}]}
            answer = json.loads(post(OLLAMA, body, timeout=600)["message"]["content"])
        except Exception as ex:
            print("error", ex)
            continue
        for n in batch:
            v = str(answer.get(n, "")).upper().strip()
            if v in TYPES:
                done[n] = v
        CACHE.write_text(json.dumps(done, ensure_ascii=False, indent=0), encoding="utf-8")
        print(f"{len(done)} typed ({time.time() - t:.0f}s)", flush=True)


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 else 60)
