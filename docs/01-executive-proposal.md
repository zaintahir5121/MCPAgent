# 01 — Executive Proposal: Avantra AI Advisory Platform

> Deliverables covered: (1) executive business proposal, (2) product vision and strategy, (3) problem statement and business value, (21) business opportunity and prioritisation matrix. This document also contains **Step 1 — Challenge and Analyse** and **Step 2 — Recommend**.

---

## 1. Executive summary

Avantra should build an **AI Advisory Platform**: a governed, multi-vendor decision-support service that turns a customer's requirements into an **explainable, evidence-backed solution recommendation** (products, bundle, licensing considerations, gaps, risks and implementation considerations) and a **shareable advisory report**.

It is **not a chatbot**. Nexa, the Avantra Marketplace, a sales copilot and partner portals are all *channels* that consume the same advisory capabilities through two standard interfaces: a **REST API** (system integration) and an **MCP server** (AI assistants such as Nexa).

The core idea is to separate the two kinds of reasoning:

| Concern | Who does it | Why |
|---|---|---|
| Understanding messy natural language, writing prose | LLM (bounded, validated) | That is what LLMs are good at |
| Deciding *which products cover which requirements*, scoring, licensing logic | Deterministic engine over a curated catalogue | Repeatable, auditable, explainable, cannot hallucinate a product or a price |
| Proving a claim | Citations to versioned sources with trust levels | Customer trust, legal defensibility |
| Approving customer-facing output | A human reviewer | Accountability for commercial advice |

The MVP in this repository implements this end to end for **Planview** on .NET 10, Microsoft.Extensions.AI, Microsoft Agent Framework, the MCP C# SDK, EF Core and an Azure AI Search-compatible hybrid index. It runs with **no AI key** in deterministic mode, and switches to AI-assisted mode when you provide your OpenAI-compatible endpoint.

---

## 2. Step 1 — Challenge and analyse the vision

You asked me to challenge the idea rather than agree with it. These are the points that most affect whether this succeeds.

### 2.1 Assumptions that do not hold, or need confirming

| # | Assumption in the brief | Challenge | Consequence for the design |
|---|---|---|---|
| A1 | "Public documentation will let the agent recommend subscriptions, plans and licensing tiers." | For Planview (and largely for Salesforce enterprise SKUs and OutSystems), **pricing, editions and licence-type definitions are mostly not public**. Planview sells by quote. A public-only knowledge base can explain *capabilities* well, but **cannot** answer "which tier and how much". | The licensing module is designed around **disclosure status** (`NotPublic`, `PartnerConfidential`, `PubliclyDocumented`). It outputs *sizing inputs and questions for the quote*, never prices. The real commercial moat is **Avantra's partner data** (price books, packaging guides, deal history). Using it needs a legal check of each partner agreement (see A2). |
| A2 | "We can ingest vendor documentation into an AI system." | Partner portals, price books and enablement material are usually covered by **partner agreements and NDAs**. Some terms restrict redistribution or use in automated systems. Web-scraping public sites may breach the site's terms. | Every source carries a **trust level and licence/usage note**. Partner-confidential content is filtered by audience (internal vs customer-facing). **Action:** Legal and Alliances confirm usage rights per vendor before Phase 2. |
| A3 | "Customers will use the advisor directly." | Customer-facing licensing advice creates **commercial and legal exposure** (mis-sold subscriptions, implied commitments). It also risks channel conflict with the vendor's own sales team. | Launch **internal-first** (pre-sales copilot), with a **human approval gate** before any report goes to a customer. Customer self-service comes later, with a narrower, approved surface. |
| A4 | "Nexa → MCP → Agent is the architecture." | MCP is a good *adapter* for AI clients, but it is not a system of record or a business API. If Nexa is not LLM-driven (for example a scripted bot), MCP adds nothing. Other channels (Marketplace UI, CRM, portals) need REST. | **REST API is the primary contract**. MCP is a thin adapter over the same application services. No business logic lives in MCP tools. |
| A5 | "An autonomous agent / multi-agent system will reason about requirements." | Autonomous agents are non-deterministic, hard to test and hard to audit. In licensing advice those weaknesses are liabilities. | A **deterministic orchestrated pipeline** with LLM steps at defined points. An agent (Microsoft Agent Framework) is used **only** for open Q&A, and it has no tool that can produce recommendations or prices. |
| A6 | "The knowledge base is the product." | Retrieval over vendor docs is commoditising: vendors ship their own copilots, and Planview Copilot already exists. A RAG bot over public docs is not a defensible product. | The defensible assets are the **curated capability model and catalogue**, **Avantra's implementation experience** (fit rules, implementation considerations, effort data), **cross-vendor neutrality**, and the **workflow into the sales process**. |
| A7 | Product names and packaging are stable. | Vendors rebrand and repackage often (e.g. Clarizen → AdaptiveWork, LeanKit → AgilePlace, Tasktop → Hub/Viz). | Aliases, catalogue versioning (`packVersion`), `verifiedOn` dates, a freshness SLA and deprecation states are built into the model. |

### 2.2 Missing information (needs business confirmation)

1. **Nexa's technology**: is it LLM-based, and does it support MCP client connections (Streamable HTTP and OAuth)? Which identity does it run under? *(Determines the integration pattern in doc 05.)*
2. **Target users for v1**: internal sales/pre-sales only, or also customers? *(Determines the approval gate, data exposure and tone.)*
3. **Partner data rights** for Planview, Salesforce and OutSystems material (price books, packaging, roadmap decks).
4. **Owner of the catalogue**: who curates and verifies product/capability mappings? This needs a named **Knowledge Curator** role per vendor, roughly 0.2–0.5 FTE.
5. **Success metrics** and a baseline: current time to first proposal, win rate, proposal rework.
6. **CRM of record**: Salesforce or Dynamics, for opportunity linkage and pipeline intelligence.
7. **Data residency** requirements of customers (EU, UAE, …) that affect Azure region choice.
8. **Azure tenancy and landing zone**: existing subscriptions, networking standards, approved model deployments.
9. **Budget for model consumption**, and whether Azure OpenAI (data stays in tenant) is mandatory or your current OpenAI-compatible endpoint is acceptable for customer data.

### 2.3 Key risks

| Risk | Likelihood | Impact | Mitigation built into the design |
|---|---|---|---|
| Hallucinated capability, price or tier reaches a customer | High without controls | Severe | Catalogue-as-truth; deterministic engine; price/unknown-product output guard; evidence grading; human approval gate |
| Stale knowledge after a vendor repackages | High | High | `packVersion`, `verifiedOn`, freshness SLA, document versioning and supersession, curator workflow |
| Prompt injection / document poisoning via uploaded files | Medium | High | Quarantine on ingest, trust levels, review-before-publish, untrusted-content delimiters, tools are read-only |
| Partner-confidential data leaks to customers | Medium | Severe | Disclosure status and audience filtering, tenant isolation, RBAC, audit |
| Low adoption by sales | Medium | High | Embed in existing tools (Nexa, CRM, Teams) instead of a new destination; fast time-to-value; report output they would write anyway |
| Vendor copilots commoditise single-vendor Q&A | High | Medium | Differentiate on cross-vendor neutrality, Avantra experience data and the sales workflow |
| Over-engineering (microservices, multi-agent) delays value | Medium | High | Modular monolith with clear seams; split only on evidence |

---

## 3. Problem statement

Avantra's sales and pre-sales teams answer the same questions repeatedly — *"Which Planview product do we need? Do we need AgilePlace as well? How is it licensed? What will the implementation involve?"* — using knowledge that sits with a few senior consultants, in slide decks and in partner portals. The result:

- **Slow, inconsistent responses**: days to produce a first recommendation, and quality depends on who answers.
- **Expert bottleneck**: senior architects spend time on repeatable qualification instead of complex deals.
- **Risky licensing conversations**: over- or under-scoped proposals, and occasionally commitments the vendor will not honour.
- **Lost institutional knowledge**: implementation lessons are not reused in pre-sales.
- **Marketplace is a catalogue, not an advisor**: customers browse products but get no guidance on fit.

## 4. Target users and value proposition

| User | Job to be done | Value |
|---|---|---|
| **Account executives / sales** | Qualify fast, propose credibly | First recommendation in minutes, with the right clarification questions |
| **Pre-sales & solution architects** | Produce solution designs and proposals | Draft solution option, coverage matrix and report to refine instead of writing from scratch |
| **Customers (phase 2+)** | Understand options before talking to sales | Self-service advisory on the Marketplace, routed to sales as a qualified lead |
| **Delivery / PMO leads** | Scope implementations realistically | Implementation considerations and risks from Avantra experience |
| **Alliance managers** | Understand demand per vendor/product | Aggregated, anonymised demand and gap analytics |

**Value proposition (customer-facing):** *"Vendor-neutral, evidence-backed technology advice from the people who implement it — in minutes, with every claim sourced."*

## 5. Product direction — chatbot vs agent vs MCP vs advisory platform

| Option | What it is | Business value | Defensibility | Risk | Verdict |
|---|---|---|---|---|---|
| **Chatbot over docs (RAG)** | Q&A on vendor documentation | Low–medium: answers questions, does not produce decisions | Very low; vendors ship this themselves | Hallucination; no workflow | Not on its own |
| **Autonomous AI agent** | LLM plans and calls tools freely | Medium | Low | Non-deterministic; hard to audit; licensing liability | Only for bounded Q&A |
| **MCP server only** | Expose tools to Nexa | Depends on what sits behind it | None by itself — it is a protocol | Logic leaks into the client | As an adapter, yes |
| **AI Advisory Platform** *(recommended)* | Governed advisory services (catalogue, rules, evidence, reports) exposed via API and MCP to all channels | **High**: shortens the sales cycle, scales experts, differentiates the Marketplace | **High**: curated multi-vendor model plus Avantra experience plus workflow | Needs a knowledge curation discipline | **Build this** |

### Recommended direction

Build the **Avantra AI Advisory Platform** as a product with three layers:

1. **Knowledge layer**: per-vendor *knowledge packs* (capability model, catalogue, licensing disclosure, fit rules, clarification rules, source documents) on a shared ingestion, search and provenance platform.
2. **Advisory layer**: a deterministic recommendation and evidence pipeline, with AI for extraction and narrative only; human review; reports.
3. **Channel layer**: REST API and MCP server, consumed by Nexa, the Marketplace UI, a Teams/CRM pre-sales copilot and partner portals.

Sequence: **internal pre-sales copilot (Planview) → Nexa via MCP → Marketplace self-service → Salesforce and OutSystems packs → cross-vendor advisory and proposal generation.**

## 6. How it differentiates Avantra Marketplace

1. **From catalogue to advisor**: every Marketplace listing gains "Is this right for me?", backed by the same engine sales uses.
2. **Vendor-neutral, cross-platform advice**: Planview + Salesforce + OutSystems in one assessment (e.g. "PPM in Planview, CRM in Salesforce, custom portal in OutSystems — and how they integrate"). Single-vendor copilots cannot do this.
3. **Implementation-informed**: recommendations carry Avantra's delivery experience (risks, phasing, effort), not only vendor marketing.
4. **Trustworthy by design**: citations, evidence grades, explicit unknowns and reviewer approval. That is a selling point to enterprise buyers.

## 7. Commercial opportunities

| Model | Description |
|---|---|
| **Sales acceleration (internal)** | Shorter cycles, more qualified opportunities per pre-sales FTE (primary ROI) |
| **Lead generation** | Marketplace self-assessment → qualified, pre-scoped leads into CRM |
| **Paid advisory products** | "Planview Fit Assessment", "Licence Optimisation Review" packaged as fixed-price services, accelerated by the platform |
| **Customer success / renewals** | Adoption and licence-utilisation advisory for existing customers (expansion and churn prevention) |
| **Partner / white-label** | Offer the advisory engine to vendor partners or regional resellers (longer term, after the IP is proven) |

**KPIs**: time to first recommendation (target < 15 min, from days); pre-sales hours per opportunity (−30–40%); proposal rework rate; win rate on advisory-assisted deals; % of recommendations approved without major change (quality proxy; target > 80%); catalogue freshness (100% of products verified within 90 days).

> These targets are hypotheses to validate in the Phase 3 pilot. Baselines must be measured first.

---

## 8. Additional AI business opportunities — prioritisation matrix

Scored 1–5 (5 = best). *Effort* is inverted (5 = least effort). **Priority = Impact × 0.4 + Differentiation × 0.3 + Effort × 0.3.**

| # | Opportunity | Business problem | Target users | Required data | Tech complexity | Impact | Effort (inv.) | Differentiation | **Priority** |
|---|---|---|---|---|---|---|---|---|---|
| 1 | **AI Pre-sales Copilot** (this MVP) | Slow, expert-dependent qualification | Sales, pre-sales | Vendor packs, Avantra rules | Medium | 5 | 4 | 4 | **4.4** |
| 2 | **AI RFP & Requirements Analyser** | RFPs take days to parse and map | Pre-sales, bid team | RFP docs, capability models | Medium (doc extraction + mapping reuses engine) | 5 | 4 | 4 | **4.4** |
| 3 | **AI Proposal Generator** | Proposals rewritten from scratch | Pre-sales | Advisory results, templates, past proposals | Medium | 5 | 3 | 4 | **4.1** |
| 4 | **AI Implementation Estimator** | Inaccurate scoping, margin erosion | Delivery, pre-sales | **Historical project actuals** (effort by module/integration) | High (needs clean data) | 5 | 2 | 5 | **4.1** |
| 5 | **AI Solution Comparison Engine (cross-vendor)** | Customers compare vendors with no neutral advice | Customers, sales | Multiple packs, a common capability ontology | Medium–High | 4 | 3 | 5 | **4.0** |
| 6 | **AI Marketplace Recommendation Engine** | Marketplace browsing does not convert | Customers | Packs, behaviour data, CRM outcomes | Medium | 4 | 3 | 4 | **3.7** |
| 7 | **AI Customer Success & Adoption Advisor** | Low adoption leads to churn and smaller renewals | CSMs, customers | Usage telemetry (vendor APIs), licence data | High (data access per vendor) | 4 | 2 | 4 | **3.4** |
| 8 | **AI Licensing & Subscription Advisor (partner data)** | Over/under-licensing | Sales, customers | Partner price books (**legal check**) | Medium (data rights are the blocker) | 5 | 2 | 3 | **3.5** |
| 9 | **AI Integration Architecture Advisor** | Integrations are underestimated | Architects | Integration patterns, Avantra reference architectures | Medium | 4 | 3 | 4 | **3.7** |
| 10 | **AI Modernisation Assessment** (e.g. legacy → OutSystems) | Unclear modernisation business case | CIOs, architects | App inventories, assessment frameworks | High | 4 | 2 | 4 | **3.4** |
| 11 | **AI Customer Maturity Assessment** | No structured view of customer maturity | Sales, CSMs | Maturity models (PPM, CRM, low-code) | Low–Medium | 3 | 4 | 3 | **3.3** |
| 12 | **AI Sales Opportunity Intelligence** | Pipeline lacks signal | Sales leadership | CRM data, advisory sessions (anonymised) | Medium | 4 | 3 | 3 | **3.4** |
| 13 | **AI Implementation Knowledge Assistant** | Delivery lessons not reused | Consultants | Project documentation, retros, runbooks | Low–Medium (reuses KB) | 3 | 4 | 3 | **3.3** |
| 14 | **AI Technical Documentation Assistant** | Writing design docs is slow | Consultants | Templates, configuration exports | Low | 2 | 4 | 2 | **2.6** |
| 15 | **AI Technology Selection Advisor (open-ended)** | "Which platform should we use at all?" | Customers, CIOs | Broad multi-vendor ontology | High | 4 | 1 | 5 | **3.4** |

**Recommended sequence:** (1) Pre-sales Copilot → (2) RFP Analyser → (3) Proposal Generator. All three reuse the same engine and knowledge packs, so each step adds value at low marginal cost. Start collecting **implementation actuals now** so the Estimator (4) becomes possible within about 6–9 months; it is the most defensible asset Avantra can build, because no vendor or competitor has your delivery data.

---

## 9. Investment shape (indicative, to be validated)

| Phase | Duration | Team |
|---|---|---|
| 0–2: Foundation + knowledge platform (Planview verified) | 6–8 weeks | 1 architect/lead dev, 1 .NET dev, 0.5 Planview curator, 0.2 security |
| 3–4: Advisory hardening, Nexa via MCP, pilot | 6 weeks | + 0.5 QA, sales pilot group (5–10 users) |
| 5: Salesforce + OutSystems packs | 4–6 weeks per vendor (mostly curation) | curator per vendor + 1 dev |

Run cost is dominated by Azure AI Search (Basic/S1), Azure SQL (serverless/GP), Container Apps and model tokens. A single advisory run costs a few thousand tokens across two LLM calls; the deterministic core costs nothing per call.

> Costs and durations above are planning estimates, not quotes. They need confirmation against Avantra's rates and Azure pricing in your region.
