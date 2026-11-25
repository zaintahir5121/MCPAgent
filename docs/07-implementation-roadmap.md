# 07 — Implementation Roadmap, Testing, Deployment & Multi-Vendor Expansion

> Deliverables covered: (18) testing strategy, (19) deployment strategy, (20) multi-vendor expansion roadmap, plus the phased implementation plan. Also **Step 7 — Scale**.

## 1. Phased roadmap

Status legend: ✅ delivered in this repository · 🔜 next.

### Phase 1 — Architecture & foundation (weeks 1–2) ✅

| | |
|---|---|
| **Deliverables** | Solution structure (Clean Architecture modular monolith), domain models, configuration model (`AI`, `Database`, `Knowledge`, `Auth`, `KeyVault`), OpenAI-compatible AI provider via Microsoft.Extensions.AI, API foundation (minimal APIs, ProblemDetails, OpenAPI, health, OpenTelemetry), auth (Entra/API key), EF Core persistence |
| **Dependencies** | AI endpoint and key; Azure subscription for later phases |
| **Technical tasks** | Projects + central package management; `IChatClient` registration with logging/telemetry; DbContext; security policies; CI build |
| **Acceptance criteria** | ✅ `dotnet build` clean; ✅ API starts with no AI key (deterministic mode) and with a key (AI mode); ✅ anonymous API calls → 401; ✅ secrets only from env/user-secrets/Key Vault |

### Phase 2 — Knowledge platform (weeks 2–5) ✅ MVP / 🔜 production hardening

| | |
|---|---|
| **Deliverables** | ✅ Ingestion (MD/HTML/PDF/DOCX/TXT), hashing and versioning, quarantine, chunking, classification, optional embeddings, in-memory hybrid index, Azure AI Search adapter, vendor packs + validator, search API, citations. 🔜 Blob storage for originals, Azure Document Intelligence, Content Safety, SQL catalogue + curator admin UI, AI-assisted fact extraction with verbatim-quote check, **verified Planview pack** |
| **Dependencies** | Curator assigned (Planview); Legal confirmation of source usage; Azure AI Search + Storage provisioned |
| **Technical tasks** | 🔜 `BlobDocumentStore`; `DocumentIntelligenceParser`; ingestion job (Container Apps Job) triggered by Blob events; `SqlCatalogueRepository`; admin pages (documents, publish, catalogue edit, candidate queue); golden retrieval set |
| **Acceptance criteria** | ✅ Poisoned document quarantined (tested); ✅ vendor isolation (tested); ✅ unchanged re-ingest is a no-op; 🔜 recall@5 ≥ 0.9 on the golden set; 🔜 100% of Planview products `Verified` with `VendorOfficial` sources |

### Phase 3 — Advisory agent (weeks 4–8) ✅ MVP / 🔜 pilot

| | |
|---|---|
| **Deliverables** | ✅ Requirements analysis (LLM + deterministic), clarification workflow (sessions), recommendation engine, product comparison, gap analysis, licensing evaluation, evidence grades, grounded summary + guard, report (MD/HTML), review gate, Blazor UI, MAF Q&A agent. 🔜 DOCX/PDF report template with Avantra branding, evaluation harness, sales pilot |
| **Dependencies** | Sales pilot group (5–10 users); 30–50 golden scenarios from senior consultants; business confirmation of Avantra fit rules |
| **Technical tasks** | 🔜 `Microsoft.Extensions.AI.Evaluation` test project; branded report renderer (Open XML); feedback capture ("was this right?") on results; tune `RecommendationOptions` |
| **Acceptance criteria** | ✅ Example scenario → Planview Portfolios with 100% must-have coverage (tested); ✅ every covered requirement has ≥ 1 citation (tested); ✅ no price ever appears (tested); 🔜 ≥ 80% of pilot recommendations approved without major change; 🔜 median time to first recommendation < 15 min |

### Phase 4 — MCP & Nexa (weeks 6–10) ✅ MCP server / 🔜 Nexa go-live

| | |
|---|---|
| **Deliverables** | ✅ MCP server (10 tools, stateless Streamable HTTP, auth, tool annotations), ✅ MCP integration tests acting as Nexa. 🔜 Entra app roles + Nexa service principal, APIM policies, Nexa prompt and tool configuration, OBO/user identity, end-to-end UAT |
| **Dependencies** | Nexa team: MCP support, auth method, state handling (doc 05 §4.4) |
| **Technical tasks** | 🔜 Protected resource metadata for MCP OAuth discovery (if Nexa supports it); APIM product for Nexa; dashboards (calls, latency, errors, guard rejections) |
| **Acceptance criteria** | ✅ Tool discovery + recommend → report flow over MCP (tested); ✅ no write/admin tools exposed (tested); 🔜 Nexa UAT script passes; 🔜 p95 latency < 10 s with AI enabled |

### Phase 5 — Multi-vendor expansion (weeks 10–20)

| | |
|---|---|
| **Deliverables** | Salesforce pack, OutSystems pack, cross-vendor capability ontology (common capability codes), vendor selector in UI and Nexa routing, cross-vendor comparison (opportunity #5) |
| **Dependencies** | Curator per vendor; partner data rights; vendor-specific clarification and fit rules from senior consultants |
| **Technical tasks** | Pack authoring (mostly data); `CommonCapabilityCode` mapping; optional `Connectors.Salesforce` (org metadata/usage via Salesforce APIs) for the adoption advisor; per-vendor `RecommendationOptions` |
| **Acceptance criteria** | A new vendor goes live **without platform code changes** (only pack + documents + tests); vendor isolation tests extended; golden set per vendor passes |

## 2. Multi-vendor expansion design (Step 7 — Scale)

### 2.1 What a new vendor needs

| Artifact | Planview (done) | Salesforce (plan) | OutSystems (plan) |
|---|---|---|---|
| Capability model | 20 PPM/agile/work-management capabilities | Sales automation, service/case management, marketing automation, commerce, CPQ, field service, analytics, integration (MuleSoft), data cloud, AI agents, industry clouds, platform/app dev | Low-code app dev, mobile, workflow/BPM, integration, AI-assisted development, DevOps/lifecycle, hosting (cloud/self-managed), scalability, governance/architecture tooling |
| Products | Portfolios, AdaptiveWork, AgilePlace, … | Clouds/products (Sales Cloud, Service Cloud, …) and **editions** as separate catalogue entries where editions differ in capabilities | Platform offerings and **editions/capacity units** |
| Licensing model | NotPublic; sizing by persona | Editions and per-user list prices are partly public: model them as `PubliclyDocumented` with cited source and `retrievedOn`, **never** as negotiated price | Capacity-based (application objects, users, environments): sizing drivers differ — add `applicationObjects`, `environments` persona drivers |
| Clarification rules | Methodology, personas, integrations | Users by role (sales reps, service agents, partners), existing CRM, channels, industry, data residency | Number and type of apps, internal vs external users, integration count, hosting constraints, team skills |
| Fit rules (Avantra) | Agile + portfolio, enterprise scale | Edition fit by required features; industry-cloud fit | Internal vs external user mix; modernisation scenarios |
| Connectors (optional) | — | Org usage/feature adoption (Salesforce REST/Tooling API) | App inventory (OutSystems APIs) for the modernisation assessment |

### 2.2 Platform changes needed (small and generic)

1. **Persona drivers become pack data** (today `LicensingEvaluator.Personas` is a fixed map). Move them into the pack (`sizingPersonas: [{code, label, profileField}]`) and make `OrganizationProfile` extensible (`Dictionary<string,int> Counts`). *This is the one known MVP limitation for Salesforce/OutSystems.*
2. **Edition modelling**: represent editions as products with `ProductFamily` + `Edition` fields, so the engine can choose "Sales Cloud Enterprise vs Unlimited" by capability coverage. Avoid a separate edition engine.
3. **Cross-vendor advisory**: an orchestrator that runs per-vendor advisories over a shared requirement profile mapped through `CommonCapabilityCode`, then composes a multi-vendor architecture with integration considerations.
4. **Routing**: `list_vendor_domains` + a lightweight vendor classifier (keywords/LLM) so Nexa can pick the vendor or ask the user.

## 3. Testing strategy

| Layer | What | Tooling | In repo |
|---|---|---|---|
| **Unit** | Extraction (numbers, methodology, priorities), engine (ranking, determinism, gaps, rules), evidence grades, licensing (no prices), guards (injection, price, narrative), parsers, chunker, classifier, index isolation, pack validation | xUnit | ✅ 37 tests |
| **Integration** | Full HTTP host via `WebApplicationFactory`: auth 401/403, recommend, follow-up session, review gate and report watermark, tenant isolation, quarantine, ProblemDetails, compare, **AI path with fake `IChatClient`** (grounded vs hallucinated), **MCP client** discovery/recommend/report/licensing/auth | xUnit + Mvc.Testing + MCP C# client | ✅ 13 tests |
| **Pack tests (data quality)** | Every shipped pack validates; golden scenarios per vendor produce the expected top option | xUnit (runs on every pack PR) | ✅ validation; 🔜 golden set |
| **AI evaluation** | Extraction accuracy, narrative groundedness, guard pass rate, against the real model | `Microsoft.Extensions.AI.Evaluation` (nightly, not in PR gate) | 🔜 |
| **Retrieval evaluation** | recall@k, MRR on the golden Q→doc set | custom harness | 🔜 |
| **UI** | Advisor flow smoke test | Playwright | 🔜 (used manually during MVP build) |
| **Security** | Prompt-injection red-team corpus, OWASP ZAP baseline, dependency scanning | ZAP, Dependabot | 🔜 |
| **Performance** | 50 concurrent advisory runs; p95 targets | Azure Load Testing | 🔜 |

**Principle:** tests run with **no network and no AI key**. The AI path is tested with deterministic fakes, so CI is fast and free. Real-model evaluation runs separately with budgets.

## 4. Deployment strategy

### 4.1 Environments

`dev` (local SQLite + in-memory index) → `test` (Azure, AI Search Basic, SQL serverless) → `prod` (zone-redundant where available, private networking).

### 4.2 Pipeline (GitHub Actions — `.github/workflows/ci.yml`)

1. **Build & test**: `dotnet restore` (locked), `dotnet build -warnaserror` (optional), `dotnet test`, publish test results.
2. **Package**: container images for `api` and `web` (`deployment/docker/*.Dockerfile`), pushed to ACR with an immutable tag (git SHA).
3. **Infrastructure**: `az deployment group create -f deployment/bicep/main.bicep` (what-if on PR, apply on main).
4. **Database**: EF Core **migrations bundle** (`dotnet ef migrations bundle`) executed as a pre-deploy job. The MVP uses `EnsureCreated`; switch to migrations before the first Azure deployment.
5. **Deploy**: Container Apps revision with traffic split (blue/green: 0% → smoke tests → 100%).
6. **Knowledge**: pack changes trigger the ingestion job; the vendor pack validator gates the PR.

### 4.3 Configuration in Azure

| Setting | Source |
|---|---|
| `AI__Endpoint`, `AI__ChatModel`, `AI__EmbeddingModel` | Container App env vars (non-secret) |
| `AI__ApiKey` (only if not using managed identity) | Key Vault secret `AI--ApiKey` via `KeyVault__Uri` |
| `Database__Provider=SqlServer`, `ConnectionStrings__Advisory` | Env var; Entra auth (`Authentication=Active Directory Default`) |
| `Knowledge__IndexProvider=AzureAiSearch`, `Knowledge__AzureAiSearch__Endpoint` | Env vars; managed identity (no key) |
| `Auth__Mode=EntraId`, `Auth__EntraId__TenantId`, `Auth__EntraId__Audience` | Env vars |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Env var |

### 4.4 Scaling and resilience

- API is stateless (sessions in SQL, MCP stateless) → horizontal scale on HTTP concurrency (KEDA). Min 1 replica in prod to avoid cold starts.
- The in-memory index is **only for dev/test**. In Azure, use `IndexProvider=AzureAiSearch` so replicas share one index.
- Model calls: 60 s timeout; failure degrades to deterministic mode, so the advisor stays up during model outages.
- SQL: retry on transient failures (`EnableRetryOnFailure`).
