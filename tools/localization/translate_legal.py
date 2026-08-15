#!/usr/bin/env python3
"""Generate localized legal/support Razor views with an independent AI review pass."""

from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path

from translate_ui import API_URL, CACHE, LOCALES, ROOT, api_json


SOURCE_DIR = ROOT / "Glosify/Views/Home"
PAGES = ("Privacy", "Terms", "Support")
PROTECTED = (
    "Glosify, Glosify Live Subtitles, Microsoft Foundry, Azure Speech, Azure Translator, "
    "ElevenLabs Scribe v2, Google, Microsoft, Stripe, Chrome, OAuth, API, and email addresses"
)


def translated_source(page: str) -> str:
    source = (SOURCE_DIR / f"{page}.cshtml").read_text(encoding="utf-8-sig")
    if page in ("Privacy", "Terms"):
        action = f"{page}English"
        notice = f'''\n        <aside class="legal-language-notice">\n            This translation is provided for convenience. If it differs from the English version, the English version governs.\n            <a asp-action="{action}">Read the governing English version</a>.\n        </aside>\n'''
        source = source.replace("</header>", "</header>" + notice, 1)
    return normalize_legal_nav(page, source)


def normalize_legal_nav(page: str, content: str) -> str:
    links = {
        "Privacy": (
            '            <a asp-action="Terms">@Text["Legal.TermsOfService"]</a>\n'
            '            <a asp-action="Support">@Text["Legal.Support"]</a>'
        ),
        "Terms": (
            '            <a asp-action="Privacy">@Text["Legal.PrivacyPolicy"]</a>\n'
            '            <a asp-action="Support">@Text["Legal.Support"]</a>'
        ),
        "Support": (
            '            <a asp-action="Privacy">@Text["Legal.PrivacyPolicy"]</a>\n'
            '            <a asp-action="Terms">@Text["Legal.TermsOfService"]</a>'
        ),
    }
    nav = f'<nav class="legal-links" aria-label=\'@Text["Legal.Label"]\'>\n{links[page]}\n        </nav>'
    return re.sub(r'<nav class="legal-links"[^>]*>.*?</nav>', nav, content, count=1, flags=re.DOTALL)


def validate(page: str, source: str, translation: str) -> None:
    if "```" in translation:
        raise RuntimeError(f"{page}: output contains a Markdown fence")
    source_tokens = set(re.findall(r"@[A-Za-z][A-Za-z0-9_.]*", source))
    translated_tokens = set(re.findall(r"@[A-Za-z][A-Za-z0-9_.]*", translation))
    if source_tokens != translated_tokens:
        raise RuntimeError(f"{page}: Razor tokens changed: {source_tokens ^ translated_tokens}")
    for attribute in re.findall(r'(?:asp-action|href)="[^"]+"', source):
        if attribute not in translation:
            raise RuntimeError(f"{page}: required attribute changed: {attribute}")
    if page in ("Privacy", "Terms"):
        if f'asp-action="{page}English"' not in translation or "legal-language-notice" not in translation:
            raise RuntimeError(f"{page}: governing-English notice is missing")


def translate_page(api_key: str, locale: str, page: str, draft_model: str, review_model: str) -> None:
    language, tone = LOCALES[locale]
    source = translated_source(page)
    cache_base = CACHE / f"legal-{page}-{locale}"
    draft_path = cache_base.with_suffix(".draft.json")
    review_path = cache_base.with_suffix(".reviewed.json")
    CACHE.mkdir(parents=True, exist_ok=True)

    draft = json.loads(draft_path.read_text(encoding="utf-8")) if draft_path.exists() else None
    if not isinstance(draft, dict) or set(draft) != {"content"}:
        draft = api_json(api_key, draft_model, f"""
Translate the visible text and SEO metadata in this complete ASP.NET Core Razor view into {language}.
{tone} Return JSON with exactly one property named content containing the complete translated Razor file.
Preserve every Razor directive/expression, HTML element, attribute name/value, tag-helper action, URL,
email address, and document structure exactly. Do not translate {PROTECTED}. Do not use Markdown fences.
For Privacy and Terms, translate the disclaimer faithfully while keeping the English-governs meaning.
""".strip(), {"content": source})
        draft["content"] = normalize_legal_nav(page, draft["content"])
        try:
            validate(page, source, draft["content"])
        except RuntimeError:
            cache_base.with_suffix(".invalid.cshtml").write_text(draft["content"], encoding="utf-8")
            raise
        draft_path.write_text(json.dumps(draft, ensure_ascii=False, indent=2), encoding="utf-8")

    reviewed = json.loads(review_path.read_text(encoding="utf-8")) if review_path.exists() else None
    if not isinstance(reviewed, dict) or set(reviewed) != {"content"}:
        reviewed = api_json(api_key, review_model, f"""
Independently review this {language} legal/support Razor translation against SOURCE. Mentally back-translate
it and return corrected JSON with exactly one property named content containing the complete file.
{tone} Correct omissions, mistranslations, awkward language, and untranslated ordinary English.
Preserve every Razor token, HTML structure, attribute value, URL, and protected product/provider name exactly.
The English-governs disclaimer must remain unambiguous. Do not use Markdown fences or explain.
""".strip(), {"SOURCE": source, "DRAFT": draft["content"]})
        reviewed["content"] = normalize_legal_nav(page, reviewed["content"])
        validate(page, source, reviewed["content"])
        review_path.write_text(json.dumps(reviewed, ensure_ascii=False, indent=2), encoding="utf-8")

    final_content = normalize_legal_nav(page, reviewed["content"])
    validate(page, source, final_content)
    output = SOURCE_DIR / f"{page}.{locale}.cshtml"
    output.write_text(final_content.lstrip("\ufeff"), encoding="utf-8")
    print(f"wrote {output}", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("locales", nargs="*", choices=LOCALES, default=list(LOCALES))
    parser.add_argument("--draft-model", default="gpt-5.4-mini")
    parser.add_argument("--review-model", default="gpt-5.4-mini")
    args = parser.parse_args()
    api_key = os.environ.get("OPENAI_API_KEY")
    if not api_key:
        raise SystemExit("OPENAI_API_KEY is required")
    for locale in args.locales:
        for page in PAGES:
            translate_page(api_key, locale, page, args.draft_model, args.review_model)


if __name__ == "__main__":
    main()
