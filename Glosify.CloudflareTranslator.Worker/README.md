# Glosify Cloudflare translator experiment

This Worker exposes Cloudflare Workers AI's `@cf/meta/m2m100-1.2b` model to the
Glosify server. It is not called directly by the Chrome extension. Every
translation request requires a shared bearer secret, which protects the
account's Workers AI quota.

## Deploy

1. Install dependencies with `npm install`.
2. Sign in with `npx wrangler login`.
3. Create the secret with `npx wrangler secret put TRANSLATOR_TOKEN` and save
   the same generated value in Glosify's secret store.
4. Deploy with `npm run deploy`.
5. Configure Glosify without changing the production defaults:

   ```sh
   dotnet user-secrets set 'RealtimeTranslation:Cloudflare:Enabled' true --project ../Glosify
   dotnet user-secrets set 'RealtimeTranslation:Cloudflare:Endpoint' 'https://glosify-m2m100-translator.YOUR-SUBDOMAIN.workers.dev/translate' --project ../Glosify
   dotnet user-secrets set 'RealtimeTranslation:Cloudflare:ApiToken' 'THE-SAME-RANDOM-SECRET' --project ../Glosify
   ```

The third mode then appears as **Scribe + Cloudflare**. It translates live
Scribe revisions with a single in-flight request and a latest-wins pending
revision, then translates the finalized phrase. The user-facing partial-caption
toggle remains specific to Azure Scribe.

Use two-letter language codes when testing the Worker directly:

```sh
curl 'https://glosify-m2m100-translator.YOUR-SUBDOMAIN.workers.dev/translate' \
  -H 'Authorization: Bearer THE-SAME-RANDOM-SECRET' \
  -H 'Content-Type: application/json' \
  --data '{"text":"Hello, how are you?","source_lang":"en","target_lang":"fr"}'
```
