# Avantra AI Advisory Platform

An enterprise AI advisory platform on **.NET 10** that turns customer requirements into **explainable, evidence-backed technology recommendations**: products, solution bundles, licensing considerations, gaps, risks and a shareable report. It is exposed to every channel (web, Nexa chatbot, Marketplace, CRM) through a **REST API** and an **MCP server**.

First vendor domain: **Planview**. Salesforce and OutSystems are added as data packs (see [docs/07](docs/07-implementation-roadmap.md)).

> **Not just another RAG chatbot.** Product selection is done by a deterministic engine over a curated, versioned catalogue. AI is used only to understand free text and to write prose, and both outputs are validated. Every claim is cited, prices are never generated, and customer-facing reports need human approval.

---

## Documentation

**Simple overview (draw.io, 3 tabs: High-Level Architecture · Low-Level Design · Delivery Plan):** [`docs/diagrams/avantra-ai-advisory-overview.drawio`](docs/diagrams/avantra-ai-advisory-overview.drawio) — PNG previews in `docs/diagrams/overview-*.png`.

**Detailed one-page architecture (draw.io):** [`docs/diagrams/avantra-ai-advisory-architecture.drawio`](docs/diagrams/avantra-ai-advisory-architecture.drawio) — open at https://app.diagrams.net (File → Open from → Device) or in VS Code with the Draw.io Integration extension.

| # | Document | Contents |
|---|---|---|
| 01 | [Executive proposal](docs/01-executive-proposal.md) | Challenge & analysis of the vision, product strategy, value proposition, commercial model, **opportunity prioritisation matrix** |
| 02 | [Enterprise architecture](docs/02-enterprise-architecture.md) | Principles, decisions & trade-offs, business/application/data/AI/integration/security/infra views, **8 Mermaid diagrams**, component specs, solution structure, technology maturity |
| 03 | [Knowledge & data architecture](docs/03-knowledge-and-data-architecture.md) | Knowledge packs, ingestion pipeline, hybrid retrieval, SQL vs Search vs graph, provenance & evidence grades, lifecycle, quality metrics, database schema |
| 04 | [AI agent & recommendation](docs/04-ai-agent-and-recommendation-architecture.md) | Reasoning pipeline, scoring model, LLM vs rules vs RAG, guardrails, Microsoft Agent Framework usage, multi-agent evaluation |
| 05 | [MCP & Nexa integration](docs/05-mcp-and-nexa-integration.md) | MCP spec, **10 tool contracts**, what stays internal, MCP security, Nexa conversation design, **REST API contracts** |
| 06 | [Security & governance](docs/06-security-and-governance.md) | Threat model, Entra ID & app roles, tenant isolation, prompt-injection & poisoning defences, AI governance, audit |
| 07 | [Roadmap, testing, deployment, scale](docs/07-implementation-roadmap.md) | Phases 1–5 with acceptance criteria, multi-vendor expansion, testing strategy, CI/CD & Azure configuration |
| 08 | [Architecture review](docs/08-architecture-review.md) | Critical review: bottlenecks, security gaps, weaknesses, items needing business confirmation |
| 09 | [Demo scenarios](docs/09-demo-scenarios.md) | Captured end-to-end runs (REST + MCP/Nexa), governance demo, [sample report](docs/samples/sample-advisory-report.md) |

---

## Quick start

**Prerequisites:** .NET SDK 10.0.100+.

```bash
dotnet test Avantra.AIAdvisory.sln            # 50 tests, no network or AI key needed

# Terminal 1 - API + MCP server (http://localhost:5080)
cd src/Avantra.AIAdvisory.Api
dotnet run

# Terminal 2 - Blazor advisory UI (http://localhost:5180)
cd src/Avantra.AIAdvisory.Web
dotnet run
```

Open http://localhost:5180, pick a sample ("Enterprise SPM"), and click **Get recommendation**.

On first start the API creates `advisory.db` (SQLite), loads the Planview pack, ingests the pack documents and builds the in-memory hybrid index. Without AI settings it runs in **deterministic mode**, which is fully functional.

### Connect your OpenAI-compatible endpoint

Configuration section `AI` (never commit keys):

```bash
cd src/Avantra.AIAdvisory.Api
dotnet user-secrets set "AI:Endpoint" "https://<your-endpoint>/v1/"      # e.g. https://<res>.openai.azure.com/openai/v1/
dotnet user-secrets set "AI:ApiKey" "<your-key>"
dotnet user-secrets set "AI:ChatModel" "<model-or-deployment-name>"
dotnet user-secrets set "AI:EmbeddingModel" "<embedding-model>"          # optional: enables vector + BM25 hybrid
```

Or use environment variables: `AI__Endpoint`, `AI__ApiKey`, `AI__ChatModel`, `AI__EmbeddingModel`. In Azure, put `AI--ApiKey` in Key Vault and set `KeyVault__Uri` (managed identity). The provider is reached through `Microsoft.Extensions.AI`'s `IChatClient`, so switching model or vendor is a configuration change.

`GET http://localhost:5080/` shows the active mode (`deterministic-only` or `ai-assisted`).

### Try the API

Development API keys (from `appsettings.Development.json`, dev only): `dev-sales-key` (Advisory.User), `dev-reviewer-key` (User + Reviewer + Knowledge.Admin), `dev-nexa-key` (Advisory.User).

```bash
curl -s -X POST http://localhost:5080/api/v1/advisory/recommend \
  -H "X-Api-Key: dev-sales-key" -H "Content-Type: application/json" \
  -d '{"vendorCode":"planview","requirements":"We are a company with 2,000 employees, 150 project managers, 40 portfolio managers, and multiple business units. We need enterprise portfolio management, strategic planning, resource management, financial management, and project governance."}'
```

OpenAPI: `http://localhost:5080/openapi/v1.json`.

### Connect an MCP client (Nexa, VS Code, Claude, Copilot Studio…)

```json
{
  "mcpServers": {
    "avantra-advisory": {
      "type": "http",
      "url": "http://localhost:5080/mcp",
      "headers": { "X-Api-Key": "dev-nexa-key" }
    }
  }
}
```

Tools: `list_vendor_domains`, `search_vendor_knowledge`, `retrieve_product_capabilities`, `analyze_customer_requirements`, `recommend_vendor_products`, `compare_subscription_plans`, `evaluate_licensing_requirements`, `identify_solution_gaps`, `generate_advisory_report`, `ask_vendor_knowledge_assistant`. Production uses Entra ID bearer tokens (see [docs/05](docs/05-mcp-and-nexa-integration.md)).

### Demonstrate the security controls

```bash
# Poisoned document -> Quarantined, never retrievable
curl -s -X POST http://localhost:5080/api/v1/knowledge/documents -H "X-Api-Key: dev-reviewer-key" \
  -F vendorCode=planview -F publish=true -F file=@knowledge/samples/poisoned-datasheet.md

# Sales user cannot approve (403); reviewer can
curl -s -o /dev/null -w "%{http_code}\n" -X POST http://localhost:5080/api/v1/advisory/sessions/<id>/review \
  -H "X-Api-Key: dev-sales-key" -H "Content-Type: application/json" -d '{"approve":true}'
```

---

## Solution structure

```
src/
  Avantra.AIAdvisory.Domain          Catalogue, knowledge, advisory and governance models (no dependencies)
  Avantra.AIAdvisory.Application     Use cases + ports: orchestrator, recommendation engine, evidence, licensing, reports, guards
  Avantra.AIAdvisory.KnowledgeBase   Parsers (MD/HTML/PDF/DOCX/TXT), chunking, classification, ingestion, hybrid index, Azure AI Search, packs
  Avantra.AIAdvisory.Agents          LLM extractor, grounded narrative, Microsoft Agent Framework knowledge assistant, prompts
  Avantra.AIAdvisory.Infrastructure  EF Core (SQLite / SQL Server), sessions, audit, OpenAI-compatible AI provider
  Avantra.AIAdvisory.McpServer       MCP tools (thin adapters over application services)
  Avantra.AIAdvisory.Api             ASP.NET Core host: /api/v1, /mcp, auth, OpenAPI, telemetry
  Avantra.AIAdvisory.Web             Blazor advisory UI (consumes the REST API)
tests/
  Avantra.AIAdvisory.UnitTests       37 tests (engine, extraction, guards, parsers, index, pack validation)
  Avantra.AIAdvisory.IntegrationTests 13 tests (REST, auth, tenant isolation, AI path with fake model, MCP client)
knowledge/
  vendors/planview/                  pack.json (capabilities, products, licensing disclosure, rules) + documents/
  samples/                           poisoned document for the security demo
deployment/
  bicep/main.bicep                   Azure: Container Apps, Azure SQL, AI Search, Storage, Key Vault, App Insights, managed identity
  docker/                            API and Web Dockerfiles; docker-compose.yml
.github/workflows/ci.yml             Build, test, vulnerability check, container build
```

## Configuration reference

| Key | Default | Purpose |
|---|---|---|
| `AI:Endpoint` / `AI:ApiKey` / `AI:ChatModel` | empty | OpenAI-compatible chat model; empty means deterministic mode |
| `AI:EmbeddingModel` | empty | Enables vector retrieval (hybrid with BM25) |
| `Database:Provider` | `Sqlite` | `Sqlite` or `SqlServer` |
| `ConnectionStrings:Advisory` | `Data Source=advisory.db` | Database connection |
| `Knowledge:PacksPath` | `knowledge/vendors` | Vendor packs folder (searched upward from the app directory) |
| `Knowledge:IndexProvider` | `InMemory` | `InMemory` (dev/test) or `AzureAiSearch` |
| `Knowledge:AzureAiSearch:Endpoint` | empty | Azure AI Search endpoint (managed identity if `ApiKey` is empty) |
| `Knowledge:SeedDocumentsOnStartup` | `true` | Ingest pack documents at start (disable in Azure; use the ingestion job: `--ingest-only`) |
| `Auth:Mode` | `EntraId` (`ApiKey` in Development) | `EntraId`, `ApiKey`, or both |
| `Auth:EntraId:TenantId` / `Audience` | — | Token validation |
| `KeyVault:Uri` | empty | Loads secrets from Key Vault via managed identity |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | empty | Enables Azure Monitor OpenTelemetry |

## Important: demonstration data

The Planview catalogue and documents in `knowledge/vendors/planview` are **demonstration data**: an Avantra summary of publicly known product positioning, **pending curator verification** against official Planview sources. Source links point to planview.com and must be replaced with exact product pages or datasheets. **No pricing, editions or licence tiers are included or inferred.** Responses and reports carry this disclaimer automatically until the pack is verified (see [docs/03 §10](docs/03-knowledge-and-data-architecture.md#10-sample-knowledge-base-in-this-repository)).
