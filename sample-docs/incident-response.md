---
title: Incident Response Procedure
tags: [incident, security, oncall, operations]
owner: Platform Operations
last_verified: 2025-03-20
---

# Incident Response Procedure

This document describes the process for detecting, responding to, and resolving production incidents and security events.

## Severity Levels

| Severity | Description | Initial Response |
|----------|-------------|-----------------|
| **P1 — Critical** | Production outage; data loss or breach in progress; all users impacted | 15 minutes |
| **P2 — High** | Major feature unavailable; significant user impact; potential data integrity issue | 30 minutes |
| **P3 — Medium** | Degraded performance; partial outage; non-critical feature unavailable | 2 hours |
| **P4 — Low** | Minor issue; cosmetic bug; single user affected | Next business day |

## Declaring an Incident

Any engineer who suspects a P1 or P2 condition should immediately:

1. Post in `#incidents` on Slack with the format: `@oncall [P1/P2] <one-line description>`
2. This automatically pages the on-call engineer via PagerDuty.
3. The on-call engineer creates the incident channel: `#inc-YYYYMMDD-short-description`
4. All incident communication happens in that channel from this point.

For P3/P4 issues, create a Jira ticket in the **OPS** project (no need to page on-call).

## Incident Roles

### Incident Commander (IC)

The first on-call engineer to respond is the IC until explicitly handed off.

**Responsibilities:**
- Coordinates all responders; keeps the channel signal-to-noise ratio high.
- Provides regular status updates every 15 minutes for P1, 30 minutes for P2.
- Makes the call to escalate or de-escalate severity.
- Declares the incident resolved and initiates the post-mortem.

### Technical Lead

Usually a subject-matter expert pulled in by the IC.

**Responsibilities:**
- Leads investigation and mitigation.
- Proposes and executes fixes; documents every action taken with timestamps.

### Comms Lead

Required for P1 incidents affecting external customers.

**Responsibilities:**
- Updates the public status page (`https://status.companyname.com`) every 30 minutes.
- Drafts customer communications and runs them by legal/management before sending.
- Notifies account managers for enterprise customers if SLA breach is likely.

## Investigation Checklist

When joining an incident, start with:

1. **What changed?** — Check recent deployments (`#deployments` channel, Argo CD, or Kubernetes rollout history).
2. **What does the monitoring say?** — Check the primary Grafana dashboard for the affected service.
3. **Are there error spikes?** — Check Sentry or the ELK stack for recent error clusters.
4. **Which component is failing?** — Narrow down via health endpoints and dependency maps.

Hypothesis-driven debugging: form a specific hypothesis, test it, discard or confirm, repeat.

## Mitigation Actions

Try the least-disruptive fix first:

1. **Rollback** — Roll back the last deployment if it caused the issue. This is always the first choice for a post-deployment incident.
2. **Scale out** — If the issue is resource exhaustion, scale the affected service horizontally.
3. **Feature flag** — Disable the failing feature via the feature flag system if available.
4. **Restart** — As a last resort, restart the affected pods/services. Document why this was necessary.

For data-loss scenarios, do not attempt writes until the root cause is understood — you may worsen the situation.

## Security Incidents

If the incident involves a suspected breach, data leak, or active attack:

1. Do not announce details publicly or in non-incident channels.
2. Immediately loop in the CISO or Security Lead (their PagerDuty on-call is always active).
3. Preserve logs — do not restart or terminate compromised instances until forensics are completed or explicitly approved.
4. Consult legal before any external notification (GDPR breach notification has a 72-hour window).

## Declaring Resolution

The IC declares the incident resolved when:
- The service is restored to normal operation and confirmed by monitoring.
- The root cause is understood at a high level.
- No further immediate risk exists.

Post in the incident channel: `@channel RESOLVED — [brief description of fix]`

Update the status page to **Operational** and close the PagerDuty incident.

## Post-Mortem

A post-mortem is required for all P1 and P2 incidents. A blameless post-mortem document must be filed in Confluence under **Platform > Post-Mortems** within 5 business days.

**Required sections:**
- Timeline (what happened, when, and what actions were taken)
- Root cause
- Contributing factors
- Impact (duration, number of users affected, SLA impact)
- Action items with owners and due dates

Action items must be tracked in Jira. The team lead reviews completion status at the monthly operations review.
