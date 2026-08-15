# Stripe payments

Glosify’s first Stripe product is a one-time purchase of AI credit packs. The
application creates Stripe-hosted Checkout Sessions from server-side package
configuration and fulfills paid sessions into the existing AI credit ledger.

## Dashboard setup

1. In Stripe test mode, create one Product and one one-time Price for each credit
   pack. Copy each `price_...` ID.
2. Configure the public Checkout branding in Stripe.
3. Create a restricted API key with permission to read Prices and PaymentIntents,
   and to create and read Checkout Sessions. PaymentIntent metadata lets Glosify
   reconcile a refund or dispute even if Stripe delivers that event before the
   Checkout completion event. Keep the key in user secrets or Azure App Service
   settings; never commit it.
4. Create a webhook endpoint at
   `https://glosify.se/Payments/Webhook` for:
   - `checkout.session.completed`
   - `checkout.session.async_payment_succeeded`
   - `refund.created`
   - `refund.updated`
   - `charge.dispute.created`
   - `charge.dispute.closed`
5. Set the webhook endpoint API version to `2026-06-24.dahlia`, which is the
   version pinned by Stripe.net 52.1.1. Stripe's .NET webhook deserializer rejects
   events generated with a different API version. Keep this setting aligned with
   `StripeConfiguration.ApiVersion` whenever Stripe.net is upgraded.
6. Copy the webhook signing secret (`whsec_...`) into the deployment settings.

Glosify deliberately does not enable Stripe Tax automatically. Before charging
real customers, decide where the business is registered for VAT/GST and configure
Stripe Tax and registrations accordingly.

## Configuration

The checked-in defaults keep payments disabled, but ship the nonsecret production
URL and live package catalog. A deployment only needs to provide the
environment-specific enable flag and secrets:

```text
Stripe__Enabled=true
Stripe__SecretKey=rk_test_...
Stripe__WebhookSecret=whsec_...
```

Override `Stripe__PublicBaseUrl` and `Stripe__CreditPackages__...` only for a
separate test or staging catalog. Keeping the live nonsecret mappings in shipped
configuration prevents an App Service settings edit from silently removing the
purchase catalog.

The package key, credit amount, price amount, and currency are controlled by
Glosify, not by the browser or Stripe metadata. Before creating Checkout, Glosify
retrieves the configured Stripe Price and requires it to be active, one-time, and
an exact amount/currency match. The webhook and success page verify the paid total
again before fulfillment. The purchase ID is stored in Checkout metadata and in
the ledger uniquely, so a retried event cannot grant credits twice.

Successful refunds revoke credits proportionally, rounding up to avoid retaining
an unearned fractional credit. An open or lost dispute revokes the full purchase;
winning it restores the non-refunded portion. These adjustments are idempotent and
can make a balance negative, which prevents further paid usage while preserving
the historical usage ledger.

## Local testing

Use the Stripe CLI to forward signed test events to the local endpoint:

```bash
stripe login
stripe listen --forward-to https://localhost:5001/Payments/Webhook
```

Set the `whsec_...` printed by `stripe listen` in local user secrets, then use a
Stripe test card in Checkout. The CLI and Checkout testing guidance are in the
[Stripe documentation](https://docs.stripe.com/stripe-cli).
