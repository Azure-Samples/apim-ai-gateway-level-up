# Session One — Hands-on Lab: Front Azure AI Foundry with APIM

In this lab you'll import your Foundry endpoint as an API in APIM, add a policy
so APIM authenticates to Foundry with its **managed identity**, and prove it end
to end with the chat app — first calling Foundry directly, then through the
gateway.

> **Before you start:** complete the **Deploy & run** steps in the
> [README](../README.md) first — infra deployed and the chat app running locally.

---

## 1. Get the Foundry endpoint and test the chat directly

First, prove the model works by calling Foundry directly from the app.

1. Get your Foundry endpoint (run from the repo root, with `RG` set to your
   resource group):

   ```bash
   az deployment group show -g $RG -n main --query properties.outputs.foundryEndpoint.value -o tsv
   ```

2. The chat app should already be running — open it in your browser
   (`http://localhost:5152`).
3. Paste the **Foundry endpoint** into the **Endpoint** field at the top, and
   confirm the **Deployment** is `gpt-4.1-mini`.
4. Click **Check access (debug)** — you should get a **200**.
   - If you get a **401/403**, your role assignment just hasn't propagated yet
     (data-plane RBAC can take ~15–20 min). Wait a few minutes and try again.
5. Paste this prompt into the message box and hit **Send**:

   ``` text
   Complete the following sentence with one word: It is a beautiful day outside let's go to the
   ```

   You should get a response. A **401** here (with a 200 on the debug check) also
   means propagation — wait a bit and resend.

> **Heads-up — two different endpoints:** the Foundry **portal playground** shows a
> *project* endpoint like `https://<name>.services.ai.azure.com/api/projects/<project>`,
> which is used by the Azure AI Foundry project SDK and the Agent service. We
> deliberately use the **Azure OpenAI data-plane** endpoint
> (`https://<name>.cognitiveservices.azure.com/openai/...`) instead — it hits the
> **same model**, but it's the clean `/openai/...` route that APIM fronts in this
> lab. Same model, different API surface.

## 2. Create the API in APIM

1. In the [Azure portal](https://portal.azure.com), navigate to your **resource
   group** (`rg-apim-ai-levelup`) and click the **APIM instance**
   (`<your-apim-name>`) to open it.
2. Go to **APIs** → **+ Add API** → **HTTP** (blank API), and select **Full**
   to show all settings.
3. Give it a display name — we used **`FoundryPortal`** — and set the **API URL
   suffix** to `foundry`.
4. **Uncheck "Subscription required"** (the app calls the gateway without a
   subscription key). Create it.

## 3. Create a backend for Foundry

1. In your APIM instance, go to **Backends** → **+ Add**.
2. Configure:
   - **Name:** `foundry-backend`
   - **Type:** Custom URL
   - **Runtime URL:** your **Foundry endpoint** — copy it from the same
     `foundryEndpoint` output you used to set the app's Endpoint field in step 1:

     ```bash
     az deployment group show -g $RG -n main --query properties.outputs.foundryEndpoint.value -o tsv
     ```
3. Create it.

> Using a named backend (instead of a hard-coded URL in the policy) keeps the
> endpoint in one place and is what you'll reference from the policy below.

## 4. Add the POST operation

1. Navigate back to **APIs** → **FoundryPortal**, then select **+ Add operation**.
2. Configure:
   - **Display name:** `PostFoundry`
   - **URL:** `POST` with path `/*` (wildcard — forwards the full Foundry path)
   - **Query parameter:** add `api-version`, default value `2024-10-21`
   - **Header:** add `Content-Type`, default value `application/json` (Foundry
     requires this to parse the request body — without it you'll get a 400)
3. Save.

> The wildcard path lets you pass the full Foundry route through the gateway,
> e.g. `/openai/deployments/gpt-4.1-mini/chat/completions`.

## 5. Add the gateway policy

Open the API, select **All operations**, then the **policy code editor**
(`</>` in the **Inbound processing** box) and paste the policy below.

This routes to the `foundry-backend` you created and has APIM attach a **managed
identity** token on every call, so clients never need a key or their own role on
Foundry — the gateway handles auth.

```xml
<policies>
    <inbound>
        <base />
        <!-- Route to the Foundry backend created in step 3 -->
        <set-backend-service backend-id="foundry-backend" />
        <!-- APIM's managed identity gets a token for the Cognitive Services data plane -->
        <authentication-managed-identity
            resource="https://cognitiveservices.azure.com"
            output-token-variable-name="msi-access-token"
            ignore-error="false" />
        <set-header name="Authorization" exists-action="override">
            <value>@("Bearer " + (string)context.Variables["msi-access-token"])</value>
        </set-header>
    </inbound>
    <backend>
        <base />
    </backend>
    <outbound>
        <base />
    </outbound>
    <on-error>
        <base />
    </on-error>
</policies>
```

> No keys or secrets in the policy — APIM's managed identity already has
> **Cognitive Services OpenAI User** on Foundry (granted by the Bicep deployment).

Save the policy.

## 6. Re-test through the APIM gateway

1. Replace the **Endpoint** field with your **APIM gateway URL + the API suffix**:
   `https://<your-apim-name>.azure-api.net/foundry`
2. Send the same prompt again.
3. You should get the same kind of response — but this time the call went through
   APIM, which authenticated to Foundry on your behalf. 🎉

> From here you can layer on AI-gateway policies (token rate limiting, semantic
> caching, load balancing) — all without touching the client.
