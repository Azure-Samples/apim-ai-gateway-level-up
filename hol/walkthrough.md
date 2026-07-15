# Session Four — Hands-on Lab: Front MCP servers with APIM (expose a REST API as MCP with OBO, and govern an existing MCP server)

In Sessions One and Two you fronted Azure AI Foundry with APIM and layered on the
AI-gateway policies. Session Four uses the same gateway to front **MCP servers** —
the protocol agents use to call tools.

> **Before you start:** complete the **Deploy & run** steps in the
> [README](../README.md) so an APIM instance exists in your resource group. For
> this session you also need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
> and [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local).

This lab covers **three complementary patterns** for fronting agent infrastructure with Azure API Management (APIM), the same AI Gateway you used in Sessions 1 and 2 — two for **MCP** (the protocol agents use to call *tools*) and one for **A2A** (the protocol agents use to talk to *other agents*):

1. **Expose-as-MCP** — take a REST API managed in APIM and expose its operations as **MCP tools**, secured with **Microsoft Entra** (OAuth 2.0 Protected Resource Metadata + On-Behalf-Of token exchange). You'll deploy a small **.NET 8 Function App** with two endpoints — `GET /echo` (pass-through) and `GET /me` (calls Microsoft Graph `/me` using an OBO token) — and front it as an MCP server.
2. **Passthrough-MCP** — put APIM in front of an **existing external MCP server** (the public **DeepWiki MCP server**) so you can apply gateway policies (rate limiting, tracing, auth) to a third-party MCP server you don't own.
3. **A2A agent** — deploy a small **.NET 8 "Summarizer" agent** (serves an Agent Card + a JSON-RPC `message/send` endpoint, backed by the Foundry model) and import it into APIM as an **A2A Agent API**. APIM mediates the agent card and governs agent-to-agent traffic.

```
Pattern 1 (expose-as-MCP)
  MCP Client ──(Entra token, access_mcp)──► APIM (MCP server) ──► Function App ──► Microsoft Graph /me
                                              │  validate token + OBO exchange
                                              └─ /.well-known/oauth-protected-resource (PRM)

Pattern 2 (passthrough-MCP)
  MCP Client ──► APIM (MCP server) ──(policies: rate-limit, trace)──► https://mcp.deepwiki.com/mcp

Pattern 3 (A2A agent)
  A2A Client ──(subscription key)──► APIM (A2A Agent API) ──► Agent (Function App) ──► Foundry model
                                       │  mediates agent card, rate-limits, OTel agent traces
```

> To test the MCP servers you build in this lab, use **VS Code with GitHub Copilot (agent mode)** — it performs the OAuth sign-in and token handling for you. The A2A agent can be tested with `curl` or any A2A client.

Throughout, replace angle-bracket placeholders (e.g. `<your-apim-name>`, `<tenant-id>`) with values from your deployment.

---

## Pattern 1 — Expose a REST API as a secured MCP server

### 1. Create the two Entra app registrations

The OBO flow needs a **client app** (used by the MCP client to sign the user in) and a **backend API app** (defines the `access_mcp` scope and performs the OBO exchange).

#### App 1 — Client app (MCP client)

1. In the [Azure Portal](https://portal.azure.com): **Microsoft Entra ID → App registrations → New registration**.
2. Name it `mcp-client`, leave the redirect URI blank, and **Register**.
3. Note the **Application (client) ID** — you'll add this to **App 2**'s *Authorized client applications* below.
4. **Authentication → Advanced settings → Allow public client flows → Yes** (VS Code signs in as a public client via PKCE).
5. No client secret is needed for this app.

#### App 2 — Backend API app (OBO middle-tier)

1. Register a new app `mcp-backend-api`.
2. Note its **Application (client) ID** → this is your `oboClientId`, referred to below as `<mcp-backend-client-id>`.
3. **Expose an API:**
   - Set the **Application ID URI** to `api://<oboClientId>` → this is your `mcpClientAudience`.
   - Add a scope named `access_mcp` (display name e.g. "Access MCP Server"), consent **Admins and users**.
   - Under **Authorized client applications**, add App 1's client ID and authorize it for `access_mcp`.
4. **API permissions:** add **Microsoft Graph → User.Read** (delegated) and **Grant admin consent**.
5. **Certificates & secrets:** create a new client secret → this is your `oboClientSecret`.

Grab your tenant ID with `az account show --query tenantId -o tsv` → this is your `entraIdTenantId`.

### 2. Deploy the Session 4 infrastructure

This is your **single Session 4 deploy** — it stands up everything for all three patterns at once. The MCP Function App and the MCP APIs are gated behind `oboClientId`, so pass all of the session-4 parameters. The A2A agent deploys automatically as part of Session 4. Pass the secret on the CLI so it's never committed:

```bash
az deployment group create \
  --resource-group <your-resource-group> \
  --template-file infra/main.bicep \
  --parameters infra/main.bicepparam \
  --parameters apimPublisherEmail=you@example.com \
               entraIdTenantId=<tenant-id> \
               oboClientId=<mcp-backend-client-id> \
               oboClientSecret=<mcp-backend-secret> \
               mcpClientAudience=api://<mcp-backend-client-id>
```

Everything else (location, `namePrefix`, model deployment, Redis, Content Safety, App Insights) comes from `infra/main.bicepparam`, so you only spell out the sensitive values above.

This provisions:

- The **MCP Function App** (Flex Consumption + storage + a user-assigned identity) and three APIs on APIM: the REST API (`mcp-function-app`), the PRM endpoint (`mcp-auth`), and the MCP server (`obo-mcp-server`), plus the named values the policies use.
- The **A2A agent Function App**, whose identity is granted **Cognitive Services OpenAI User** on Foundry so it can call the model.

Note the deployment outputs: `mcpFunctionAppName`, `mcpServerUrl`, `a2aAgentFunctionAppName`, and `a2aAgentCardUrl`. You'll deploy code to both Function Apps and use these values in Patterns 1 and 3.

### 3. Deploy the Function App code

Bicep provisions the Function App but not your code. Publish it with the Core Tools:

```bash
cd src/mcp-functionapp
func azure functionapp publish <mcpFunctionAppName> --dotnet-isolated
```

> **If publish fails with `403 (This request is not authorized...)` on a storage/blob upload:** this is a first-deploy timing issue, not a config error. The deployment storage account has shared-key auth disabled, so the Function App's managed identity must upload the package via Entra RBAC — and the **Storage Blob Data** role assignments the template creates can take **5–10 minutes to propagate**. Wait a few minutes and re-run the same command. To confirm the roles landed, run:
> ```bash
> MI_PRINCIPAL=$(az identity show -g <your-resource-group> -n <mcpFunctionAppName-without-mcpfunc>-mcp-id-<suffix> --query principalId -o tsv)
> STG_ID=$(az storage account show -g <your-resource-group> -n stmcp<suffix> --query id -o tsv)
> az role assignment list --assignee "$MI_PRINCIPAL" --scope "$STG_ID" -o table
> ```

Sanity-check the pass-through endpoint directly (Echo requires no auth):

```bash
curl "https://<mcpFunctionAppName>.azurewebsites.net/api/echo?name=World"
# → {"message":"Echo: World"}
```

### 4. How the pieces fit — the policies

Three policy files (in `infra/policies/`) implement the security model:

- **`mcp-api-policy.xml`** (API-level on `obo-mcp-server`) — validates the inbound Entra JWT with `validate-azure-ad-token` (audience = `{{mcp-client-audience}}`). On `401`, it returns a `WWW-Authenticate` header pointing MCP clients at the PRM endpoint, so they can discover how to authenticate:

  ```
  WWW-Authenticate: Bearer error="invalid_token", resource_metadata="https://<apim>/.well-known/oauth-protected-resource/obo-mcp-server/mcp"
  ```

- **`mcp-prm-policy.xml`** (on `mcp-auth`) — anonymous. Returns the OAuth 2.0 **Protected Resource Metadata** (RFC 9728): the resource URL, the Entra authorization server, and the `api://<oboClientId>/access_mcp` scope the client must request.

- **`obo-getme-policy.xml`** (operation-level on `getMe`) — takes the caller's `access_mcp` token and performs the **On-Behalf-Of** exchange against Entra to get a **Microsoft Graph `User.Read`** token, then forwards it to the Function App. The `echo` tool has no such policy — it's a plain pass-through.

### 5. Test the MCP server

Add the MCP server in **VS Code** (GitHub Copilot agent mode). On first use VS Code follows the `WWW-Authenticate` → PRM discovery flow, prompts you to sign in with Entra, and attaches the `access_mcp` token automatically — no manual token step needed:

1. Command Palette → **MCP: Add Server** → **HTTP (HTTP or Server Sent Events)**.
2. Server URL: `https://<your-apim-name>.azure-api.net/obo-mcp-server/mcp`
3. Give it a Server ID and save to workspace or user settings.

Then, in Copilot agent mode, invoke the tools:

- `echo` — returns `Echo: <name>`.
- `getMe` — triggers the OBO exchange and returns your Microsoft Graph profile.

---

## Pattern 2 — Govern an existing external MCP server (passthrough)

APIM can front an **existing** remote MCP server and apply gateway policies to it. Here you'll proxy the public **DeepWiki MCP server** (`https://mcp.deepwiki.com/mcp`), which requires no auth, uses streamable HTTP, and answers questions about any public GitHub repository. This is configured live in the portal (it targets an external server, so there's nothing to deploy).

> The external MCP server must conform to MCP version `2025-06-18` or later — the DeepWiki MCP server does.

### 1. Create the passthrough MCP server

1. APIM → **APIs → MCP servers → + Create MCP server**.
2. Select **Expose an existing MCP server**.
3. **Backend MCP server:**
   - **MCP server base URL:** `https://mcp.deepwiki.com/mcp`
   - **Transport type:** **Streamable HTTP** (default).
4. **New MCP server:**
   - **Name:** `deepwiki-mcp`
   - **Base path:** `deepwiki` (this becomes the route prefix).
5. **Create.** APIM imports the remote server's tools (e.g. `ask_question`, `read_wiki_structure`, `read_wiki_contents`) and lists it with a **Server URL** like `https://<your-apim-name>.azure-api.net/deepwiki-mcp/mcp`.

### 2. Add a governance policy

The whole point of putting APIM in front is to apply gateway policies. In the MCP server's **Policies** editor, add an inbound rate limit and a trace — these apply to every tool call:

```xml
<inbound>
    <base />
    <rate-limit-by-key calls="2" renewal-period="300"
        counter-key="@(context.Request.IpAddress)"
        remaining-calls-variable-name="remainingCallsPerIP" />
    <trace source="deepwiki-mcp" severity="information">
        <message>DeepWiki MCP tool call</message>
        <metadata name="agent-id" value="@(context.Request.Headers.GetValueOrDefault("agent-id", "n/a"))" />
    </trace>
</inbound>
```

> **Caution:** don't read `context.Response.Body` in MCP server policies — it forces response buffering and breaks the streaming MCP servers require.

> **Why these numbers?** `rate-limit-by-key` enforcement is **approximate**, not an exact gate on call N+1. The gateway counts per worker and reconciles the shared counter on a short delay, so a burst can slip through roughly **2× the limit** before `429`s appear. Agent tool calls also arrive slowly (~20–40 s apart, since each call waits for the model's answer), so a short window lets earlier calls age out before the count accumulates. A low `calls` with a long `renewal-period` (300 s is the max) keeps the spaced-out calls counted together, so the limit trips reliably through normal chat — here, around the 3rd–4th call.

### 3. Test the passthrough

Add `https://<your-apim-name>.azure-api.net/deepwiki-mcp/mcp` as an HTTP MCP server in VS Code (same steps as Pattern 1, step 5). Ask Copilot several questions about a public GitHub repo (e.g. *"Using deepwiki, how does routing work in the `vercel/next.js` repo?"*) that trigger `ask_question`. With `calls=2` over a 5-minute window, the request that crosses the limit (around the 3rd–4th call from the same IP) is rejected by APIM and surfaces as a failed tool call in Copilot. In App Insights the blocked request shows up as a failed request rather than a `200`.

---

## Pattern 3 — Govern an A2A agent

Here you deploy a minimal **A2A "Summarizer" agent** and put APIM in front of it as an **A2A Agent API**. The agent hosts an [Agent Card](https://a2a-protocol.org/dev/specification/#5-agent-discovery-the-agent-card) at `/.well-known/agent-card.json` and a JSON-RPC endpoint at `/a2a` that implements `message/send` — it takes the caller's text and returns a summary produced by the Foundry model (called keyless with the agent's managed identity).

When you import it, APIM **mediates the agent card** (rewrites the endpoint to APIM's hostname, forces JSON-RPC transport, and injects the subscription-key requirement) and governs the JSON-RPC traffic with policies. With Application Insights enabled, APIM also emits OpenTelemetry GenAI agent attributes (`gen_ai.agent.id`, `gen_ai.agent.name`).

### 1. Deploy the agent

You already provisioned the agent Function App in **Pattern 1, step 2** — that single Session 4 deploy always includes the A2A agent, granted its identity **Cognitive Services OpenAI User** on Foundry, and produced the `a2aAgentFunctionAppName` and `a2aAgentCardUrl` outputs. No extra deploy is needed here; grab those two output values and continue.

### 2. Publish the agent code

```bash
cd src/a2a-agent
func azure functionapp publish <a2aAgentFunctionAppName> --dotnet-isolated
```

> The same first-deploy `403` storage propagation note from Pattern 1, step 3 applies here — wait a few minutes and retry if publish fails on a blob upload.

Sanity-check the agent directly (before APIM), using the outputs:

```bash
# Agent card
curl "https://<a2aAgentFunctionAppName>.azurewebsites.net/.well-known/agent-card.json"

# A2A message/send (JSON-RPC)
curl -X POST "https://<a2aAgentFunctionAppName>.azurewebsites.net/a2a" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": "1",
    "method": "message/send",
    "params": {
      "message": {
        "kind": "message",
        "role": "user",
        "messageId": "m1",
        "parts": [{ "kind": "text", "text": "Summarize: Azure API Management can front models, MCP tools, and A2A agents with one set of governance policies." }]
      }
    }
  }'
# → {"jsonrpc":"2.0","id":"1","result":{"kind":"message","role":"agent","parts":[{"kind":"text","text":"..."}], ...}}
```

### 3. Import the agent into APIM

1. APIM → **APIs → + Add API** → the **A2A Agent** tile.
2. **Agent card → URL:** paste your `a2aAgentCardUrl`
   (`https://<a2aAgentFunctionAppName>.azurewebsites.net/.well-known/agent-card.json`). Select **Next**.
3. On **Create an A2A agent API**, APIM reads the **Runtime URL** and **Agent ID** from the card (adjust if needed). Set:
   - **Display name:** `Summarizer Agent`
   - **Base path:** `summarizer`
4. **Create.** On the API's **Overview** page APIM shows a **Runtime base URL** (for JSON-RPC calls) and an **Agent card URL** — both served through APIM.

> **About the mediated card path.** APIM serves the card at the fixed **Agent card URL** shown on the Overview page — `https://<your-apim-name>.azure-api.net/summarizer/agent-card.json` (note: **no** `.well-known` segment, and this isn't configurable). A2A clients should consume this explicit Agent card URL rather than relying on `/.well-known/agent-card.json` auto-discovery. Your agent still hosts its own card at `/.well-known/agent-card.json`; APIM re-serves a mediated copy (hostname rewritten to APIM, transport forced to JSON-RPC, subscription-key requirement injected) at its Agent card URL.

### 4. Govern the agent

A2A responses are JSON-RPC (not OpenAI-shaped), so `llm-emit-token-metric` doesn't apply here — token metrics belong on the *model* leg (the Foundry API from Session 2). Govern the **agent** leg with a subscription key, rate limiting, and tracing instead:

- **Require a subscription key:** the API's **Settings → Subscription required** is enabled by the import. Grab a key from a Product/subscription that includes this API.
- **Rate-limit + trace:** in the A2A agent API's **Policies**, add:

  ```xml
  <inbound>
      <base />
      <rate-limit-by-key calls="2" renewal-period="300"
          counter-key="@(context.Subscription?.Id ?? context.Request.IpAddress)"
          remaining-calls-variable-name="remaining" />
      <trace source="summarizer-agent" severity="information">
          <message>A2A message/send</message>
      </trace>
  </inbound>
  ```

  > **Note:** `rate-limit-by-key` enforcement is approximate and A2A calls are slow (each `message/send` waits on the model), so — as with Pattern 2 — a low `calls` with a long `renewal-period` (300 s max) is needed to trip the limit reliably. With `calls=2` the block lands around the 3rd–4th call. To trip a higher limit instead, fire the test calls in parallel (a `for … & wait` curl burst) so they land inside the window before the shared counter reconciles.

### 5. Test through APIM

Call the **APIM** base URL with your subscription key — same JSON-RPC body as step 2:

```bash
curl -X POST "https://<your-apim-name>.azure-api.net/summarizer" \
  -H "Content-Type: application/json" \
  -H "Ocp-Apim-Subscription-Key: <your-subscription-key>" \
  -d '{ "jsonrpc":"2.0","id":"1","method":"message/send",
        "params": { "message": { "kind":"message","role":"user","messageId":"m1",
          "parts":[{ "kind":"text","text":"Summarize: <your text here>" }] } } }'
```

Fetch the **mediated** agent card through APIM at its **Agent card URL** (the one shown on the Overview page — no `.well-known` segment) and note the `url` now points at APIM, not the Function App:

```bash
curl "https://<your-apim-name>.azure-api.net/summarizer/agent-card.json"
```

> **Want the model leg governed too?** The agent calls Foundry directly by default (`FOUNDRY_ENDPOINT` = the Foundry account endpoint). To route the agent's model calls through the APIM Foundry gateway from Session 2 instead, redeploy with the agent's `FOUNDRY_ENDPOINT` app setting pointed at your APIM Foundry URL — then *both* the agent leg and the model leg are governed by APIM.

---

## When to use which pattern

| | Expose-as-MCP | Passthrough-MCP | A2A agent |
| --- | --- | --- | --- |
| Protocol | MCP (tools) | MCP (tools) | A2A (agents) |
| Source | A REST API **you** manage in APIM | An MCP server hosted **elsewhere** | An agent **you** host (Agent Card + JSON-RPC) |
| Deployed here | Function App + APIM MCP APIs (Bicep) | Nothing — points at an external URL | Agent Function App (Bicep) + portal import |
| Auth / governance shown | Entra OBO + PRM discovery | Rate limit + trace | Subscription key + rate limit + OTel agent traces |
| Use it to | Turn existing APIs into governed agent tools | Put your gateway in front of third-party MCP tools | Govern agent-to-agent traffic through one endpoint |

All three give agents a single, **governed** endpoint fronted by the same AI Gateway policies you used for the Foundry API in Session 2.

---

## Reference

- [MCP server support in API Management](https://learn.microsoft.com/azure/api-management/mcp-server-overview)
- [Expose a REST API as an MCP server](https://learn.microsoft.com/azure/api-management/export-rest-mcp-server)
- [Connect and govern an existing MCP server](https://learn.microsoft.com/azure/api-management/expose-existing-mcp-server)
- [Import an A2A agent API](https://learn.microsoft.com/azure/api-management/agent-to-agent-api)
- [A2A protocol specification](https://a2a-protocol.org/dev/specification/)
- [OAuth 2.0 Protected Resource Metadata (RFC 9728)](https://datatracker.ietf.org/doc/html/rfc9728)
- [On-Behalf-Of flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow)
