# ChatGPT integration

ADFi's optional ChatGPT provider follows OpenAI's current open-source **Sign in with ChatGPT** token-sharing flow; it does not ask for an API key or password and does not imply access to existing conversations. The official documentation says eligible ChatGPT Plus and Pro users may authorize plan usage, while identity-only integration has separate availability. ADFi must disclose what project text/images will leave the device before each cloud-assisted operation and must continue to default Creator work to local Ollama/Qwen3-VL.

The desktop creates and persists one random `urn:uuid:` host ID, listens only on a `127.0.0.1` loopback callback, generates fresh state, nonce and PKCE values for every authorization attempt, begins first registration with `dynamic_agent_client`, retains the issued client ID returned by the callback, and requests `openid profile email offline_access resource.invoke chatgpt.tokens.use.direct` for resource `https://api.openai.com/v1`. Callback state, issued-client continuity and the ID token signature/issuer/audience/expiry/nonce must validate before credentials replace a saved profile. Granted scopes—not a successful identity sign-in—determine plan-usage permission. Tokens and identity profiles belong in DPAPI/Windows Credential Manager, never project files or logs.

Inference lists models available to the selected account, calls the public Responses API (never private ChatGPT backend endpoints), sends `store: false` and `stream: true`, suppresses token deltas from screenreader live regions, and treats only `response.completed` as success. Usage-limit, incomplete, failed and interrupted streams remain distinct recoverable states. The UI shows **Using ChatGPT plan** and a **Manage usage** action only when the active profile grants direct plan use.

Official sources consulted on 2026-10-02:

- https://developers.openai.com/cookbook/articles/sign-in-with-chatgpt
- https://developers.openai.com/siwc/token-sharing-open-source/sign-in
- https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference

