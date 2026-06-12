# APIM AI Gateway Level Up

A hands-on training repo for using **Azure API Management (APIM)** as an **AI Gateway** in front of **Azure AI Foundry**. You deploy a small starter (APIM + Foundry + a `gpt-4.1-mini` model + a tiny chat app), then wire it through the gateway live during the session.

## Why an AI Gateway?

APIM sits between your apps and your model backends to centrally handle **cost control**, **token rate limiting**, **key/identity management**, **observability**, and **load balancing/failover** — so clients call one governed endpoint instead of the model directly.

```
Client app  ──►  Azure API Management (AI Gateway)  ──►  Azure AI Foundry (gpt-4.1-mini)
```

The same gateway can also front **MCP servers** (expose REST APIs as agent tools) and **A2A agents** (govern agent-to-agent traffic) with the same policies.

## What's in this repo

- **[`infra/`](./infra)** — one Bicep template (`main.bicep`) that provisions:
  - **APIM Standard V2** with a system-assigned managed identity
  - **Azure AI Foundry** account (`AIServices`) + a **Foundry project**
  - a **`gpt-4.1-mini`** deployment
  - a role assignment giving APIM's identity **Cognitive Services OpenAI User** on Foundry
  - an **optional** role assignment giving a principal you pass in (`inferenceUserPrincipalId`) the same role, so you can test locally

  > The APIM API import and AI-gateway policies are added live during the session — not in the template.

- **[`src/chatapp/`](./src/chatapp)** — a minimal **.NET 10** app (Minimal API + one static page) that chats with the model via the **`Azure.AI.OpenAI`** SDK and **`DefaultAzureCredential`** (no keys). The page has an **editable endpoint field** so you can switch from the Foundry URL to the APIM URL without code changes, plus a **Check access (debug)** button that calls `/openai/models` to confirm your identity has data-plane access.

## Prerequisites

- Azure subscription (rights to create APIM + AI Foundry), [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), and `gpt-4.1-mini` availability in your region.

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
