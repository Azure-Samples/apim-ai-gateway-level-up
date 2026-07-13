
 # Session Two Lab: Add token limits, telemetry, semantic caching, and content safety to your Foundry API in APIM

 This lab walks you through layering AI Gateway capabilities onto the Microsoft Foundry API you imported into Azure API Management (APIM):

 1. Import a Microsoft Foundry API into APIM
 2. Apply a token-limit policy (rate limiting)
 3. Apply an emit-token-metric policy (telemetry)
 4. Add semantic caching backed by Azure Managed Redis
 5. Add an Azure AI Content Safety prompt shield

 > Prerequisites: you have already deployed `infra/main.bicep` from this repo, so an APIM instance, a Foundry account with a `gpt-4.1-mini` and a `text-embedding-ada-002`  deployment, an Azure Managed Redis cluster, and an Azure AI Content Safety resource all exist in your resource group.

 Throughout the lab, replace placeholders in angle brackets (e.g. `<your-foundry-name>`, `<your-redis-host>`) with the actual values from your deployment outputs:


 ---

 ## 1. Import the Microsoft Foundry API into APIM

 Reference:
 Import a Microsoft Foundry API - Azure API Management | Microsoft Learn

 Steps (Azure Portal):

 1. Open your APIM instance → **APIs** → **+ Add API** → **Azure AI Foundry**.
 2. Select your Foundry account and the project deployed by the Bicep template.
 3. Pick the models to expose (at minimum `gpt-4.1-mini`).
 4. Fill in the import wizard:
    - **API URL suffix (base URL):** `foundrygateway`
    - **Description:** `AI Gateway integration to Foundry`
    - **Client compatibility:** `AzureAI` — choose this if your clients need to call any model in Microsoft Foundry (not just OpenAI-shaped ones).
 5. On the next screen, leave **Enable token limit** and **Emit token metric** policies checked — you'll fine-tune them in the next steps.
 6. Click **Create**.

 > Importing through the portal automatically grants the **Cognitive Services OpenAI User** role to the APIM managed identity on the Foundry account, so APIM can call Foundry on  your behalf with its system-assigned MI.

 ---

 ## 2. Configure the token-limit policy (rate limiting)

 Reference:
 Azure API Management policy reference - llm-token-limit | Microsoft Learn

 Goal: cap tokens per minute per subscription so you can demonstrate `429 Too Many Requests` throttling.

 1. Open the imported API → **Design** → **All operations** → **Inbound processing** → pencil icon (Policy code editor).
 2. Inside `<inbound>`, add the policy below. `tokens-per-minute="100"` is intentionally tiny so you can hit the limit in a few clicks of **Send** from the Test tab.

 ```xml
 <llm-token-limit
     remaining-tokens-header-name="remaining-tokens"
     tokens-per-minute="100"
     counter-key="@(context.Subscription.Id)"
     estimate-prompt-tokens="true"
     tokens-consumed-header-name="consumed-tokens" />
 ```

 3. **Save**.
 4. Open the **Test** tab, pick the chat completions operation for `gpt-4.1-mini`, and click **Send** 2–3 times with a short prompt. The `remaining-tokens` header drops with each  call; once you exceed 100 tokens in the minute, subsequent calls return **HTTP 429**.

 ---

 ## 3. Configure the emit-token-metric policy (telemetry)

 Reference:
 Azure API Management policy reference - llm-emit-token-metric | Microsoft Learn

 Goal: emit per-call token counts as a custom metric in Application Insights so you can chart usage and estimated cost per subscription.

 1. In the same Inbound policy editor, add this policy *after* `<llm-token-limit>`:

 ```xml
 <llm-emit-token-metric>
     <dimension name="Subscription ID" />
 </llm-emit-token-metric>
 ```

 2. **Save**.
 3. In your Application Insights resource, open **Usage and estimated costs** → **Custom metrics (Preview)** → toggle **With dimensions** to **On** (required so the `Subscription  ID` dimension is retained).
 4. Send a few test requests, then go to **Metrics**, pick the `azure.applicationinsights` namespace, and chart the new token metric split by `Subscription ID`.

 ---

 ## 4. Enable semantic caching with Azure Managed Redis

 References:
 Enable Semantic Caching for LLM APIs in Azure API Management | Microsoft Learn
 Use an external cache in Azure API Management | Microsoft Learn

 Semantic caching needs three pieces wired up in APIM:

 - An **embedding backend** pointing at your `text-embedding-ada-002` deployment (used to convert prompts into vectors for similarity matching).
 - An **external cache** pointing at your Azure Managed Redis cluster.
 - **Cache lookup + store** policies on the API.

 ### 4a. Create the embedding backend

 1. In APIM, go to **Backends** → **+ Add**.
 2. **Name:** `embedding-backend`
 3. **Type:** Custom URL
 4. **Runtime URL:** `https://<your-foundry-name>.services.ai.azure.com/openai/deployments/text-embedding-ada-002/embeddings`
 6. **Create**.

 > APIM's managed identity already has Cognitive Services OpenAI User on the Foundry account from the API import in step 1, so no extra role assignment is needed.

 ### 4b. Create the external cache

 1. APIM → **External cache** → **+ Add**.
 2. **Cache instance:** Custom
 3. **Use from:** Default
 4. **Description:** `AI Gateway external cache`
 5. **Connection string:** open your Azure Managed Redis resource → **Access keys** → copy the **Primary key**, then build the string as:

    ```
    <your-redis-host>:10000,password=<REDIS_PRIMARY_ACCESS_KEY>,ssl=True,abortConnect=False
    ```

    Example host (yours will differ): `<your-redis-name>.<region>.redis.azure.net`. **Never commit this connection string or the access key anywhere.**
 6. **Save**.

 ### 4c. Add cache lookup + store policies

 In the API → **Design** → **All operations** → Inbound policy editor:

 Inside `<inbound>` (after the token-limit / emit-metric policies):

 ```xml
 <!--Semantic cache lookup-->
 <llm-semantic-cache-lookup
     embeddings-backend-auth="system-assigned"
     embeddings-backend-id="embedding-backend"
     score-threshold="0.05">
     <vary-by>@(context.Subscription.Id)</vary-by>
 </llm-semantic-cache-lookup>
 ```

 Inside `<outbound>`:

 ```xml
 <!--Semantic cache store-->
 <llm-semantic-cache-store duration="60" />
 ```

 **Save**, then in **Test** send the same prompt twice. The second call should return much faster; confirm the cache hit by checking Redis keys or adding a trace.

 ---

 ## 5. Enable Azure AI Content Safety (prompt shield)

 Goal: block prompts that contain hate, violence, self-harm, or sexual content above the configured severity threshold before they reach the model.

 ### 5a. Grant APIM's MI access to Content Safety

 1. Open your **Azure AI Content Safety** resource.
 2. **Access control (IAM)** → **+ Add role assignment**.
 3. Role: **Cognitive Services User**.
 4. Assign to **Managed identity** → pick your APIM instance.
 5. **Review + assign**.

 ### 5b. Create the content-safety backend

 1. APIM → **Backends** → **+ Add**.
 2. **Name:** `content-safety-backend`
 3. **Type:** Custom URL
 4. **Runtime URL:** `https://<your-content-safety-name>.cognitiveservices.azure.com`
 5. Under **Authorization credentials**, choose **Managed Identity**:
    - **Resource:** `https://cognitiveservices.azure.com`
 6. **Create**.

 ### 5c. Add the llm-content-safety policy

 In the API → Inbound policy editor, add this policy **before** the cache lookup so unsafe prompts are blocked before the cache or backend is touched:

 ```xml
 <llm-content-safety backend-id="content-safety-backend" shield-prompt="true">
     <categories output-type="EightSeverityLevels">
         <category name="Hate"     threshold="4" />
         <category name="Violence" threshold="4" />
         <category name="SelfHarm" threshold="4" />
         <category name="Sexual"   threshold="4" />
     </categories>
 </llm-content-safety>
 ```

 **Save** and test with a benign prompt (passes) and a clearly unsafe prompt (blocked with a 4xx from APIM).

 ---

 ## Final inbound/outbound policy shape

 For reference, after all five steps your policy on the API should look roughly like this — order matters (content safety first, then rate limit, then metric, then cache lookup):

 ```xml
 <inbound>
     <base />
     <llm-content-safety backend-id="content-safety-backend" shield-prompt="true">
         ...
     </llm-content-safety>
     <llm-token-limit tokens-per-minute="100" ... />
     <llm-emit-token-metric>
         <dimension name="Subscription ID" />
     </llm-emit-token-metric>
     <llm-semantic-cache-lookup ... />
 </inbound>
 <outbound>
     <base />
     <llm-semantic-cache-store duration="60" />
 </outbound>
 ```
