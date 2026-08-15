#!/usr/bin/env python3
"""Generate complete UI .resx catalogs with an independent AI correction pass."""

from __future__ import annotations

import argparse
import copy
import getpass
import json
import os
import re
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path
from tempfile import gettempdir


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Glosify/Resources/Localization.UiText.resx"
CACHE = Path(os.environ.get(
    "GLOSIFY_LOCALIZATION_CACHE",
    Path(gettempdir()) / f"glosify-localization-cache-{getpass.getuser()}"))
API_URL = os.environ.get("OPENAI_API_URL", "https://api.openai.com/v1/chat/completions")
BATCH_SIZE = 80

LOCALES = {
    "es-419": ("Latin American Spanish", "Use friendly informal tú copy."),
    "pt-BR": ("Brazilian Portuguese", "Use friendly você copy."),
    "fr-FR": ("French", "Use clear standard polite French."),
    "ja-JP": ("Japanese", "Use concise, natural polite です/ます UI copy."),
    "zh-Hans": ("Simplified Chinese", "Use concise Mainland-standard Simplified Chinese UI copy."),
    "uk-UA": ("Ukrainian", "Use natural contemporary Ukrainian UI copy."),
    "tr-TR": ("Turkish", "Use natural, approachable Turkish UI copy."),
    "id-ID": ("Indonesian", "Use clear, neutral Indonesian UI copy."),
    "vi-VN": ("Vietnamese", "Use clear, natural Vietnamese UI copy."),
    "ar": ("Modern Standard Arabic", "Use clear neutral Modern Standard Arabic UI copy."),
}

PROTECTED = (
    "Glosify, Microsoft Foundry, Azure Speech, Azure Translator, ElevenLabs, "
    "Google, Microsoft, Stripe, Chrome, PDF, API, MCP, SignalR, WebRTC, AI, "
    "email addresses, URLs, personal names, language-learning examples, and currency codes"
)


def api_json(api_key: str, model: str, system: str, payload: dict[str, object]) -> dict[str, str]:
    body = json.dumps({
        "model": model,
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": json.dumps(payload, ensure_ascii=False)},
        ],
        "response_format": {"type": "json_object"},
        "max_completion_tokens": 16000,
    }).encode("utf-8")
    request = urllib.request.Request(API_URL, body, {
        "Authorization": f"Bearer {api_key}",
        "Content-Type": "application/json",
    })
    last_error: Exception | None = None
    for attempt in range(7):
        try:
            with urllib.request.urlopen(request, timeout=180) as response:
                envelope = json.load(response)
            content = envelope["choices"][0]["message"]["content"]
            result = json.loads(content)
            if not isinstance(result, dict):
                raise ValueError("Model response was not a JSON object")
            return {str(key): str(value) for key, value in result.items()}
        except urllib.error.HTTPError as exc:
            try:
                detail = exc.read().decode("utf-8")[:2000]
            except Exception:
                detail = str(exc)
            last_error = RuntimeError(f"HTTP {exc.code}: {detail}")
            if exc.code not in {408, 409, 425, 429, 500, 502, 503, 504} or attempt == 6:
                break
            time.sleep(min(30, 2 ** attempt))
        except (urllib.error.URLError, TimeoutError, KeyError, ValueError, json.JSONDecodeError) as exc:
            last_error = exc
            if attempt == 6:
                break
            time.sleep(min(30, 2 ** attempt))
    raise RuntimeError(f"Translation request failed after retries: {last_error}")


def entries(path: Path) -> tuple[ET.ElementTree, dict[str, str]]:
    tree = ET.parse(path)
    values = {
        node.attrib["name"]: node.findtext("value", default="")
        for node in tree.getroot().findall("data")
    }
    return tree, values


def placeholders(value: str) -> list[str]:
    return sorted(re.findall(r"\{\d+(?:[^}]*)?\}", value))


def translate_locale(api_key: str, locale: str, draft_model: str, review_model: str) -> None:
    language, tone = LOCALES[locale]
    tree, source = entries(SOURCE)
    CACHE.mkdir(parents=True, exist_ok=True)
    translated: dict[str, str] = {}
    items = list(source.items())

    for offset in range(0, len(items), BATCH_SIZE):
        batch = dict(items[offset:offset + BATCH_SIZE])
        cache_base = CACHE / f"{locale}-{offset:04d}"
        draft_path = cache_base.with_suffix(".draft.json")
        reviewed_path = cache_base.with_suffix(".reviewed.json")

        draft = json.loads(draft_path.read_text(encoding="utf-8")) if draft_path.exists() else None
        if not isinstance(draft, dict) or set(draft) != set(batch):
            draft = api_json(api_key, draft_model, f"""
You translate production web UI resources from British English into {language}.
{tone}
Return one JSON object with exactly the input keys and translated string values.
Preserve every .NET placeholder such as {{0}}, literal token such as {{{{blank}}}}, HTML fragment,
ellipsis, punctuation meaning, and explicit singular/plural distinction. Do not translate {PROTECTED}.
Translate interface copy, accessibility labels, confirmations, and errors naturally; never explain.
""".strip(), batch)
            if set(draft) != set(batch):
                missing = sorted(set(batch) - set(draft))
                extra = sorted(set(draft) - set(batch))
                raise RuntimeError(f"{locale} draft batch {offset}: missing={missing}, extra={extra}")
            draft_path.write_text(json.dumps(draft, ensure_ascii=False, indent=2), encoding="utf-8")

        reviewed = json.loads(reviewed_path.read_text(encoding="utf-8")) if reviewed_path.exists() else None
        if not isinstance(reviewed, dict) or set(reviewed) != set(batch):
            reviewed = api_json(api_key, review_model, f"""
You are the final localization reviewer for {language}. Independently compare SOURCE with DRAFT,
mentally back-translate it, and return one corrected JSON object with exactly the SOURCE keys.
The top-level properties must be resource keys such as "Nav.Home" mapped directly to translated strings.
Never return SOURCE, DRAFT, TRANSLATION, or any other wrapper property.
{tone} Fix mistranslation, awkward UI wording, untranslated ordinary English, grammar, and inconsistent terms.
Every placeholder present in a SOURCE value, including {{0}} in singular strings, must appear literally
and unchanged in the corrected value. Preserve all markup exactly. Do not translate {PROTECTED}. Never explain.
""".strip(), {"SOURCE": batch, "DRAFT": draft})
        if set(reviewed) != set(batch):
            missing = sorted(set(batch) - set(reviewed))
            extra = sorted(set(reviewed) - set(batch))
            raise RuntimeError(f"{locale} batch {offset}: missing={missing}, extra={extra}")
        for key, value in list(reviewed.items()):
            required = placeholders(batch[key])
            if required != placeholders(value):
                correction = api_json(api_key, review_model, f"""
Return one JSON object whose only property is {json.dumps(key)}. Translate the SOURCE into {language}.
The output value MUST contain these literal placeholders exactly once and unchanged: {json.dumps(required)}.
{tone} Preserve protected names and markup. Never explain and never add a wrapper property.
""".strip(), {"SOURCE": batch[key], "DRAFT": value})
                if set(correction) != {key} or placeholders(correction[key]) != required:
                    raise RuntimeError(f"{locale} placeholder correction failed for {key}")
                reviewed[key] = correction[key]
        reviewed_path.write_text(json.dumps(reviewed, ensure_ascii=False, indent=2), encoding="utf-8")
        translated.update(reviewed)
        print(f"{locale}: reviewed {min(offset + BATCH_SIZE, len(items))}/{len(items)}", flush=True)

    output_tree = copy.deepcopy(tree)
    for node in output_tree.getroot().findall("data"):
        node.find("value").text = translated[node.attrib["name"]]
    ET.indent(output_tree, space="  ")
    output = ROOT / f"Glosify/Resources/Localization.UiText.{locale}.resx"
    output_tree.write(output, encoding="utf-8", xml_declaration=True)
    print(f"wrote {output}", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("locales", nargs="*", choices=LOCALES, default=list(LOCALES))
    parser.add_argument("--draft-model", default="gpt-5.4-mini")
    parser.add_argument("--review-model", default="gpt-5.4")
    args = parser.parse_args()
    api_key = os.environ.get("OPENAI_API_KEY")
    if not api_key:
        raise SystemExit("OPENAI_API_KEY is required")
    for locale in args.locales:
        translate_locale(api_key, locale, args.draft_model, args.review_model)


if __name__ == "__main__":
    main()
