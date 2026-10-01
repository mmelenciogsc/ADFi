# Security and privacy

- Private source media, frames, transcripts and renders remain on Windows unless the user explicitly invokes a documented cloud or publication action.
- Source intake accepts local files only and requires versioned rights attestation. Attestation records authorization claimed by the user; it is not independent ownership proof.
- OAuth uses the system browser, Authorization Code with PKCE where supported, minimum scopes and explicit account/channel confirmation. Passwords and API keys are never requested through ADFi.
- Desktop secrets use Windows Credential Manager or DPAPI. Ubuntu secrets use root-readable service credentials or an equivalent secret manager and never Git, deployment archives, command arguments, logs or model prompts.
- Model output is untrusted: schema validation, evidence provenance, authorization and deterministic policy gates precede state mutation.
- API controls include verified email, adaptive password hashing, rotating sessions, per-client rate limits, short-lived one-use reset tokens, HSTS/security headers, bounded request bodies, audit logs and server-authoritative entitlements. Current state-changing endpoints use bearer tokens and do not use ambient browser cookies; if cookie-authenticated browser mutations are added, antiforgery validation is mandatory before enabling them.
- Financial and contribution history is append-only or corrected with compensating entries. Webhooks and payouts use idempotency keys.
- Sensitive values must be redacted from structured logs. Repository secret scanning is part of release validation.

## Viewer VQA evidence

Moment VQA is user-triggered and read-only. Local paths and ephemeral file paths are explicitly excluded from serialized request context. Evidence directories are per-session/per-request under current-user local application data and are deleted after failure, resume, seek, close, source change and process exit; startup scavenging handles abandoned crash residue. Cloud transport receives no source filename or unrelated personal metadata.

Arbitrary YouTube visual capture is enforced by server flag plus code limits: explicit Ask, paused player, one player-region capture per transaction, at most three configured images overall, capped temporal span/dimensions, no iteration/export/background capture and immediate cleanup. Production defaults the flag off pending YouTube compliance/audit and legal review. A separate ChatGPT-plan-usage flag defaults off pending approval for ADFi as a paid app. Disabling either flag must not disable official playback, ADFi persisted evidence, local viewing or the Ask/Dictate/Resume shell.
