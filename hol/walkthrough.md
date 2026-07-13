# Session Two — Hands-on Lab: AI Gateway Policies (Outline)

In Session One you fronted Azure AI Foundry with APIM and proved the gateway path
end to end. Session Two builds on that working gateway to explore the **AI-gateway
policies** that make APIM more than a passthrough: token governance, observability,
caching, safety, and resiliency — all applied at the gateway, with no client changes.

> **Before you start:** complete the **Deploy & run** steps in the
> [README](../README.md). Unlike Session One, the `FoundryPortal` API,
> `foundry-backend`, and managed-identity routing policy are **already deployed**
> by the Bicep template — so you begin with a working APIM → Foundry gateway.

> **This is an outline.** Each section below is a placeholder to be fleshed out with
> step-by-step instructions, policy snippets, and screenshots.

---

## 0. Starting point — confirm the gateway works

- Confirm the deployed resources: `FoundryPortal` API (path `/foundry`),
  `foundry-backend`, and the API-level managed-identity policy.
- Point the chat app at the APIM gateway URL
  (`https://<your-apim-name>.azure-api.net/foundry`) and send a test prompt.
- _TODO:_ note on subscription key (the API is `subscriptionRequired: true`) — where
  to get a key and how to pass it.

## 1. Rate limiting — cap the number of calls

- Add `<rate-limit>` / `<rate-limit-by-key>` to throttle request counts.
- Demo: send N+1 calls quickly → observe **429 Too Many Requests**.
- _TODO:_ policy snippet, counter-key options (subscription vs IP vs custom).

## 2. Token limiting — cap token consumption

- Use `<azure-openai-token-limit>` (a.k.a. `<llm-token-limit>`) to cap tokens
  per minute instead of raw call counts.
- Cover prompt vs completion tokens, `estimate-prompt-tokens`, and response headers
  that report remaining tokens.
- _TODO:_ policy snippet + demo showing the 429 once the budget is exhausted.

## 3. Token usage metrics — observability

- Emit token metrics with `<azure-openai-emit-token-metric>` to Application Insights.
- Dimensions: subscription/app/model, prompt/completion/total tokens.
- _TODO:_ wire up App Insights, sample KQL queries, and a chart of token usage by client.

## 4. Semantic caching — cut cost and latency

- Use `<azure-openai-semantic-cache-lookup>` / `<azure-openai-semantic-cache-store>`
  with an embeddings backend + external cache (Redis).
- Demo: ask semantically similar questions → second hit served from cache.
- _TODO:_ prerequisites (embeddings deployment, Redis), similarity threshold, snippet.

## 5. Content safety — guard inputs

- Apply `<llm-content-safety>` to screen prompts against Azure AI Content Safety
  (hate, violence, self-harm, sexual) before they reach the model.
- Demo: a blocked prompt returns a safety error; a clean prompt passes.
- _TODO:_ Content Safety resource setup, category/severity thresholds, snippet.

## 6. Load balancing & resiliency — backend pools + circuit breaker

- Define a **backend pool** across multiple Foundry deployments/regions with
  round-robin / weighted / priority routing.
- Add a **circuit breaker** to trip on repeated failures and fail over.
- Demo: take one backend offline → traffic shifts automatically.
- _TODO:_ pool definition, circuit-breaker rules, retry policy.

## 7. Putting it together — a layered policy

- Compose the above into a single, ordered inbound policy and discuss ordering
  (safety → limits → cache → route).
- _TODO:_ full combined policy listing + before/after metrics.

---

## Clean up

See **Clean up** in the [README](../README.md) — delete the resource group, then
purge the soft-deleted APIM and Foundry accounts.
