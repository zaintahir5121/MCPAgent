# Avantra Advisory Agent (Planview first)

**One project** that contains everything:

| Part | Where | What it does |
|---|---|---|
| **Chat page** | `http://localhost:5090/` | One page with a chat box. Ask anything about Planview, or describe your needs to get a recommendation. "Ingest Planview now" button. |
| **REST API** | `/api/...` | Chat, Planview ingestion (run now + schedule status), document upload, product list, status. |
| **MCP server** | `/mcp` | Tools for any MCP client (Nexa, a one-page MCP client, assistants): `ask_advisor` + `planview_*`. |
| **Planview connector** | `Connectors/Planview` | Searches its own index **`planview-index`**, recommends, compares, licensing guidance, live Planview API calls. |

Access management is **not** done here — the existing platform (gateway / Nexa) in front of the service handles it.
Optional headers `X-Tenant-Id` and `X-User-Id` are recorded for audit.

## Run

```bash
cd src/Avantra.AdvisoryAgent
dotnet run            # opens http://localhost:5090
```

On start it creates `advisory-agent.db` (SQLite), ingests `knowledge/vendors/planview/documents` into `planview-index`
and re-ingests every 24 hours (unchanged files are skipped). Without an AI key it runs in **deterministic mode**
(recommend / refine / compare / search) — fully usable.

### Use your AI endpoint (agent mode)

```bash
dotnet user-secrets set "AI:Endpoint"  "https://<your-endpoint>/v1/"   # OpenAI-compatible, e.g. Azure OpenAI v1
dotnet user-secrets set "AI:ApiKey"    "<key>"
dotnet user-secrets set "AI:ChatModel" "<model or deployment>"
dotnet user-secrets set "AI:EmbeddingModel" "<embedding model>"       # optional, enables vector search
```

With a model configured, the chat becomes an **agent** (Microsoft Agent Framework) that decides which Planview tools to call.

### Use Azure AI Search instead of the local index

```json
"Search": { "Provider": "AzureAiSearch", "Endpoint": "https://<name>.search.windows.net", "ApiKey": "",
            "Indexes": { "planview": "planview-index" } }
```

Empty `ApiKey` → managed identity. The index is created automatically on first ingestion.

### Live Planview data (agentic)

```json
"Connectors": { "Planview": { "Api": { "BaseUrl": "https://<your-planview-api>/", "ApiKey": "<in user-secrets>" } } }
```

The `planview_api_get` tool then makes read-only GET calls under that base URL.

## API

| Method | Path | Purpose |
|---|---|---|
| POST | `/api/chat` | `{ "message": "...", "chatId": "optional" }` → `{ chatId, answer (Markdown), mode }` |
| POST | `/api/connectors/planview/ingest` | Ingest the Planview source folder into `planview-index` now (409 if already running) |
| GET | `/api/connectors/planview/ingest` | Schedule status: last run, next scheduled run, interval |
| POST | `/api/connectors/planview/documents` | Upload one file (PDF, DOCX, HTML, MD, TXT). Held for review unless `publish=true`; suspicious files are quarantined |
| POST | `/api/connectors/planview/documents/{id}/publish` | Publish a held document into the index |
| GET | `/api/connectors/planview/documents` | Documents and their status |
| GET | `/api/connectors/planview/products` | Planview product catalogue |
| GET | `/api/status` | Mode, index, document count, ingestion status |

Schedule settings: `Connectors:Planview:IngestionIntervalHours` (0 = off), `RunIngestionOnStartup`, `SourceFolder`.

## MCP

```json
{ "mcpServers": { "avantra-advisor": { "type": "http", "url": "http://localhost:5090/mcp" } } }
```

Tools: `ask_advisor` (agentic, keeps `chatId`), `planview_search_knowledge`, `planview_recommend_products`,
`planview_compare_products`, `planview_licensing_guidance`, `planview_list_products`, `planview_api_get`.

## Adding Salesforce and OutSystems later

Copy the Planview pattern: a `Connectors/Salesforce` folder with its own options, ingestion job and `salesforce_*` MCP tools,
a `knowledge/vendors/salesforce` pack, and index `salesforce-index` (already mapped in `Search:Indexes`).

## Tests

`dotnet test tests/Avantra.AdvisoryAgent.Tests` — chat flows, ingestion, uploads/quarantine, MCP tools, AI-agent path with a fake model.
