# AvantraAgent

Production-ready technology advisory agent: **chat page + REST API + MCP server in one service**, over as many
**knowledge bases** as you need. Each knowledge base has **its own search index** (Azure AI Search in production,
in-memory locally). Planview ships pre-configured with full product advisory; add Salesforce, OutSystems,
internal playbooks or anything else at runtime — no code changes.

| Part | Where |
|---|---|
| Chat page (with knowledge-base selector and a **Knowledge** panel to add knowledge) | `/` |
| REST API (OpenAPI at `/openapi/v1.json`) | `/api/...` |
| MCP server (Streamable HTTP, stateless) | `/mcp` |
| Health: liveness / readiness (database + indexes loaded) | `/health/live`, `/health/ready` |

## Run locally

```bash
cd src/AvantraAgent
dotnet run                       # http://localhost:5090
```

Creates `avantra-agent.db` (SQLite), seeds the **planview** knowledge base, and syncs its source folder in the background.
Without an AI key it runs in deterministic mode (recommend / refine / compare / search) — fully usable.
After pulling a version with schema changes, delete the local `.db` file (production uses migrations).

## Adding more and more knowledge

1. **Create a knowledge base** — page: *Knowledge → + New knowledge base*, or:
   ```bash
   curl -X POST http://localhost:5090/api/knowledge-bases -H "Content-Type: application/json" \
     -d '{"code":"salesforce","name":"Salesforce","description":"CRM clouds, editions and licensing"}'
   ```
   It gets its own index (`salesforce-index`, or set `indexName`).
2. **Feed it** (all run as background jobs; poll `GET /api/jobs/{id}`):
   - Upload files (PDF, DOCX, HTML, MD, TXT; up to 20 per request): `POST /api/knowledge-bases/{code}/documents` (multipart `files`)
   - From a URL on an allowed https host: `POST /api/knowledge-bases/{code}/documents/from-url` `{"url":"https://..."}`
   - From a folder, on a schedule: set `sourceFolder` and `syncIntervalHours` (PATCH), or `POST /api/knowledge-bases/{code}/sync`
3. **Ask** — the page, `POST /api/chat {"message":"...","knowledgeBase":"salesforce"|"all"}`, or MCP `ask_advisor`/`knowledge_search`.

Behaviour you can rely on: unchanged files are skipped (content hash), changed files become new versions, suspicious
files are **quarantined**, uploads can wait for review (`Ingestion:PublishUploadsByDefault=false` → `POST .../documents/{id}/publish`).

**Product advisory** (recommendations, comparisons, licensing guidance) is enabled for a knowledge base when a vendor pack
with the same code exists in `knowledge/vendors/<code>/pack.json` (capability model, products, licensing disclosure, rules).
Without a pack, the knowledge base is fully searchable and usable in chat.

## REST API

| Method | Path | Notes |
|---|---|---|
| POST | `/api/chat` | `{ message, chatId?, knowledgeBase? }` → `{ chatId, answer (Markdown), mode, knowledgeBase }` |
| GET | `/api/status` | Mode, readiness, knowledge bases with document counts |
| GET | `/api/knowledge-bases` · `/{code}` | List / details |
| GET | `/api/knowledge-bases/{code}/search?q=` | Direct search of one index |
| GET | `/api/knowledge-bases/{code}/documents` · `/jobs` · `/products` | Documents, recent jobs, product catalogue |
| GET | `/api/jobs/{id}` | Job status with per-file results |
| POST 🔑 | `/api/knowledge-bases` | Create knowledge base |
| PATCH 🔑 | `/api/knowledge-bases/{code}` | Name, description, sourceFolder, syncIntervalHours, isEnabled |
| DELETE 🔑 | `/api/knowledge-bases/{code}` | Delete with its documents |
| POST 🔑 | `/api/knowledge-bases/{code}/documents` | Upload files (202 + job) |
| POST 🔑 | `/api/knowledge-bases/{code}/documents/from-url` | Ingest from URL (202 + job) |
| POST 🔑 | `/api/knowledge-bases/{code}/sync` | Sync source folder now (202 + job) |
| POST 🔑 | `/api/knowledge-bases/{code}/documents/{id}/publish` | Publish a document waiting for review |
| DELETE 🔑 | `/api/knowledge-bases/{code}/documents/{id}` | Remove a document from DB and index |

🔑 = knowledge management. Access control is done by the platform in front of AvantraAgent; additionally, if
`Security:AdminApiKey` is set, these endpoints require header `X-Admin-Key`.

## MCP tools

`ask_advisor`, `knowledge_list`, `knowledge_search`, `advisory_recommend`, `advisory_compare`, `advisory_licensing`,
`advisory_list_products`, `planview_api_get`. All take a `knowledgeBase` code, so new knowledge bases are usable by
MCP clients immediately. Knowledge management is intentionally **not** exposed via MCP.

```json
{ "mcpServers": { "avantra-agent": { "type": "http", "url": "https://<host>/mcp" } } }
```

## Production configuration

Set with environment variables (`Section__Key`) or Azure Key Vault (`KeyVault:Uri`, secret names `Section--Key`, managed identity).

| Setting | Production value |
|---|---|
| `Database:Provider` / `ConnectionStrings:Advisory` | `SqlServer` / Azure SQL with Entra auth (e.g. `Authentication=Active Directory Managed Identity`). Migrations are applied at startup. |
| `Search:Provider` / `Search:Endpoint` | `AzureAiSearch` / `https://<name>.search.windows.net` (empty `ApiKey` → managed identity). Indexes are created automatically. |
| `AI:Endpoint`, `AI:ChatModel`, `AI:EmbeddingModel`, `AI:ApiKey` | OpenAI-compatible endpoint (Azure OpenAI v1). Key from Key Vault. |
| `Security:AdminApiKey` | Optional second lock for knowledge management (Key Vault). |
| `Security:AllowedOrigins` | Browser origins allowed to call the API (CORS). |
| `RateLimits:ChatPerMinute`, `RateLimits:AdminPerMinute` | Per client IP (uses `X-Forwarded-For` behind the gateway). |
| `Ingestion:AllowedUrlHosts` | Hosts allowed for URL ingestion (https only). |
| `Ingestion:StagingPath` | Persistent volume path for uploads waiting to be processed. |
| `KnowledgeBases:Seed` | Knowledge bases created on first start. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | OpenTelemetry → Application Insights. |

Container: `docker build -f deployment/docker/avantra-agent.Dockerfile -t avantra-agent .` (port 8080, non-root).
Configuration is validated at startup; the service refuses to start with an invalid search/AI/ingestion setup.

## Operational notes

- **Scaling**: run one instance for the ingestion worker; with `Search:Provider=AzureAiSearch` chat/MCP can scale out
  (the in-memory index is per instance and meant for development). Jobs interrupted by a restart are marked *Interrupted*.
- **Schema changes**: `dotnet ef migrations add <Name> -p src/Avantra.AIAdvisory.Infrastructure -o Persistence/Migrations`.
- **Tests**: `dotnet test tests/AvantraAgent.Tests` (15 tests: knowledge bases, jobs, isolation, admin key, URL rules, chat, MCP, AI path).
