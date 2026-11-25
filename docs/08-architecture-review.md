# 08 — Architecture Review (Step 4 — Validate)

A critical review of the architecture **and of the MVP code as built**: what is good enough, what is a deliberate MVP shortcut, and what must change before production.

## 1. Verdict

The core design decisions hold up under review: deterministic engine over a curated catalogue, AI at bounded edges, REST + MCP adapters, modular monolith, evidence grading and human approval. They address the main risks of the brief (hallucinated licensing, untraceable advice, over-engineering). The weaknesses below are mostly **operational hardening** and **knowledge-curation throughput**, not structural flaws.

## 2. Bottlenecks

| # | Bottleneck | Impact | Mitigation |
|---|---|---|---|
| B1 | **Knowledge curation** (human verification of every catalogue entry) | Platform value is capped by curator capacity; packs go stale | AI-assisted candidate extraction with verbatim-quote check (doc 03 §3.1); curator dashboard with freshness SLA; budget 0.2–0.5 FTE per vendor |
| B2 | Evidence retrieval is **one search per covered requirement per option** (~8 × 3 = 24 queries) | Fine in-process (ms); with Azure AI Search ~1–2 s added latency | Parallelise (`Task.WhenAll`) with a concurrency cap; cache (product, capability) → evidence per pack version — evidence depends only on catalogue + index, not on the customer |
| B3 | Two sequential LLM calls per run | 2–8 s latency; cost per run | Extraction only on new turns; summary generated lazily (on report request) for Nexa; small/fast model for extraction |
| B4 | Bundle enumeration is O(C(n,3)) over the candidate pool | Pool capped at 8 → 92 bundles: trivial. Larger catalogues (Salesforce editions) could grow | Keep the pool cap; prune products with zero Must coverage; per-vendor `RecommendationOptions` |
| B5 | `KnowledgeBootstrapper` seeds on every API start | Multiple replicas race to seed the same documents (unique index on vendor/externalId/version → one fails) | **Production:** disable `SeedDocumentsOnStartup`; run ingestion as a Container Apps Job. The in-memory index is dev/test only |

## 3. Unnecessary complexity check

| Item | Keep? | Reasoning |
|---|---|---|
| 8 projects | Yes | Each has a distinct dependency profile (EF, OpenAI, PdfPig, MCP, Azure Search). Merging Agents into Infrastructure is possible but would couple model changes to persistence |
| Two index implementations | Yes | In-memory makes tests and demos run offline; production uses Azure AI Search behind the same port |
| MAF dependency for one agent | Yes, but watch | Small surface; strategic for future proposal/RFP agents. If it stays single-use for 6+ months, reconsider |
| Catalogue-as-code JSON | Yes for MVP | Avoids building an admin UI before the model is validated; plan the SQL move (doc 03 §9.2) |
| Microservices | **Not introduced** | No evidence of need. Ingestion is the first split candidate |

## 4. Security gaps found in the MVP code

| # | Gap | Severity | Fix |
|---|---|---|---|
| S1 | Blazor Web uses a **service API key with reviewer rights** (dev convenience) | High if deployed | Entra OIDC sign-in + OBO token; UI shows Approve only for reviewers |
| S2 | `SystemUserContext` (no HTTP context) originally granted every role | Medium | **Fixed during review**: restricted to `Knowledge.Admin` only. Next: register it only in job scopes, never as a request-scope fallback |
| S3 | API-key auth in Development config with fixed demo keys | Low (dev only) | `appsettings.json` defaults to `EntraId`; keys only in `appsettings.Development.json`; production keys only in Key Vault, if used at all |
| S4 | Regex-based injection detection | Medium | Expected to miss obfuscated attacks. Add Azure AI Content Safety Prompt Shields; the real control remains *no side-effecting tools + review-before-publish* |
| S5 | Price detection is pattern-based (e.g. misses "forty dollars per seat") | Medium | Add word-number patterns and an LLM-judge check in evaluation; licensing domain model has **no price fields at all**, so the engine cannot produce prices — only the narrative could, and it is guarded |
| S6 | No in-app rate limiting | Medium | APIM policies; or ASP.NET Core rate limiter (`AddRateLimiter`) per client id on `/mcp` and `/advisory/recommend` |
| S7 | Report HTML generated server-side and shown in UI | Low | Markdig with `DisableHtml()`; UI renders it in a `sandbox=""` iframe |
| S8 | Customer requirement text stored in `AdvisorySessions` | Medium (data protection) | TDE (default on Azure SQL); retention policy; consider Always Encrypted for `Turns` if customer PII is expected |

## 5. Design weaknesses and mitigations

| # | Weakness | Mitigation |
|---|---|---|
| W1 | **Cumulative re-analysis** of all turns cannot express corrections ("actually we're not agile"). Both terms present → "hybrid" | Per-turn extraction with *latest value wins* for scalar fields and explicit remove semantics; or the LLM produces a profile diff validated against the previous profile |
| W2 | Heuristic extraction is **English-only** and keyword-based: false positives (e.g. "integration" mentioned in passing) and misses (unusual phrasing) | The LLM path covers paraphrase; the heuristic is the safety net. Add per-vendor negative patterns; show "Inferred/Stated" so users can correct; multilingual via the LLM path |
| W3 | Classification of chunks by name/synonym matching is noisy | Chunks with document-level product codes get priority; add embedding-similarity classification; evidence threshold per vendor |
| W4 | Persona sizing drivers are hard-coded (`LicensingEvaluator.Personas`) | Move to pack data before Salesforce/OutSystems (doc 07 §2.2) |
| W5 | Fit-rule adjustments can reorder options | **By design they cannot override must-have coverage** (lexicographic ranking). Rules are labelled, need business confirmation, and should get an `ApprovedBy` field (SQL schema) |
| W6 | Coverage levels (Core/Supported/ViaIntegration) are coarse | Adequate for advisory; finer granularity (feature-level) only where it changes decisions, e.g. editions |
| W7 | `EnsureCreated` instead of migrations | Generate the initial migration before the first Azure deployment; use migration bundles in CD |
| W8 | Review status lives on the session; a later re-run resets to Draft (correct), but there is no immutable snapshot of what was approved | Store an approved snapshot (result JSON + report hash) as a separate record; the report references the snapshot id |
| W9 | MCP tool `recommend_vendor_products` is not idempotent (creates sessions) | Acceptable; annotated `Idempotent=false`. Add an optional client-supplied idempotency key if Nexa retries |
| W10 | Evidence for a claim is the catalogue's own summary document in the demo pack (circular) | That is a demo-data artefact. Real packs cite vendor documents; the grade only becomes `Verified` with a `VendorOfficial` source and curator verification |

## 6. Scalability assessment

| Dimension | Expected load (Y1) | Headroom |
|---|---|---|
| Advisory runs | 100s/day | One API replica handles 100s/minute deterministically; LLM throughput (TPM quota) is the real limit |
| Knowledge | 3 vendors × ~1–5k documents → ~50–250k chunks | Azure AI Search S1 is comfortable; storage-optimised tiers if needed |
| Sessions | 10–50k/year | Azure SQL GP serverless is fine |
| Vendors | 3 → 10+ | Pack-based: linear curation effort, constant platform effort |

## 7. What I would do differently with more information

- If **Nexa is not LLM-based**, drop MCP from the Nexa path (keep it for other AI clients) and add a REST response shape optimised for scripted rendering.
- If **partner price books** can be used, add a restricted `PartnerLicensing` module with audience control and a second-reviewer rule. This is the most valuable commercial addition, and the riskiest.
- If **implementation actuals** exist, add an estimator module early (opportunity #4). It is the strongest differentiation.

## 8. Items requiring business confirmation

1. Avantra fit rules (all four are flagged `requiresBusinessConfirmation`).
2. Planview catalogue mappings and coverage levels (all `PendingCuratorVerification`).
3. Target users for v1 (internal vs customer), approval workflow owners.
4. Nexa capabilities (doc 05 §4.4).
5. Data residency, retention and the model data-handling policy.
6. Partner data usage rights per vendor.
