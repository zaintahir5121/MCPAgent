# 06 — Security & AI Governance

> Deliverable covered: (11) security and governance design.

## 1. Threat model (summary)

| Threat | Vector | Control (implemented ✅ / planned 🔜) |
|---|---|---|
| Unauthorised access | Anonymous calls to API/MCP | ✅ Default-deny authorization on `/api/v1` and `/mcp`; Entra JWT or API key; anonymous only `/`, `/health`, `/openapi` |
| Privilege escalation | Sales user approves own report or uploads knowledge | ✅ Policies `Advisory.Review`, `Knowledge.Admin` (403 tested) |
| Cross-tenant data access | Guessing session ids | ✅ Every session query includes `TenantId` from the token; continuing a foreign session id starts a new session (tested) |
| Cross-vendor leakage | Search returns other vendor or partner content | ✅ Mandatory vendor filter before ranking; 🔜 audience filter (Internal vs Customer) for partner-confidential content |
| Prompt injection (direct) | User text "ignore previous instructions…" | ✅ Detected → warning; text only ever used as data; LLM output schema-validated; no tools with side effects reachable by the LLM |
| Prompt injection (indirect) / document poisoning | Malicious uploaded datasheet | ✅ Quarantine on ingest; review-before-publish; trust levels; `<untrusted_source>` delimiting; 🔜 Azure AI Content Safety Prompt Shields |
| Hallucinated commercial facts | LLM invents a price, tier or product | ✅ Catalogue-only decisions; `NarrativeGuard` (prices, unknown products, invalid citations); agent price guard; validator forbids `publicTerms` on non-public licensing |
| Sensitive data sent to model | Card numbers, IDs in requirements | ✅ Redaction of card/ID-shaped numbers before model calls; telemetry with `EnableSensitiveData=false`; 🔜 Azure AI Language PII detection; Azure OpenAI in-tenant with abuse monitoring opt-out where approved |
| Secret leakage | Keys in config or repo | ✅ No secrets in appsettings; env vars / user-secrets in dev; Key Vault + managed identity in prod; constant-time API key comparison |
| Supply chain | Vulnerable packages | ✅ Central package management, pinned versions; 🔜 Dependabot, `dotnet list package --vulnerable` in CI, container scanning |
| Abuse / cost exhaustion | High call volume through MCP | 🔜 APIM rate limits and quotas per client; input length cap (8k) ✅ |
| Repudiation | "Who sent this recommendation?" | ✅ Audit log: actor, tenant, channel, action, model, analyzer, pack version, options, summary source |

## 2. Identity and access

### 2.1 Microsoft Entra ID setup

1. **App registration `Avantra AI Advisory API`**, Application ID URI `api://avantra-ai-advisory`.
2. **App roles** (assignable to users/groups **and** applications):

| Role | Assigned to | Grants |
|---|---|---|
| `Advisory.User` | Sales, pre-sales groups; Nexa service principal | Run advisory, search, compare, read own sessions, reports |
| `Advisory.Reviewer` | Solution architects | + approve/reject results, view all tenant sessions |
| `Knowledge.Admin` | Knowledge curators | + ingest, publish, quarantine override |

3. **Web app (Blazor)**: OIDC sign-in, then calls the API with a delegated token (or On-Behalf-Of). *The MVP uses a dev API key for brevity; production switches `Auth:Mode` to `EntraId`.*
4. **Nexa**: confidential client or managed identity, granted application role `Advisory.User`.
5. **Configuration**: `Auth:Mode = "EntraId"` (or `"EntraId,ApiKey"` during migration), `Auth:EntraId:TenantId`, `Audience`. Roles are read from the `roles` claim; tenant from `tid`; user from `oid`.

### 2.2 Tenant and customer data isolation

- **Tenant** = the Entra tenant (`tid`) of the caller. For a multi-customer portal, map an external identity (Entra External ID) to a customer tenant id claim.
- Sessions store `TenantId` + `OwnerId`. Non-reviewers see only their own sessions; reviewers see all sessions **in their tenant**.
- Knowledge is **not** tenant data (it is Avantra's). Customer-specific documents (e.g. an uploaded RFP, Phase 3+) are stored with `TenantId` and must be filtered by tenant in the index, the same way as vendor filtering.
- Data retention: advisory sessions 24 months (configurable), audit 7 years. Both are confirmed with Legal/Compliance.

## 3. API security

- HTTPS only (HSTS at Front Door/APIM); ProblemDetails without stack traces; `traceId` for support.
- CORS allow-list (`Cors:AllowedOrigins`), empty by default.
- Upload limits: 25 MB, parser allow-list (md, txt, html, pdf, docx); unknown types → 415.
- Antiforgery disabled only on the token-authenticated multipart endpoint (no cookie auth on the API).
- APIM in front: subscription keys for partners, rate limit (e.g. 60 calls/min/client for `recommend`), payload size limits, IP filtering for Nexa.

## 4. AI governance

| Control | Implementation |
|---|---|
| **Human approval for high-impact output** | Every advisory result starts as `Draft`; customer-shareable only after `Advisory.Reviewer` approval; report watermark; review recorded in session + audit |
| **Separation of facts, rules and AI** | Evidence grades; Avantra rules labelled "not vendor facts" and flagged `requiresBusinessConfirmation`; summary source shown |
| **Knowledge source trust levels** | `VendorOfficial` › `PartnerVerified` › `AvantraInternal` › `ThirdParty` › `Unverified`; `minimumTrust` filter; trust shown on every citation |
| **Licensing disclosure discipline** | `NotPublic` / `PartnerConfidential` / `PubliclyDocumented`; validator rule; no price fields in the domain model |
| **Prompt and model change control** | Versioned prompts in code; model via config; evaluation suite must pass before change (Phase 3) |
| **Transparency** | Users see analyzer used, inferred vs stated requirements, assumptions, warnings, data disclaimer |
| **Responsible AI review** | Run the Microsoft Responsible AI Impact Assessment for customer-facing launch (Phase 4) |
| **Model data handling** | Prefer Azure OpenAI in Avantra's tenant (data not used for training; regional processing). Confirm your current OpenAI-compatible endpoint's data terms before sending customer data |

## 5. Secret management

| Environment | Mechanism |
|---|---|
| Local dev | `dotnet user-secrets` (`UserSecretsId` set on the API project) or environment variables `AI__ApiKey`, `AI__Endpoint`, `AI__ChatModel` |
| CI | GitHub Actions secrets → only for integration environments; tests use fakes, no real keys |
| Azure | **Key Vault** referenced by `KeyVault:Uri`, loaded with `DefaultAzureCredential` (managed identity). Secret names use `--` for sections, e.g. `AI--ApiKey`. Prefer **keyless** auth: managed identity to Azure OpenAI, Azure AI Search and SQL (Entra auth), so most secrets disappear |

## 6. Audit logging

`AuditLog` rows (also emitted as structured logs to Log Analytics):

| Action | Detail recorded |
|---|---|
| `advisory.run` | pack version, analyzer (`llm+heuristic`/`heuristic`), options (product codes), summary source, model |
| `advisory.approve` / `advisory.reject` | reviewer, comment |
| `knowledge.ingest` | vendor/externalId, version, status, chunks, trust |
| `knowledge.publish` | curator |

Customer requirement text is **not** written to the audit log (it lives in the session, under tenant isolation). This keeps the audit log shareable with security teams.

## 7. Security checklist before production

- [ ] `Auth:Mode = EntraId`, API keys removed or limited to APIM-to-API
- [ ] Managed identity for SQL, Search, Storage, Key Vault, Azure OpenAI; no connection-string secrets
- [ ] Private endpoints; Container Apps environment internal; Front Door WAF policy (OWASP)
- [ ] APIM rate limits and quotas on `/mcp` and `/api/v1/advisory/*`
- [ ] Content Safety Prompt Shields on ingestion and user input
- [ ] Catalogue verified (no `isDemonstrationData` packs exposed to customers)
- [ ] Pen test including prompt-injection red-team corpus
- [ ] Data retention and DPIA approved; Responsible AI impact assessment completed
- [ ] Partner data usage rights confirmed per vendor (Legal/Alliances)
