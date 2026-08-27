const RELAY_PATH = /^\/api\/realtime-translation\/sessions\/([0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12})\/stream$/iu;

export function buildRelayWebSocketUrl(
  baseUrl,
  relayPath,
  sessionId,
  { allowInsecure = false } = {}) {
  let base;
  let relay;
  try {
    base = new URL(baseUrl);
    relay = new URL(relayPath, base);
  } catch {
    throw new Error("Glosify returned an invalid subtitle relay URL.");
  }

  const match = RELAY_PATH.exec(relay.pathname);
  const secureOrigin = base.protocol === "https:";
  const explicitlyAllowedInsecureOrigin = allowInsecure && base.protocol === "http:";
  if ((!secureOrigin && !explicitlyAllowedInsecureOrigin)
      || base.pathname !== "/"
      || base.username
      || base.password
      || base.search
      || base.hash
      || relay.origin !== base.origin
      || relay.username
      || relay.password
      || relay.search
      || relay.hash
      || !match
      || match[1].toLowerCase() !== String(sessionId).toLowerCase()) {
    throw new Error("Glosify returned an invalid subtitle relay URL.");
  }

  relay.protocol = secureOrigin ? "wss:" : "ws:";
  return relay.toString();
}

export function buildRelayProtocols(relayToken) {
  if (typeof relayToken !== "string" || !/^[A-Za-z0-9_-]{43}$/u.test(relayToken)) {
    throw new Error("Glosify returned an invalid subtitle relay token.");
  }
  return ["glosify-realtime", `relay-token.${relayToken}`];
}
