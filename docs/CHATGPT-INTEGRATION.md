# ChatGPT integration

ADFi's optional ChatGPT provider follows OpenAI's current open-source **Sign in with ChatGPT** token-sharing flow; it does not ask for an API key or password and does not imply access to existing conversations. The official documentation says eligible ChatGPT Plus and Pro users may authorize plan usage, while identity-only integration has separate availability. ADFi must disclose what project text/images will leave the device before each cloud-assisted operation and must continue to default Creator work to local Ollama/Qwen3-VL.

The desktop creates and persists one random `urn:uuid:` host ID, listens only on a `127.0.0.1` loopback callback, generates fresh state, nonce and PKCE values for every authorization attempt, begins first registration with `dynamic_agent_client`, retains the issued client ID returned by the callback, and requests `openid profile email offline_access resource.invoke chatgpt.tokens.use.direct` for resource `https://api.openai.com/v1`. The implemented profile service validates callback state and issued-client continuity, then validates the ID-token signature against OpenAI's JWKS plus issuer, audience, expiry and nonce before replacing credentials. Granted scopes—not a successful identity sign-in—determine plan-usage permission. The selected profile, stable host ID, registered client ID and rotating refresh/access tokens are stored as one current-user DPAPI-protected vault, never in projects or logs. Refresh is serialized; disconnect attempts provider revocation and always removes local access.

Inference lists models available to the selected account, calls the public Responses API (never private ChatGPT backend endpoints), sends `store: false` and `stream: true`, suppresses token deltas from screenreader live regions, and treats only `response.completed` as success. Usage-limit, incomplete, failed and interrupted streams remain distinct recoverable states. The UI shows **Using ChatGPT plan** and a **Manage usage** action only when the active profile grants direct plan use.

`ChatGptPlanResponsesClient` implements those public model-list/Responses transport rules. `ChatGptMomentVqaProvider` refuses visual answers when approved visual evidence is absent, strips paths from prompt context, reports evidence provenance and maps documented plan-usage errors to accessible states. OAuth access tokens are bearer credentials owned by the selected protected local profile; they are never placed in a prompt, URL, project JSON or log.

The viewer includes native, screenreader-named **Continue with ChatGPT**, profile selection, **Manage usage**, and **Disconnect ChatGPT** controls. Authorization opens only in the system browser, waits at a random loopback port for at most five minutes, and never renders a provider password surface inside ADFi. These controls remain disabled while the server approval flag is off.

OpenAI currently instructs paid or remotely hosted apps to request access before offering this integration. ADFi is a paid application, so the server-controlled `ChatGptPlanUsageIntegrationEnabled` production flag defaults off until approval is documented. Code readiness is not represented as provider approval.

Official sources consulted on 2026-10-02:

- https://developers.openai.com/cookbook/articles/sign-in-with-chatgpt
- https://developers.openai.com/siwc/token-sharing-open-source/sign-in
- https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference
- https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery
- https://developers.openai.com/siwc/ui-ux-guidelines
