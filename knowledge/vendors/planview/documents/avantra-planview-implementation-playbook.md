---
title: Avantra Planview implementation playbook (internal experience)
source_url:
trust: AvantraInternal
products: planview-portfolios, planview-adaptivework, planview-agileplace, planview-hub
demonstration_data: true
tags: implementation, playbook
---

# Avantra Planview implementation playbook (internal experience)

## Phasing

Enterprise portfolio programmes are delivered in phases: (1) portfolio and demand management with governance stage-gates, (2) resource and capacity planning, (3) financial planning and actuals integration, (4) agile delivery connection and value stream metrics.

## Common risks

- Poor resource master data (roles, skills, availability) undermines capacity planning.
- Unclear system of record between agile tools (Jira, Azure DevOps) and the portfolio tool causes duplicate data entry.
- Financial planning scope creep when ERP integration is not agreed early.
- Too many custom fields and workflows reduce adoption; enforce configuration governance.

## Integration patterns

Use Planview Hub (or the Planview integration services agreed with the customer) to synchronise epics and features between agile tools and portfolio planning. Use the customer's BI platform (for example Power BI) for cross-system executive reporting where required.
