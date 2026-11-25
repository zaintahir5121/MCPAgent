# 04 — AI Agent & Recommendation Architecture

> Deliverable covered: (8) AI agent architecture. This document explains how the system reasons about requirements, and why it is built as a governed pipeline rather than an autonomous agent.

## 1. Reasoning pipeline (as implemented)

| Step | Component | Technique | Deterministic? | Output |
|---|---|---|---|---|
| 1. Input hygiene | `AdvisoryOrchestrator.Validate`, `ContentSafety` | Length limit (8k chars), sensitive-number redaction, injection detection → warning | Yes | Clean text, security warning |
| 2. Conversation context | `AdvisorySession` | All user turns of the tenant-scoped session are re-analysed together | Yes | Cumulative requirement text |
| 3. Requirements extraction | `RequirementsAnalyzer` = `LlmRequirementsExtractor` ∪ `HeuristicRequirementsExtractor` | LLM JSON extraction **constrained to the pack's capability codes**; regex/synonym extraction in parallel | Hybrid | `RequirementProfile` |
| 4. Validation & merge | `RequirementsAnalyzer.Validate/Merge` | Unknown capability codes dropped to "unmapped"; **numbers from regex win** over model numbers; priority = max; IDs renumbered | Yes | Validated profile |
| 5. Classification | Capability taxonomy (pack) | Requirements carry capability code, priority (Must/Should/Could), origin (Stated/Inferred) | Yes | — |
| 6. Clarification | `ClarificationService` | Data-driven rules: field missing → question + why it matters (conditional on capabilities) | Yes | Ranked questions |
| 7. Product & capability mapping | `RecommendationEngine` | Weighted set cover over catalogue coverage | Yes | Candidate bundles |
| 8. Solution composition & ranking | `RecommendationEngine` | Bundles of 1..3, dominance pruning, lexicographic rank (must-have coverage → score → fewer products), diversity filter, Avantra fit rules | Yes | Ranked `SolutionOption`s with per-requirement explanation |
| 9. Knowledge retrieval & evidence | `EvidenceService` | Catalogue sources + hybrid retrieval per (product, capability), evidence grade | Yes (given the index) | Citations, grades |
| 10. Licensing evaluation | `LicensingEvaluator` | Disclosure status + persona sizing inputs; never prices | Yes | `LicensingAssessment`s |
| 11. Narrative | `GroundedNarrativeGenerator` | LLM writes the executive summary from a **fact sheet only** | No | Draft text |
| 12. Output validation | `NarrativeGuard` | Rejects price statements, products not in the catalogue, unknown citation ids → deterministic template | Yes | Safe summary |
| 13. Human review | `ReviewAsync` (`Advisory.Reviewer`) | Approve / reject; reports are watermarked DRAFT until approved | Human | Review status |
| 14. Report | `AdvisoryReportGenerator` | Markdown/HTML from the result | Yes | Report |

### Scoring model (step 8)

```
weight(Must)=3, weight(Should)=2, weight(Could)=1
coverage(Core)=1.0, coverage(Supported)=0.7, coverage(ViaIntegration)=0.4, coverage(none)=0

for bundle B:
  cov(r, B)        = max over p in B of coverage(p, r.capability)
  weighted(B)      = Σ weight(r)·cov(r,B) / Σ weight(r)
  mustCoverage(B)  = |{r ∈ Must : cov(r,B) > 0}| / |Must|
  score(B)         = weighted(B) − 0.04·(|B|−1) + Σ applicable Avantra fit-rule adjustments

rank by: mustCoverage desc, score desc, |B| asc, codes asc (stable tie-break)
prune:   bundles where a product contributes no best coverage (dominated)
         alternatives that are supersets of a better option with < 0.05 weighted gain (diversity)
```

All constants live in `RecommendationOptions` and can be tuned per vendor. Changes are covered by unit tests on the shipped pack (`Example_scenario_recommends_planview_portfolios_with_full_must_have_coverage`, `Hybrid_delivery_applies_avantra_rule_and_adds_agile_layer`, `Engine_is_deterministic`).

## 2. Where each reasoning technique fits

| Technique | Used for | Not used for | Why |
|---|---|---|---|
| **LLM reasoning** | Understanding free text (synonyms, implied needs, numbers in prose); writing explanations | Deciding products; scoring; licensing; anything a customer could rely on commercially | Non-deterministic; can invent plausible facts |
| **Deterministic business rules** | Scoring, bundle composition, fit rules, clarifications, licensing disclosure | Understanding unstructured language | Repeatable, testable, explainable, auditable |
| **Structured product data** | The single source of truth for "product X provides capability Y" | — | Curated, versioned, verifiable |
| **RAG** | Evidence passages for claims; open Q&A; curator discovery | Being the sole basis of a recommendation | Retrieval can be wrong or stale; graded and cited, never trusted blindly |
| **Knowledge graph** | (Phase 5+) cross-vendor ontology and integration paths | MVP | Not needed yet (doc 03 §5) |
| **Multiple specialised agents** | (Optional, later) proposal drafting with section writers + critic | Core recommendation | Cost, latency, unpredictability; see §6 |

**Rule of thumb:** *The LLM may propose; only deterministic code and verified data may decide; only a human may approve for customers.*

## 3. Safe combination: guardrails at each LLM boundary

| Boundary | Guard |
|---|---|
| LLM extraction output | JSON parsed tolerantly; capability codes validated against the pack; unknown codes moved to `unmapped`; deterministic numbers override; failure → heuristic only (`Analyzer_falls_back_to_heuristic_when_ai_extractor_throws`) |
| Retrieved content entering prompts | Wrapped in `<untrusted_source>` tags; quarantined/pending documents are never retrievable; agent tools are read-only |
| LLM narrative output | `NarrativeGuard`: no prices, no products outside the catalogue, only existing citation ids → else deterministic template (`Hallucinated_price_is_rejected_and_replaced_by_deterministic_summary`) |
| Agent answers (Q&A) | Price-statement guard withholds the answer; answers list the sources consulted |
| All runs | Audit entry with model used, analyzer, pack version, options, summary source |

## 4. Microsoft Agent Framework — where it is used and why

`KnowledgeAssistantAgent` uses `Microsoft.Agents.AI.ChatClientAgent` over the configured `IChatClient`, with two function tools created by `AIFunctionFactory`:

- `search_knowledge(query, productCode?)` → vendor-isolated hybrid search
- `list_products()` → catalogue summary with verification status

It serves open questions ("How do Jira teams connect to portfolio planning in Planview?"). It is **deliberately not** given tools that create recommendations, sessions or licensing statements. Those must go through the deterministic orchestrator. When no model is configured, it returns the top retrieved passages instead of failing.

**Why not build the whole advisor as an MAF agent?** Tool-calling agents choose their own path. For licensing-adjacent advice we need the same input to produce the same output, and every claim to be traceable to code and data. MAF remains the right framework for the conversational and agentic parts (Q&A, future proposal drafting, Teams copilot), and its workflow features can host the orchestrated pipeline later if that adds value (checkpointing, human-in-the-loop steps). Evaluate each workflow feature's preview status first.

## 5. Prompts and model management

- Prompts are versioned constants (`Prompts.Version`). A change is a code review, and the version can be written to audit entries.
- Temperature 0 for extraction and 0.2 for narrative; JSON response format requested, with a tolerant parser for OpenAI-compatible endpoints without strict JSON mode.
- The model and provider come from configuration (`AI:*`). Any OpenAI-protocol endpoint works: OpenAI, Azure OpenAI v1 (`https://<resource>.openai.azure.com/openai/v1/`), Azure AI Foundry, or a self-hosted gateway.
- OpenTelemetry instrumentation on `IChatClient` (`EnableSensitiveData = false`) gives token usage, latency and errors in Application Insights without logging customer text.
- Recommended next step: an **evaluation harness** (`Microsoft.Extensions.AI.Evaluation`) with a golden set of 30–50 scenarios per vendor. It asserts extraction accuracy (capabilities, numbers), recommendation stability, and narrative groundedness (LLM-as-judge plus `NarrativeGuard` pass rate), and runs nightly and on every prompt or pack change.

## 6. Multi-agent evaluation

| Candidate agent | Value | Verdict |
|---|---|---|
| Requirements analyst agent | Covered by bounded extraction | Not an agent; a function |
| Product mapping agent | Must be deterministic | No |
| Licensing agent | High hallucination risk; mostly non-public data | No — rules + disclosure |
| Clarification agent (conversational) | Natural phrasing of questions for Nexa | Optional: Nexa's own LLM already does this from the returned questions |
| **Proposal writer + critic** (Phase 3+) | Long-form proposal sections; critic checks claims against the fact sheet | **Yes, later**, as an MAF workflow with a human checkpoint |
| **RFP analyser** (Opportunity #2) | Splits an RFP into requirements, maps each via the engine, drafts responses | **Yes**: an agent loop over the deterministic engine as a tool |

## 7. Explainability: what a user sees for every recommendation

1. Requirements table: ID, statement (quoted source sentence), capability code, priority, **Stated vs Inferred**.
2. For each option: per-requirement coverage, which product covers it, coverage level, **evidence grade**, citation ids.
3. **Gaps** (uncovered requirements), never hidden.
4. **Avantra rules applied**, labelled *not vendor facts*, with adjustment and rationale, flagged "awaiting business confirmation".
5. Licensing: disclosure status, sizing inputs from the customer's own numbers, explicit vendor-confirmation statement.
6. Assumptions and warnings (data quality, inferred requirements, unmapped statements, security).
7. Sources with trust level and the demonstration-data flag.
8. Summary source: `ai-grounded` or `deterministic-template`.
