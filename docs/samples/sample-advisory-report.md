# Planview Solution Advisory Report - Contoso

*Session:* `851078ef-5a55-4473-bbaf-bb8b22a77c29` · *Generated:* 2026-11-25 02:58 UTC · *Status:* **Draft**

> **DRAFT - not yet reviewed by an Avantra solution consultant. Not for external distribution.**

## 1. Executive summary

Based on 8 identified requirement(s) (5 must-have), the recommended Planview solution is Planview Portfolios + Planview AgilePlace + Planview Hub, covering 100% of must-have and 97% of weighted requirements. 2 alternative option(s) are provided for comparison. Licensing, editions and pricing are not publicly confirmed and must be validated with Planview or an authorised partner. 1 clarification question(s) remain open and may change the recommendation.

<sub>Summary source: deterministic-template</sub>

## 2. Customer context

| Attribute | Value |
|---|---|
| Employees | 2000 |
| Project managers | 150 |
| Portfolio managers | 40 |
| Resource managers | 25 |
| Team members | 600 |
| Business units | 6 |
| Delivery methodology | hybrid |
| Deployment preference | saas |
| Existing systems | Jira |

## 3. Requirements

| ID | Requirement | Capability | Priority | Origin |
|---|---|---|---|---|
| R1 | Portfolio management (from: "We need enterprise portfolio management, strategic planning, resource management, financial management, and project governance.") | `portfolio-management` | Must | Stated |
| R2 | Strategic planning (from: "We need enterprise portfolio management, strategic planning, resource management, financial management, and project governance.") | `strategic-planning` | Must | Stated |
| R3 | Resource management (from: "We need enterprise portfolio management, strategic planning, resource management, financial management, and project governance.") | `resource-management` | Must | Stated |
| R4 | Financial management (from: "We need enterprise portfolio management, strategic planning, resource management, financial management, and project governance.") | `financial-management` | Must | Stated |
| R5 | Project governance (from: "We need enterprise portfolio management, strategic planning, resource management, financial management, and project governance.") | `project-governance` | Must | Stated |
| R6 | Agile delivery mentioned - agile planning capability inferred. | `agile-planning` | Should | Inferred |
| R7 | Existing delivery tools (Jira) must stay in sync with planning - integration inferred. | `toolchain-integration` | Should | Inferred |
| R8 | Resource management for many project managers usually requires time/actuals capture. | `time-tracking` | Should | Inferred |

<sub>Analyser: heuristic</sub>

## 4. Recommended solution

**Planview Portfolios + Planview AgilePlace + Planview Hub** - must-have coverage 100%, weighted coverage 97%, score 0.971.

| Req | Covered by | Coverage | Evidence | Sources |
|---|---|---|---|---|
| R1 | Planview Portfolios | Core | Document-supported | [C1] [C2] |
| R2 | Planview Portfolios | Core | Document-supported | [C1] [C3] |
| R3 | Planview Portfolios | Core | Document-supported | [C1] [C4] |
| R4 | Planview Portfolios | Core | Document-supported | [C1] [C3] |
| R5 | Planview Portfolios | Core | Document-supported | [C1] [C3] |
| R6 | Planview AgilePlace | Core | Document-supported | [C5] [C6] |
| R7 | Planview Hub | Core | Document-supported | [C7] [C8] |
| R8 | Planview Portfolios | Supported | Document-supported | [C1] [C9] |

**Avantra advisory rules applied** (internal heuristics, not vendor facts):

- Agile delivery + portfolio governance: connect Planview Portfolios with Planview AgilePlace (+0.06) - Avantra delivery experience: hybrid organisations need portfolio governance and team-level agile planning connected. *(awaiting business confirmation)*
- Enterprise scale (1,000+ employees) favours an enterprise SPM platform (+0.02) - Avantra heuristic: multi-business-unit governance and financial planning at enterprise scale. *(awaiting business confirmation)*

## 5. Alternatives compared

| Option | Products | Must-have | Weighted | Score | Gaps |
|---|---|---|---|---|---|
| Recommended | Planview Portfolios + Planview AgilePlace + Planview Hub | 100% | 97% | 0.971 | - |
| Alternative 1 | Planview Portfolios + Planview AgilePlace | 100% | 91% | 0.954 | - |
| Alternative 2 | Planview Portfolios + Planview Hub | 100% | 91% | 0.894 | - |

## 6. Licensing & subscription considerations

> No prices, tiers or discounts are stated in this report unless they are publicly documented and cited.

### Planview Portfolios

Planview does not publicly document complete licensing terms for Planview Portfolios in the sources held. Treat the options below as orientation only and confirm licence types, editions, quantities and pricing with Planview or an authorised partner. [C10]

- **Planview Portfolios subscription** - SaaS subscription. Licence types, editions/packages, minimums and pricing are not held in the knowledge base and must be confirmed with Planview or an authorised partner. *(metric: Not publicly documented - confirm with Planview; disclosure: NotPublic)*

| Sizing input | Count | Basis |
|---|---|---|
| Portfolio managers | 40 | Stated by customer - a sizing input, not a licence quantity. |
| Project managers | 150 | Stated by customer - a sizing input, not a licence quantity. |
| Resource managers | 25 | Stated by customer - a sizing input, not a licence quantity. |
| Team members / contributors | 600 | Stated by customer - a sizing input, not a licence quantity. |
| Executives / stakeholders | ? | Unknown - required to size the subscription. |
| Total employees (upper bound for occasional/read-only users) | 2000 | Stated by customer - a sizing input, not a licence quantity. |

### Planview AgilePlace

Planview does not publicly document complete licensing terms for Planview AgilePlace in the sources held. Treat the options below as orientation only and confirm licence types, editions, quantities and pricing with Planview or an authorised partner. [C10]

- **Planview AgilePlace subscription** - SaaS subscription. Licence types, editions/packages, minimums and pricing are not held in the knowledge base and must be confirmed with Planview or an authorised partner. *(metric: Not publicly documented - confirm with Planview; disclosure: NotPublic)*

| Sizing input | Count | Basis |
|---|---|---|
| Team members / contributors | 600 | Stated by customer - a sizing input, not a licence quantity. |

### Planview Hub

Planview does not publicly document complete licensing terms for Planview Hub in the sources held. Treat the options below as orientation only and confirm licence types, editions, quantities and pricing with Planview or an authorised partner. [C10]

- **Planview Hub subscription** - SaaS subscription. Licence types, editions/packages, minimums and pricing are not held in the knowledge base and must be confirmed with Planview or an authorised partner. *(metric: Not publicly documented - confirm with Planview; disclosure: NotPublic)*

| Sizing input | Count | Basis |
|---|---|---|
| Team members / contributors | 600 | Stated by customer - a sizing input, not a licence quantity. |

## 7. Gaps, risks and implementation considerations

- *data-quality:* DEMONSTRATION DATA: the Planview catalogue in this MVP is an Avantra summary of publicly available positioning and is pending curator verification against official Planview sources. It contains no pricing. Product capabilities, packaging and licensing must be confirmed with Planview or an authorised partner before customer use.
- *avantra-rule:* Avantra advisory heuristic applied and awaiting business confirmation: Agile delivery + portfolio governance: connect Planview Portfolios with Planview AgilePlace
- *avantra-rule:* Avantra advisory heuristic applied and awaiting business confirmation: Enterprise scale (1,000+ employees) favours an enterprise SPM platform
- Planview Portfolios: Agree the portfolio hierarchy, lifecycle stage-gates and financial planning model before configuration.
- Planview Portfolios: Resource master data quality (roles, skills, availability) is the main driver of capacity-planning value.
- Planview Portfolios: Plan ERP/finance integration for actuals early if financial management is in scope.
- Planview Portfolios: Typical enterprise roll-outs are phased (PMO/portfolio first, then resource and financial management).
- Planview AgilePlace: Define board standards (lanes, card types, WIP limits) centrally to keep teams-of-teams reporting consistent.
- Planview AgilePlace: Decide the system of record per team (AgilePlace vs. Jira/Azure DevOps) before integrating.
- Planview Hub: Model artifact mappings (e.g. epic/feature/story) and conflict rules per integration before go-live.
- Planview Hub: On-premises deployment availability to be confirmed with Planview for the customer's scenario.
- Multi-product solution (3 products): plan integration, data model alignment and a single reporting layer early.

## 8. Assumptions

- Recommendations use Planview knowledge pack version 0.1.0-demo.
- R6 was inferred, not stated: Agile delivery mentioned - agile planning capability inferred.
- R7 was inferred, not stated: Existing delivery tools (Jira) must stay in sync with planning - integration inferred.
- R8 was inferred, not stated: Resource management for many project managers usually requires time/actuals capture.

## 9. Open clarification questions

- **In which regions/countries will users and data be located?** - _Data residency and language requirements._

## Sources

- **[C1]** Planview Portfolios - product overview (Avantra summary) - Capabilities <https://www.planview.com/> · trust: AvantraInternal · *demonstration data*
- **[C2]** Planview solution areas and product fit (Avantra summary) - Strategic portfolio management <https://www.planview.com/> · trust: AvantraInternal · *demonstration data*
- **[C3]** Planview Portfolios - product overview (Avantra summary) - Capabilities <https://www.planview.com/> · trust: AvantraInternal · *demonstration data*
- **[C4]** Planview Portfolios - product overview (Avantra summary) - Implementation considerations <https://www.planview.com/> · trust: AvantraInternal · *demonstration data*
- **[C5]** Planview AgilePlace - product overview (Avantra summary) - Capabilities <https://www.planview.com/> · trust: AvantraInternal · *demonstration data*
- **[C6]** Planview AgilePlace - product overview (Avantra summary) - Capabilities <https://www.planview.com/> · trust: AvantraInternal · *demonstration data*
- **[C7]** Planview Hub - product overview (Avantra summary) - Capabilities <https://www.planview.com/> · trust: AvantraInternal · *demonstration data*
- **[C8]** Planview Hub - product overview (Avantra summary) - Capabilities <https://www.planview.com/> · trust: AvantraInternal · *demonstration data*
- **[C9]** Planview solution areas and product fit (Avantra summary) - Project and work management <https://www.planview.com/> · trust: AvantraInternal · *demonstration data*
- **[C10]** Planview licensing & subscription - what is and is not public (Avantra summary) - Licensing <https://www.planview.com/> · trust: AvantraInternal · *demonstration data*

## Evidence legend & disclaimer

- **Verified** - curator-verified against an official vendor source.
- **Document-supported** - backed by a retrieved knowledge passage, pending curator verification.
- **Catalogue (unverified)** - catalogue entry with source reference, pending curator verification.

_DEMONSTRATION DATA: the Planview catalogue in this MVP is an Avantra summary of publicly available positioning and is pending curator verification against official Planview sources. It contains no pricing. Product capabilities, packaging and licensing must be confirmed with Planview or an authorised partner before customer use._
