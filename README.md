# APIM AI Gateway Level Up

A hands-on, level-up training repo that gets you up to speed with using **Azure API Management (APIM)** as an **AI Gateway** in front of **Azure OpenAI**. You'll build a very simple, end-to-end example you can create and implement yourself — from zero to a working, governed AI endpoint.

> **Who is this for?** Developers, architects, and platform engineers who want a fast, practical introduction to the APIM AI Gateway capabilities without wading through a large reference architecture first.

---

## What is the AI Gateway?

When teams start using Large Language Models (LLMs) like those in Azure OpenAI, they quickly hit the same set of challenges:

- **Cost control** — token usage can spike unexpectedly across many apps.
- **Throttling & fairness** — one noisy app shouldn't starve everyone else.
- **Security** — backend keys should never be handed out to client apps.
- **Observability** — who is calling, how much are they spending, and how fast?
- **Resilience** — load balancing and failover across multiple model deployments.

**Azure API Management** sits between your client applications and your Azure OpenAI (or other model) backends and solves these problems centrally. Acting as an *AI Gateway*, APIM gives you a single, governed front door for all of your AI traffic.

```
+-------------+        +------------------------+        +---------------------+
|   Client    |  --->  |  Azure API Management  |  --->  |   Azure OpenAI      |
|   App / SDK |        |     (AI Gateway)       |        |   (gpt-4o-mini)     |
+-------------+        +------------------------+        +---------------------+
                              |
                              +--> Token rate limiting
                              +--> Token usage metrics / logging
                              +--> Key & identity management
                              +--> (Optional) Semantic caching
                              +--> (Optional) Load balancing & failover
```

---

## What you'll build

In this level-up you will create a minimal but complete AI Gateway:

1. **Provision** an Azure API Management instance and an Azure OpenAI resource with a chat model deployment (e.g. `gpt-4o-mini`).
2. **Import** the Azure OpenAI API into APIM so it's exposed as a managed API.
3. **Secure** the backend by keeping the Azure OpenAI key inside APIM and issuing APIM subscription keys to clients.
4. **Govern** traffic by applying the **token limit** policy and the **emit token metric** policy.
5. **Call** your new gateway endpoint and watch the policies and metrics in action.

By the end you'll have a single endpoint that your apps can call exactly like Azure OpenAI — but with cost, security, and observability handled for you.

---

## Prerequisites

| Requirement | Notes |
|-------------|-------|
| Azure subscription | With permission to create APIM and Azure OpenAI resources |
| Access to Azure OpenAI | [Request access](https://aka.ms/oai/access) if you don't have it yet |
| [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) | `az login` and select your subscription |
| A REST client | `curl`, [Bruno](https://www.usebruno.com/), Postman, or VS Code REST Client |

> 💡 **Cost note:** APIM has a [free/Developer tier](https://azure.microsoft.com/pricing/details/api-management/) suitable for learning. Azure OpenAI is billed per token. Remember to clean up resources when you're done.

---

## Quickstart

> The steps below are intentionally high-level so you can learn by doing. Detailed, copy-pasteable scripts live in the [`infra/`](./infra) and [`docs/`](./docs) folders (to be added as the repo grows).

### 1. Sign in and set variables

```bash
az login
az account set --subscription "<your-subscription-id>"

RG=rg-apim-ai-levelup
LOCATION=eastus
```

### 2. Create the resource group

```bash
az group create --name $RG --location $LOCATION
```

### 3. Deploy Azure OpenAI + a model deployment

Create an Azure OpenAI resource and deploy a chat model (for example `gpt-4o-mini`). Note the **endpoint** and **key**.

### 4. Deploy Azure API Management

Create an APIM instance (Developer or Basic v2 tier is fine for learning). This can take a little while to provision.

### 5. Import Azure OpenAI into APIM

In the APIM portal, use **APIs → Add API → Azure OpenAI Service** to import your Azure OpenAI resource. APIM will create the operations and let you store the backend key as a named value/secret.

### 6. Add AI Gateway policies

Apply these policies to the API to turn APIM into an AI Gateway:

- [`azure-openai-token-limit`](https://learn.microsoft.com/azure/api-management/azure-openai-token-limit-policy) — cap tokens per subscription/time window.
- [`azure-openai-emit-token-metric`](https://learn.microsoft.com/azure/api-management/azure-openai-emit-token-metric-policy) — emit token-usage metrics to Application Insights.

### 7. Call your gateway

```bash
curl -X POST "https://<your-apim-name>.azure-api.net/openai/deployments/gpt-4o-mini/chat/completions?api-version=2024-02-01" \
  -H "Content-Type: application/json" \
  -H "api-key: <your-APIM-subscription-key>" \
  -d '{
    "messages": [
      { "role": "system", "content": "You are a helpful assistant." },
      { "role": "user", "content": "In one sentence, what is an AI gateway?" }
    ]
  }'
```

Notice that clients use the **APIM subscription key** — never the Azure OpenAI key.

### 8. Clean up

```bash
az group delete --name $RG --yes --no-wait
```

---

## Repository structure

As the training evolves, the repo will be organized like this:

```
.
├── README.md            # You are here
├── LICENSE              # MIT
├── CONTRIBUTING.md      # How to contribute
├── CODE_OF_CONDUCT.md   # Microsoft Open Source Code of Conduct
├── SECURITY.md          # How to report security issues
├── docs/                # Step-by-step guides and explanations
└── infra/               # Bicep/Terraform to provision the example
```

> `docs/` and `infra/` are placeholders for upcoming content — the README is the starting point for the level-up.

---

## Going further

Once the basics click, explore the rest of the APIM AI Gateway capabilities:

- **Semantic caching** to cut cost and latency for repeated prompts.
- **Load balancing & circuit breakers** across multiple Azure OpenAI deployments.
- **Token-based rate limiting** per product, subscription, or app.
- **Content safety** integration.
- **Self-hosted gateways** and multi-region deployments.

Helpful references:

- [APIM AI Gateway overview](https://learn.microsoft.com/azure/api-management/genai-gateway-capabilities)
- [AI Gateway policies reference](https://learn.microsoft.com/azure/api-management/api-management-policies#ai-gateway)
- [AI-Gateway samples & labs (GitHub)](https://github.com/Azure-Samples/AI-Gateway)

---

## Contributing

Contributions are welcome! Please read [CONTRIBUTING.md](./CONTRIBUTING.md) before opening a pull request.

## License

This project is licensed under the terms of the [MIT License](./LICENSE).

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft trademarks or logos is subject to and must follow [Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general). Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship. Any use of third-party trademarks or logos is subject to those third-parties' policies.
