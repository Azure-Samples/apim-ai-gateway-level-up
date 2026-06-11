# Session One — Hands-on Lab: Front Azure AI Foundry with APIM

In this lab you'll import your Foundry endpoint as an API in APIM, add a policy
so APIM authenticates to Foundry with its **managed identity**, and prove it end
to end with the chat app — first calling Foundry directly, then through the
gateway.

> **Before you start:** the infra is deployed (see the root [README](../README.md))
> and the chat app is running locally (`cd src/chatapp && dotnet run`). Have your
> **Foundry endpoint** and **deployment name** (`gpt-4.1-mini`) handy.

---

## 1. Create the API in APIM

1. In the [Azure portal](https://portal.azure.com), open your **APIM** instance
   (`<your-apim-name>`).
2. Go to **APIs** → **+ Add API** → **HTTP** (blank API).
3. Give it a display name — we used **`FoundryPortal`** — and set the **API URL
   suffix** to `foundry`. Create it.

## 2. Add the POST operation

1. On the new API, select **+ Add operation**.
2. Configure:
   - **Display name:** `PostFoundry`
   - **URL:** `POST` with path `/*` (wildcard — forwards the full Foundry path)
   - **Query parameter:** add `api-version`, default value `2024-10-21`
3. Save.

> The wildcard path lets you pass the full Foundry route through the gateway,
> e.g. `/openai/deployments/gpt-4.1-mini/chat/completions`.

## 3. Add the gateway policy

Open the API, select **All operations**, then the **policy code editor**
(`</>` in the **Inbound processing** box) and paste the policy below.

This points the backend at your Foundry account and has APIM attach a **managed
identity** token on every call, so clients never need a key or their own role on
Foundry — the gateway handles auth.

```xml
<policies>
    <inbound>
        <base />
        <!-- Route to your Foundry account (no trailing slash) -->
        <set-backend-service base-url="https://<your-foundry-name>.cognitiveservices.azure.com" />
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

> Replace `<your-foundry-name>` with your Foundry account name. APIM's managed
> identity already has **Cognitive Services OpenAI User** on Foundry (granted by
> the Bicep deployment).

Save the policy.

## 4. Test against Foundry directly

1. The chat app should already be running. Open it in your browser
   (`http://localhost:5152`).
2. In the **Endpoint** field at the top, paste your **Foundry endpoint**:
   `https://<your-foundry-name>.cognitiveservices.azure.com/`
3. Click **Check access (debug)** — you should get a **200**.
   - If you get a **401/403**, the role assignment just hasn't propagated yet
     (data-plane RBAC can take ~15–20 min). Wait a few minutes and try again.
4. Paste this prompt into the message box and hit **Send**:

   ```
   <add your sample prompt here>
   ```

   You should get a response. A **401** here (with a 200 on the debug check) also
   means propagation — wait a bit and resend.

## 5. Test through the APIM gateway

1. Replace the **Endpoint** field with your **APIM gateway URL + the API suffix**:
   `https://<your-apim-name>.azure-api.net/foundry`
2. Send the same prompt again.
3. You should get the same kind of response — but this time the call went through
   APIM, which authenticated to Foundry on your behalf. 🎉

> From here you can layer on AI-gateway policies (token rate limiting, semantic
> caching, load balancing) — all without touching the client.
