export interface Env {
  AI: Ai;
  TRANSLATOR_TOKEN: string;
}

interface TranslationRequest {
  text: string;
  source_lang?: string;
  target_lang: string;
}

const MODEL = "@cf/meta/m2m100-1.2b";
const MAX_TEXT_CHARACTERS = 2_000;
const LANGUAGE_CODE = /^[a-z]{2,3}$/;
const JSON_HEADERS = { "Content-Type": "application/json; charset=utf-8" };

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    if (url.pathname === "/" && request.method === "GET") {
      return json({ service: "glosify-m2m100-translator", model: MODEL });
    }
    if (url.pathname !== "/translate") {
      return json({ error: "Not found" }, 404);
    }
    if (request.method !== "POST") {
      return json({ error: "Method not allowed" }, 405, { Allow: "POST" });
    }
    if (!env.TRANSLATOR_TOKEN) {
      return json({ error: "Worker is not configured" }, 503);
    }
    const authorization = request.headers.get("Authorization");
    if (!authorization?.startsWith("Bearer ")
      || !await secretsEqual(authorization.slice(7), env.TRANSLATOR_TOKEN)) {
      return json({ error: "Unauthorized" }, 401);
    }

    const contentLength = Number(request.headers.get("Content-Length") ?? 0);
    if (Number.isFinite(contentLength) && contentLength > 16_384) {
      return json({ error: "Request body is too large" }, 413);
    }

    let body: TranslationRequest;
    try {
      body = await request.json<TranslationRequest>();
    } catch {
      return json({ error: "Invalid JSON body" }, 400);
    }

    const text = typeof body.text === "string" ? body.text.trim() : "";
    const sourceLang = normalizeLanguageCode(body.source_lang ?? "en");
    const targetLang = normalizeLanguageCode(body.target_lang);
    if (!text) {
      return json({ error: "Missing required field: text" }, 400);
    }
    if (text.length > MAX_TEXT_CHARACTERS) {
      return json({ error: `text exceeds ${MAX_TEXT_CHARACTERS} characters` }, 400);
    }
    if (!LANGUAGE_CODE.test(sourceLang) || !LANGUAGE_CODE.test(targetLang)) {
      return json({ error: "source_lang and target_lang must be language codes such as en or fr" }, 400);
    }

    try {
      const result = await env.AI.run(MODEL, {
        text,
        source_lang: sourceLang,
        target_lang: targetLang,
      }) as unknown;
      const translated = readTranslation(result);
      if (!translated) {
        return json({ error: "The model returned an empty translation" }, 502);
      }
      return json({
        model: MODEL,
        source_lang: sourceLang,
        target_lang: targetLang,
        translated,
      });
    } catch (error) {
      console.error("M2M100 translation failed", error);
      return json({ error: "Translation failed" }, 502);
    }
  },
} satisfies ExportedHandler<Env>;

function readTranslation(result: unknown): string | null {
  if (typeof result === "string") {
    return result.trim() || null;
  }
  if (!result || typeof result !== "object") {
    return null;
  }
  const value = result as Record<string, unknown>;
  for (const key of ["translated_text", "translated", "result"]) {
    if (typeof value[key] === "string" && value[key].trim()) {
      return value[key].trim();
    }
  }
  return null;
}

function normalizeLanguageCode(value: unknown): string {
  return typeof value === "string"
    ? value.trim().toLowerCase().split("-", 1)[0]
    : "";
}

async function secretsEqual(left: string, right: string): Promise<boolean> {
  const encoder = new TextEncoder();
  const [leftHash, rightHash] = await Promise.all([
    crypto.subtle.digest("SHA-256", encoder.encode(left)),
    crypto.subtle.digest("SHA-256", encoder.encode(right)),
  ]);
  const leftBytes = new Uint8Array(leftHash);
  const rightBytes = new Uint8Array(rightHash);
  let difference = leftBytes.length ^ rightBytes.length;
  for (let index = 0; index < Math.max(leftBytes.length, rightBytes.length); index++) {
    difference |= (leftBytes[index] ?? 0) ^ (rightBytes[index] ?? 0);
  }
  return difference === 0;
}

function json(
  body: unknown,
  status = 200,
  headers: Record<string, string> = {},
): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { ...JSON_HEADERS, ...headers },
  });
}
