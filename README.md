# APIM AI Gateway Level Up

A hands-on training repo for using **Azure API Management (APIM)** as an **AI Gateway** in front of **Azure AI Foundry**. You deploy a small starter (APIM + Foundry + a `gpt-4.1-mini` model + a tiny chat app), then wire it through the gateway live during the session.

## Why an AI Gateway?

APIM sits between your apps and your model backends to centrally handle **cost control**, **token rate limiting**, **key/identity management**, **observability**, and **load balancing/failover** — so clients call one governed endpoint instead of the model directly.

```
Client app  ──►  Azure API Management (AI Gateway)  ──►  Azure AI Foundry (gpt-4.1-mini)
```

The same gateway can also front **MCP servers** (expose REST APIs as agent tools) and **A2A agents** (govern agent-to-agent traffic) with the same policies.

## Session agenda

This repo is organized **by branch** — each session has its own branch containing just the starter code and walkthrough for that session. The **`main`** branch is the **"full" version**: it contains everything needed across all sessions combined.

Each session branch has the matching starter code and walkthrough:

| Session | Topic |
| --- | --- |
| [Session 1](https://github.com/Azure-Samples/apim-ai-gateway-level-up/tree/session-one) | APIM intro, APIM AI abilities, and AI Foundry |
| [Session 2](https://github.com/Azure-Samples/apim-ai-gateway-level-up/tree/session-two) | Focusing on AI Gateway policies + demo |
| [Session 3](https://github.com/Azure-Samples/apim-ai-gateway-level-up/tree/session-three) | MCP + A2A |
| [Session 4](https://github.com/Azure-Samples/apim-ai-gateway-level-up/tree/session-four) | MCP + A2A hands-on / demo + customer use cases |
| [Session 5](https://github.com/Azure-Samples/apim-ai-gateway-level-up/tree/session-five) | New AI Gateway |
| [Session 6](https://github.com/Azure-Samples/apim-ai-gateway-level-up/tree/session-six) | API Center |

## What's in this repo

- **[`infra/`](./infra)** — one Bicep template (`main.bicep`) that provisions:
  - **APIM Standard V2** with a system-assigned managed identity
  - **Azure AI Foundry** account (`AIServices`) + a **Foundry project**
  - a **`gpt-4.1-mini`** deployment
  - a **`text-embedding-ada-002`** deployment
  - a role assignment giving APIM's identity **Cognitive Services OpenAI User** on Foundry
  - an **optional** role assignment giving a principal you pass in (`inferenceUserPrincipalId`) the same role, so you can test locally
  - **Azure Managed Redis** (Redis Enterprise, `Balanced_B0`, RediSearch enabled)
  - **Azure AI Content Safety** (Cognitive Services account, kind `ContentSafety`, `S0`)
  - **Application Insights** (workspace-based) + backing **Log Analytics workspace**

  > The APIM API import and AI-gateway policies are added live during the session — not in the template.

- **[`src/chatapp/`](./src/chatapp)** — a minimal **.NET 10** app (Minimal API + one static page) that chats with the model via the **`Azure.AI.OpenAI`** SDK and **`DefaultAzureCredential`** (no keys). The page has an **editable endpoint field** so you can switch from the Foundry URL to the APIM URL without code changes, plus a **Check access (debug)** button that calls `/openai/models` to confirm your identity has data-plane access.

- **[`src/mcp-functionapp/`](./src/mcp-functionapp)** — a minimal **.NET 8 isolated** Azure Function App (`GET /echo`, `GET /me`) used in **Session 4** to demo APIM as an **MCP server**. `infra/main.bicep` optionally provisions the Function App and the MCP APIs (an MCP-type API exposing the operations as tools, an OAuth Protected Resource Metadata endpoint, and an On-Behalf-Of token-exchange policy) when you pass the session-4 parameters. See **[`hol/walkthrough.md`](./hol/walkthrough.md)** for the full walkthrough, which also covers fronting an existing external MCP server (the Microsoft Learn MCP server) as a governed passthrough.

- **[`src/a2a-agent/`](./src/a2a-agent)** — a minimal **.NET 8 isolated** Azure Function App implementing a small **A2A "Summarizer" agent** (serves an Agent Card at `/.well-known/agent-card.json` and a JSON-RPC `message/send` endpoint at `/a2a`, backed by the Foundry model via managed identity). Used in **Session 4** to demo importing an agent into APIM as an **A2A Agent API**. `infra/main.bicep` provisions the agent Function App as part of the Session 4 deploy; the APIM import is a portal step covered in **[`hol/walkthrough.md`](./hol/walkthrough.md)**.

## Prerequisites

- Azure subscription (rights to create APIM + AI Foundry), [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and `gpt-4.1-mini` + `text-embedding-ada-002` availability in your region.
- For **Session 4 (MCP)** you also need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local) to build and publish the Function App.

## Deploy & run

```bash
# 1. Sign in and create a resource group
az login
az account set --subscription "<subscription-id>"
RG=rg-apim-ai-levelup
az group create --name $RG --location eastus2

# 2. Deploy infra. Pass your admin email inline (so it's never committed) and your
#    object ID so the deploy grants you Cognitive Services OpenAI User on Foundry.
#    Get your logged-in user's object ID (OID) with:
#       az ad signed-in-user show --query id -o tsv
az deployment group create -g $RG \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam \
  --parameters inferenceUserPrincipalId="$(az ad signed-in-user show --query id -o tsv)" \
  --parameters apimPublisherEmail="you@example.com"
# APIM Standard V2 can take ~15–30 min. The role grant can take a further
# ~15–20 min to be usable for inference (data-plane RBAC propagation).

# 3. Run the app (then follow the walkthrough to test and wire up the gateway)
cd src/chatapp && dotnet run
```

Once the app is running, follow the **[hands-on walkthrough](./hol/walkthrough.md)**: it walks you through testing the chat against Foundry directly, importing the Foundry endpoint as an API in APIM with a managed-identity policy, then switching the app to the APIM gateway URL.

## Clean up

Deleting the resource group is **not enough** — both **APIM** and **Azure AI Foundry (Cognitive Services)** are *soft-deleted* and keep reserving their names (and incurring some retention) until purged. Delete the group first, then purge both — otherwise the names can't be reused. (Capture the names *before* deleting; once the group is gone you can recover them with the list commands below.)

```bash
# Capture the resource names BEFORE you delete the group
APIM_NAME=$(az deployment group show -g $RG -n main --query properties.outputs.apimName.value -o tsv)
FOUNDRY_NAME=$(az deployment group show -g $RG -n main --query properties.outputs.foundryAccountName.value -o tsv)
LOCATION=eastus2

# 1. Delete the resource group and WAIT (so the soft-deleted entries exist before purge)
az group delete --name $RG --yes

# 2. Purge the soft-deleted APIM instance
az apim deletedservice purge --service-name $APIM_NAME --location $LOCATION

# 3. Purge the soft-deleted Foundry (Cognitive Services) account
az cognitiveservices account purge --name $FOUNDRY_NAME --resource-group $RG --location $LOCATION
```

If you already deleted the group and don't have the names, list what's pending purge:

```bash
az apim deletedservice list -o table
az cognitiveservices account list-deleted -o table
```

## Going further

[AI Gateway overview](https://learn.microsoft.com/azure/api-management/genai-gateway-capabilities) · [AI-gateway policies](https://learn.microsoft.com/azure/api-management/api-management-policies#ai-gateway) · [AI-Gateway samples](https://github.com/Azure-Samples/AI-Gateway) · [MCP in APIM](https://learn.microsoft.com/azure/api-management/export-rest-mcp-server) · [A2A in APIM](https://learn.microsoft.com/azure/api-management/agent-to-agent-api) · [Create Foundry Quickstart](https://learn.microsoft.com/en-us/azure/foundry/tutorials/quickstart-create-foundry-resources?tabs=azurecli)

## Contributing & License

Contributions welcome — see [CONTRIBUTING.md](./CONTRIBUTING.md). Licensed under the [MIT License](./LICENSE).

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft trademarks or logos is subject to and must follow [Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general). Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship. Any use of third-party trademarks or logos is subject to those third-parties' policies.
