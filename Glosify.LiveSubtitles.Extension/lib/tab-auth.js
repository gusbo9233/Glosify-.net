// Session storage survives MV3 worker suspension without persisting the PKCE
// verifier to disk or exposing it to content scripts.
const KEY = "glosifyPendingTabAuth";
const MAX_AGE_MS = 10 * 60 * 1000;

export function createTabAuth({ chrome, complete, failed, now = Date.now }) {
  let queue = Promise.resolve();
  const serialize = action => {
    const result = queue.then(action);
    queue = result.catch(() => {});
    return result;
  };
  const read = async () => (await chrome.storage.session.get(KEY))[KEY];

  async function navigate(details) {
    if (details.frameId !== 0) return;
    const pending = await read();
    if (!pending || pending.tabId !== details.tabId) return;
    const callback = new URL(details.url);
    const expected = new URL(pending.redirectUri);
    if (callback.origin !== expected.origin || callback.pathname !== expected.pathname) return;
    // Consume before exchange: duplicate navigation events cannot redeem twice.
    await chrome.storage.session.remove(KEY);
    try {
      if (now() >= pending.expiresAt) throw new Error("Sign-in expired. Connect GlobeGlotter again.");
      if (callback.searchParams.get("state") !== pending.oauthState) {
        throw new Error("GlobeGlotter sign-in returned an invalid state value.");
      }
      const code = callback.searchParams.get("code");
      if (!code) throw new Error("GlobeGlotter sign-in did not return a code. Please try again.");
      await complete({ code, redirectUri: pending.redirectUri, codeVerifier: pending.codeVerifier });
    } catch (error) {
      await failed(error);
    } finally {
      await chrome.tabs.remove(pending.tabId).catch(() => {});
    }
  }

  // Register synchronously so Chrome can wake the worker for the callback,
  // including a redirect to chromiumapp.org that ends in a network error.
  const filter = { url: [{ hostEquals: new URL(chrome.identity.getRedirectURL("glosify")).hostname }] };
  for (const event of [chrome.webNavigation.onBeforeNavigate,
    chrome.webNavigation.onCommitted, chrome.webNavigation.onErrorOccurred]) {
    event.addListener(details => { void serialize(() => navigate(details)).catch(failed); }, filter);
  }
  chrome.tabs.onRemoved.addListener(tabId => {
    void serialize(async () => {
      const pending = await read();
      if (pending?.tabId !== tabId) return;
      await chrome.storage.session.remove(KEY);
      await failed(new Error("GlobeGlotter sign-in was cancelled. Connect again to retry."));
    }).catch(failed);
  });

  return {
    cancel: () => serialize(async () => {
      const pending = await read();
      await chrome.storage.session.remove(KEY);
      if (pending) await chrome.tabs.remove(pending.tabId).catch(() => {});
    }),
    start: options => serialize(async () => {
      const previous = await read();
      if (previous && previous.expiresAt > now()) {
        try {
          await chrome.tabs.update(previous.tabId, { active: true });
          return;
        } catch { /* The tab may have closed while the worker was asleep. */ }
      }
      await chrome.storage.session.remove(KEY);
      // Save the tab ID before navigating, even for an immediate server redirect.
      const tab = await chrome.tabs.create({ url: "about:blank", active: true });
      try {
        await chrome.storage.session.set({
          [KEY]: { ...options, tabId: tab.id, expiresAt: now() + MAX_AGE_MS },
        });
        await chrome.tabs.update(tab.id, { url: options.authorizeUrl });
      } catch (error) {
        await chrome.storage.session.remove(KEY);
        await chrome.tabs.remove(tab.id).catch(() => {});
        throw error;
      }
    }),
  };
}
