"""Exports a token classification model of Hugging Face (argv[2], by default
FacebookAI/xlm-roberta-large-finetuned-conll03-english; the model before was Davlan/xlm-roberta-base-ner-hrl) to ONNX,
quantizes it to int8, and writes a reference for the C# side.

Usage: python ner_export.py <output folder> [model]
Output folder (argv[1]): model_quantized.onnx, tokenizer.json, sentencepiece.bpe.model, config.json, reference.json.
reference.json: for each sentence, the token ids and character offsets of the tokenizer, and the entities found by the
original model (PyTorch, fp32) and by the int8 ONNX model (both with the pipeline, aggregation "simple").
"""
import json
import os
import shutil
import sys
import time

from optimum.onnxruntime import ORTModelForTokenClassification, ORTQuantizer
from optimum.onnxruntime.configuration import AutoQuantizationConfig
from transformers import AutoModelForTokenClassification, AutoTokenizer, pipeline

MODEL = sys.argv[2] if len(sys.argv) > 2 else "FacebookAI/xlm-roberta-large-finetuned-conll03-english"
out = sys.argv[1]
work = out + "-work"
os.makedirs(out, exist_ok=True)

SENTENCES = [
    "Jérôme Fortias travaille chez Nexai à Paris.",
    "Emmanuel Macron a rencontré Olaf Scholz à Berlin lundi.",
    "La société Airbus a ouvert une usine à Toulouse.",
    "Marie Curie est née à Varsovie et a travaillé à l'Université de Paris.",
    "Le Parlement européen siège à Strasbourg et à Bruxelles.",
    "Thomas Pesquet est parti de Cap Canaveral avec SpaceX.",
    "J'ai acheté un billet SNCF pour aller de Lyon à Marseille.",
    "Kylian Mbappé a signé au Real Madrid.",
    "L'ONU a publié un rapport sur le Soudan.",
    "Anthony et Léa habitent à Nantes depuis 2019.",
    "Microsoft et OpenAI ont annoncé un partenariat à San Francisco.",
    "Barack Obama visited Nairobi with Michelle Obama.",
    "Apple opened a new store in Tokyo, says Tim Cook.",
    "The European Central Bank is based in Frankfurt.",
    "Angela Merkel met Joe Biden at the White House in Washington.",
    "Nader Jokhadar had given Syria the lead with a well-struck header in the seventh minute.",
    "Google DeepMind and the University of Oxford published a paper.",
    "Elon Musk said Tesla will build a factory near Berlin.",
    "The United Nations held a summit in Geneva.",
    "Lionel Messi joined Inter Miami in Florida.",
    "Angela Merkel und Emmanuel Macron trafen sich in Paris.",
    "Pedro Sánchez visitó Lisboa con el rey Felipe.",
    "Kaonomi est un projet personnel de Jérôme, hébergé sur GitHub.",
    "Le Louvre, à Paris, a accueilli neuf millions de visiteurs.",
    "Sophie et Paul ont visité le Mont-Saint-Michel en Normandie.",
    "La NASA et l'ESA préparent une mission vers Mars.",
    "Rendez-vous avec le docteur Martin à l'hôpital Necker mardi.",
    "Jean-Baptiste Poquelin, dit Molière, est né à Paris.",
    "The Bank of England raised its rates; London markets fell.",
    "Satya Nadella leads Microsoft from Redmond, Washington.",
]

print("export to ONNX…", flush=True)
ort_model = ORTModelForTokenClassification.from_pretrained(MODEL, export=True)
tokenizer = AutoTokenizer.from_pretrained(MODEL)
ort_model.save_pretrained(work)
tokenizer.save_pretrained(work)

print("quantization int8 (dynamic)…", flush=True)
quantizer = ORTQuantizer.from_pretrained(work)
quantizer.quantize(save_dir=out, quantization_config=AutoQuantizationConfig.avx2(is_static=False, per_channel=False))
for name in ("tokenizer.json", "sentencepiece.bpe.model", "config.json", "tokenizer_config.json", "special_tokens_map.json"):
    if os.path.exists(os.path.join(work, name)):
        shutil.copy(os.path.join(work, name), os.path.join(out, name))

print("reference…", flush=True)
fp32 = pipeline("ner", model=AutoModelForTokenClassification.from_pretrained(MODEL), tokenizer=tokenizer, aggregation_strategy="simple")
int8 = pipeline("ner", model=ORTModelForTokenClassification.from_pretrained(out, file_name="model_quantized.onnx"), tokenizer=tokenizer, aggregation_strategy="simple")


def entities(results):
    return [{"type": r["entity_group"], "text": r["word"], "start": int(r["start"]), "end": int(r["end"]), "score": round(float(r["score"]), 4)} for r in results]


reference = []
timings = {"fp32": 0.0, "int8": 0.0}
for sentence in SENTENCES:
    encoded = tokenizer(sentence, return_offsets_mapping=True)
    t = time.perf_counter(); a = fp32(sentence); timings["fp32"] += time.perf_counter() - t
    t = time.perf_counter(); b = int8(sentence); timings["int8"] += time.perf_counter() - t
    reference.append({
        "text": sentence,
        "ids": encoded["input_ids"],
        "offsets": [list(o) for o in encoded["offset_mapping"]],
        "fp32": entities(a),
        "int8": entities(b),
    })
with open(os.path.join(out, "reference.json"), "w", encoding="utf-8") as f:
    json.dump({"model": MODEL, "labels": ort_model.config.id2label, "sentences": reference}, f, ensure_ascii=False, indent=1)

shutil.rmtree(work, ignore_errors=True)
size = os.path.getsize(os.path.join(out, "model_quantized.onnx")) / 1e6
same = sum(1 for r in reference if [(e["type"], e["start"], e["end"]) for e in r["fp32"]] == [(e["type"], e["start"], e["end"]) for e in r["int8"]])
print(f"done: model_quantized.onnx {size:.0f} MB; labels {ort_model.config.id2label}")
print(f"sentences where int8 finds exactly the entities of fp32: {same}/{len(reference)}")
print(f"time on CPU for {len(reference)} sentences: fp32 {timings['fp32']*1000:.0f} ms, int8 {timings['int8']*1000:.0f} ms")
