# 05 — MCP Integration Specification & Nexa Integration Design

> Deliverables covered: (9) MCP integration specification, (10) Nexa integration design, (14) API contracts, (15) MCP tool contracts.

## 1. Responsibilities — MCP server vs agent vs API vs orchestration

| Component | Responsibility | In this platform |
|---|---|---|
| **Advisory REST API** | System-of-record contract for all channels; full-fidelity results; admin and review operations | `/api/v1/*` (primary) |
| **Orchestration service** | Runs the advisory pipeline; owns sessions, evidence, audit | `AdvisoryOrchestrator` (in-process) |
| **AI agent** | Open-ended, tool-using Q&A | `KnowledgeAssistantAgent` (MAF) |
| **MCP server** | Protocol adapter that lets LLM-based clients (Nexa, Copilot Studio, Claude, IDEs) discover and call advisory capabilities | `/mcp` (Streamable HTTP, stateless) |
| **Nexa** | Conversation UX, intent detection, deciding *when* to call a tool, phrasing answers and questions | External client |

**Nexa should not re-implement advisory logic, and MCP tools must not contain it.** Both REST and MCP call the same application services, which is verified by tests calling both surfaces.

### Should Nexa use MCP or REST?

| Nexa is… | Use |
|---|---|
| LLM-based and supports MCP (Streamable HTTP) | **MCP**: tool discovery, descriptions guide the model, zero glue code |
| LLM-based without MCP support | REST via its function-calling layer (OpenAPI at `/openapi/v1.json`) |
| Scripted/intent bot (no LLM) | REST: call `/api/v1/advisory/recommend` and render the fields |

## 2. MCP server specification

| Property | Value |
|---|---|
| Endpoint | `POST https://<api-host>/mcp` |
| Transport | MCP **Streamable HTTP**, **stateless** (`HttpServerTransportOptions.Stateless = true`) — horizontally scalable, no sticky sessions |
| Server info | name `avantra-ai-advisory`, version `1.0.0`, instructions guide clients to use `sessionId`, show citations, and never state prices |
| Serialisation | JSON, enums as strings |
| AuthN | Entra ID bearer token (`aud = api://avantra-ai-advisory`), or `X-Api-Key` in development |
| AuthZ | Policy `Advisory.Read` (roles `Advisory.User`, `Advisory.Reviewer`, `Knowledge.Admin`); tenant from `tid` claim |
| State | Platform-side `sessionId` returned by `recommend_vendor_products`; the client stores it in its conversation state |
| Tool annotations | `ReadOnly`, `Idempotent`, `OpenWorld=false` set per tool so clients can auto-approve safe calls |

### 2.1 Tool contracts

All tools take `vendorCode` (e.g. `planview`) unless noted. Errors are returned as MCP tool errors with a message (unknown vendor, invalid input, session not found).

#### `list_vendor_domains`
- **Input**: none
- **Output**: `[{ code, name, description, packVersion, dataDisclaimer, isDemonstrationData }]`
- **Use**: discover vendor codes.

#### `search_vendor_knowledge` (read-only)
- **Input**: `vendorCode`, `query`, `top?` (1–20, default 5), `productCode?`, `minimumTrust?` (`Unverified|ThirdParty|AvantraInternal|PartnerVerified|VendorOfficial`)
- **Output**: `{ query, vendorCode, hits: [{ chunkId, documentId, documentTitle, sourceUrl, section, snippet, score, trust, isDemonstrationData, productCodes, capabilityCodes }], notice }`
- **Rules**: published documents only; vendor filter mandatory; `notice` says what to do when there are no results ("Do not answer from general knowledge").

#### `retrieve_product_capabilities` (read-only)
- **Input**: `vendorCode`, `productCode?` (code, name or alias)
- **Output**: `{ vendor, packVersion, notice, capabilityModel: [{code,name,category}], products: [Product with capabilities, coverage, sources, verification, implementationConsiderations] }`

#### `analyze_customer_requirements` (read-only, nothing persisted)
- **Input**: `vendorCode`, `requirements` (≤ 8,000 chars)
- **Output**: `{ profile: { organization, requirements:[{id, statement, capabilityCode, priority, origin}], unmappedStatements, analyzerUsed }, clarifications:[{code, question, whyItMatters, priority}], matchedCapabilities }`

#### `recommend_vendor_products` (creates/updates a session)
- **Input**: `vendorCode`, `requirements`, `sessionId?`
- **Output (compact)**:
```json
{
  "sessionId": "guid", "reviewStatus": "Draft",
  "executiveSummary": "…", "executiveSummarySource": "ai-grounded | deterministic-template",
  "organization": { "employees": 2000, "projectManagers": 150, "...": "..." },
  "requirements": [{ "id": "R1", "statement": "…", "capabilityCode": "portfolio-management", "priority": "Must", "origin": "Stated" }],
  "options": [{ "label": "Recommended", "productNames": ["Planview Portfolios"], "mustHaveCoverage": 1.0, "weightedCoverage": 0.96, "score": 0.98,
                "coverage": [{ "requirement": "R1", "productCode": "planview-portfolios", "coverage": "Core", "evidence": "DocumentSupported", "citationIds": ["C1","C2"] }],
                "gaps": [], "avantraRules": ["…"], "considerations": ["…"] }],
  "licensing": [{ "productName": "Planview Portfolios", "statement": "…confirm with Planview…", "requiresVendorConfirmation": true,
                  "sizingInputs": [{ "persona": "Project managers", "count": 150, "basis": "Stated by customer…" }] }],
  "clarificationQuestions": [{ "question": "…", "whyItMatters": "…" }],
  "assumptions": [{ "kind": "…", "text": "…" }], "warnings": [{ "kind": "…", "text": "…" }],
  "citations": [{ "id": "C1", "documentTitle": "…", "url": "…", "section": "…", "snippet": "…", "trust": "AvantraInternal", "isDemonstrationData": true }],
  "dataDisclaimer": "…",
  "nextSteps": ["Ask the user the clarification questions and call recommend_vendor_products again with the same sessionId.", "…"]
}
```
- **Full result** (every field) is available at `GET /api/v1/advisory/sessions/{sessionId}`.

#### `compare_subscription_plans` (read-only)
- **Input**: `vendorCode`, `productCodes[]` (codes, names or aliases), `capabilityCodes[]?`
- **Output**: `{ productCodes, productNames, rows:[{capabilityCode, capabilityName, coverageByProduct:{code: "Core|Supported|ViaIntegration|Not listed"}}], licensing:[LicensingAssessment], citations, notice }`
- **Rule**: "Not listed" ≠ "not available" (stated in `notice`).

#### `evaluate_licensing_requirements` (read-only)
- **Input**: `vendorCode`, `productCodes[]`, `employees?`, `projectManagers?`, `portfolioManagers?`, `resourceManagers?`, `teamMembers?`, `executives?`
- **Output**: `[{ productCode, productName, knownOptions:[{code, name, description, disclosure, licenseMetric, sizingDrivers, publicTerms:null}], sizingInputs, requiresVendorConfirmation, statement, citationIds }]`
- **Rule**: never returns or estimates prices (`Licensing_tool_never_returns_prices` test).

#### `identify_solution_gaps` (read-only)
- **Input**: `vendorCode`, `requirements`, `productCodes[]` (what the customer owns or is considering)
- **Output**: `{ profile, evaluation: SolutionOption (coverage, gaps, evidence), unknownProductCodes, suggestedAdditionalProducts, citations, notice }`

#### `generate_advisory_report` (read-only)
- **Input**: `sessionId`, `customerName?`
- **Output**: `{ sessionId, title, review, generatedAt, markdown, downloadPath }`
- **Rule**: DRAFT watermark unless a reviewer approved the session.

#### `ask_vendor_knowledge_assistant` (read-only)
- **Input**: `vendorCode`, `question`
- **Output**: `{ answer, mode: "agent | retrieval-only", sourcesConsulted, notice }`

### 2.2 What stays behind internal APIs (not MCP)

| Operation | Why not MCP |
|---|---|
| Document ingestion, publishing, quarantine override | Write access to the knowledge base must not be reachable by an LLM client (poisoning vector) |
| Review/approval of advisory results | Human accountability; must be an explicit UI action by a reviewer |
| Catalogue/pack editing, fit-rule changes | Governance; curator workflow |
| Audit log access, tenant administration | Security |
| Partner-confidential licensing (future) | Audience control; internal UI only |
| Raw scoring internals / prompt contents | Prompt-injection surface and IP |

## 3. Security for MCP

1. **Identity**: Nexa is registered in Entra ID as a confidential client (or uses a **managed identity** if hosted in Azure). It is granted the API app role `Advisory.User` (application permission). Token: client credentials flow, scope `api://avantra-ai-advisory/.default`.
2. **User context (recommended Phase 4)**: when Nexa acts for an authenticated employee or customer, use **On-Behalf-Of** (employee) or pass a signed user assertion so the platform can scope sessions per end user. Without it, sessions are scoped to the Nexa service identity within its tenant.
3. **MCP authorization spec**: the platform can publish OAuth Protected Resource Metadata so standards-based MCP clients discover the Entra authorization server automatically. Confirm Nexa's support; the C# SDK provides the server-side pieces.
4. **Gateway** (recommended): put `/mcp` behind **Azure API Management** for rate limiting per client, IP restrictions, request logging and quota. Check the current GA status of APIM's MCP-specific features before relying on them.
5. **Least privilege**: all MCP tools are read-only, or session-scoped writes (`recommend_vendor_products`). No tool can change knowledge or approve output.
6. **Prompt-injection resilience**: tool outputs are data from published, reviewed sources. Retrieved text is delimited, and the server instructions tell clients not to state prices that tools did not return.
7. **Audit**: every `recommend_vendor_products` call writes an audit entry with `channel = mcp`.

## 4. Nexa integration design

### 4.1 Conversation pattern

```
User: "We're 2,000 people, 150 PMs, need portfolio, resources and finance. What Planview should we buy?"
Nexa (LLM) → tools/call recommend_vendor_products {vendorCode:"planview", requirements:"<user text>"}
Nexa ← {sessionId, executiveSummary, options, clarificationQuestions, citations, dataDisclaimer}
Nexa → User: summary + top option + 2–3 most important clarification questions + "Sources: C1, C2" + disclaimer
User: "Hybrid. Jira for dev teams. 600 team members."
Nexa → tools/call recommend_vendor_products {vendorCode:"planview", sessionId:"…", requirements:"<answers>"}
Nexa ← refined result (Portfolios + AgilePlace + Hub)
User: "Send me a report."
Nexa → tools/call generate_advisory_report {sessionId:"…", customerName:"Contoso"}
Nexa → User: report (DRAFT) + "A solution architect will review before it is shared externally."
```

### 4.2 Nexa system-prompt guidance (for Nexa's own LLM)

> When a user asks which products, editions, subscriptions or licences they need for Planview (or another supported vendor), call `recommend_vendor_products`. Keep the returned `sessionId` and pass it on follow-ups. Ask at most three of the returned clarification questions at a time. Present the recommended option, its gaps and citations. Never state prices, editions or tiers unless a tool returned them with a source; say they must be confirmed with the vendor or Avantra. Always include the `dataDisclaimer` when `isDemonstrationData` is true. For general product questions use `ask_vendor_knowledge_assistant` or `search_vendor_knowledge`.

### 4.3 Client configuration examples

**.NET (C# MCP SDK) — how Nexa (or any .NET service) connects:**

```csharp
var http = new HttpClient();
http.DefaultRequestHeaders.Authorization = new("Bearer", await GetTokenAsync("api://avantra-ai-advisory/.default"));
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("https://advisory.avantra.example/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp,
    Name = "nexa",
}, http, ownsHttpClient: true);
await using var mcp = await McpClient.CreateAsync(transport);

var tools = await mcp.ListToolsAsync();               // hand these to Nexa's IChatClient as AITools
var result = await mcp.CallToolAsync("recommend_vendor_products", new Dictionary<string, object?>
{
    ["vendorCode"] = "planview",
    ["requirements"] = userText,
});
```

`McpClientTool` instances returned by `ListToolsAsync` are `AIFunction`s, so they plug directly into `ChatOptions.Tools` of a Microsoft.Extensions.AI `IChatClient`, or into a Microsoft Agent Framework `ChatClientAgent`.

**Generic MCP client config (development):**

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

### 4.4 Nexa adapter needs (confirm with the Nexa team)

| Question | Why |
|---|---|
| Does Nexa support MCP Streamable HTTP and custom headers or OAuth? | Transport/auth choice |
| Where is conversation state stored, and can it hold `sessionId`? | Follow-ups |
| Can Nexa render Markdown tables and links? | Report display; else use `downloadPath` |
| Does Nexa know the end-user identity (employee vs customer)? | OBO / per-user scoping, audience filtering |
| Latency budget per turn? | The advisory run is ~50–200 ms deterministic; plus 2 LLM calls (~2–8 s) when AI is enabled |

## 5. REST API contracts (v1)

OpenAPI 3.1 document at `/openapi/v1.json`. All endpoints require authentication; errors are RFC 9457 ProblemDetails with `traceId`.

| Method & path | Policy | Body / query | Response |
|---|---|---|---|
| `GET /api/v1/vendors` | Advisory.Read | — | `Vendor[]` |
| `GET /api/v1/vendors/{vendor}/capabilities` | Advisory.Read | — | `Capability[]` |
| `GET /api/v1/vendors/{vendor}/products` | Advisory.Read | — | `Product[]` |
| `GET /api/v1/vendors/{vendor}/products/{code}` | Advisory.Read | — | `{ product, licensing[] }` |
| `POST /api/v1/vendors/{vendor}/knowledge/search` | Advisory.Read | `{ query, top?, productCode?, minimumTrust? }` | `KnowledgeSearchResult` |
| `POST /api/v1/advisory/analyze` | Advisory.Read | `{ vendorCode, requirements }` | `RequirementAnalysis` |
| `POST /api/v1/advisory/recommend` | Advisory.Read | `{ vendorCode, requirements, sessionId? }` | `AdvisoryResult` (full) |
| `POST /api/v1/advisory/gaps` | Advisory.Read | `{ vendorCode, requirements, productCodes[] }` | `GapAnalysis` |
| `POST /api/v1/advisory/compare` | Advisory.Read | `{ vendorCode, productCodes[], capabilityCodes?[] }` | `ProductComparison` |
| `POST /api/v1/advisory/ask` | Advisory.Read | `{ vendorCode, question }` | `AssistantAnswer` |
| `GET /api/v1/advisory/sessions?take=` | Advisory.Read | — | sessions (own; all in tenant for reviewers) |
| `GET /api/v1/advisory/sessions/{id}` | Advisory.Read | — | `AdvisoryResult` |
| `POST /api/v1/advisory/sessions/{id}/review` | **Advisory.Review** | `{ approve, comment? }` | `{ sessionId, review }` |
| `GET /api/v1/advisory/sessions/{id}/report?format=md\|html\|json&customerName=` | Advisory.Read | — | report |
| `GET /api/v1/knowledge/documents?vendor=&status=` | **Knowledge.Admin** | — | documents |
| `POST /api/v1/knowledge/documents` (multipart) | **Knowledge.Admin** | `vendorCode, file, sourceUrl?, trust?, title?, publish?` | `IngestionResult` |
| `POST /api/v1/knowledge/documents/{id}/publish` | **Knowledge.Admin** | — | `{ id, status, version }` |
| `GET /health`, `GET /`, `GET /openapi/v1.json` | anonymous | — | health / info / spec |
| `POST /mcp` | Advisory.Read | MCP JSON-RPC | MCP |

**Versioning**: URL major version (`/api/v1`). Additive changes only within v1; MCP tool names are stable and new tools are additive. Breaking changes get a new tool name.
