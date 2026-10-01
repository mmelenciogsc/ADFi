# Security and privacy

- Private source media, frames, transcripts and renders remain on Windows unless the user explicitly invokes a documented cloud or publication action.
- Source intake accepts local files only and requires versioned rights attestation. Attestation records authorization claimed by the user; it is not independent ownership proof.
- OAuth uses the system browser, Authorization Code with PKCE where supported, minimum scopes and explicit account/channel confirmation. Passwords and API keys are never requested through ADFi.
- Desktop secrets use Windows Credential Manager or DPAPI. Ubuntu secrets use root-readable service credentials or an equivalent secret manager and never Git, deployment archives, command arguments, logs or model prompts.
- Model output is untrusted: schema validation, evidence provenance, authorization and deterministic policy gates precede state mutation.
- API controls include verified email, adaptive password hashing, rotating sessions, CSRF protection for browser flows, rate limits, short-lived reset tokens, audit logs and server-authoritative entitlements.
- Financial and contribution history is append-only or corrected with compensating entries. Webhooks and payouts use idempotency keys.
- Sensitive values must be redacted from structured logs. Repository secret scanning is part of release validation.

