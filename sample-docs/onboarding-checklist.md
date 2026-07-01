---
title: New Employee Onboarding Checklist
tags: [onboarding, hr, it-setup, accounts]
owner: HR & IT
last_verified: 2025-04-10
---

# New Employee Onboarding Checklist

This document lists the steps a new employee should complete during their first week, and the steps the IT and HR teams need to complete before the employee starts.

## Before Day 1 (IT & HR Actions)

- [ ] Create Active Directory account (IT — requires signed offer letter from HR)
- [ ] Add employee to relevant Azure AD groups based on department
- [ ] Provision laptop per the hardware standard for the employee's role
- [ ] Enrol laptop in Intune MDM and apply the relevant policy group
- [ ] Create GitLab account and add to department project groups
- [ ] Create email account and assign Microsoft 365 licence
- [ ] Add employee to the company-wide Slack workspace and department channels
- [ ] Set up hardware MFA token or send Microsoft Authenticator setup instructions
- [ ] Assign a desk and request access card from facilities

## Day 1 — New Employee Actions

### Account Setup

1. Log in to your laptop with the temporary password sent to your personal email. You will be forced to change it immediately.
2. Password requirements: minimum 16 characters, at least one uppercase, lowercase, digit, and special character. Passphrases are recommended.
3. Set up Microsoft Authenticator for MFA. Follow the link in the setup email from IT.
4. Log in to Outlook / Outlook Web App and verify you can send and receive email.
5. Log in to Slack and join your department channel. Post a brief introduction.

### VPN Access

If you will be working remotely at any point, install the GlobalProtect VPN client on Day 1 even if you are currently on-premises. See the [VPN Access Guide](vpn-access.md) for installation instructions.

### Development Environment (Developers Only)

1. Clone the engineering bootstrap repository: `git clone https://gitlab.internal/platform/dev-bootstrap.git`
2. Run the setup script: `./setup.sh`. This installs the required toolchain, configures Git with your company email, and sets up the SSH key for GitLab.
3. Request access to the repositories you need via a Helpdesk ticket or ask your team lead to add you in GitLab.

## Week 1 — Mandatory Training

All employees must complete the following courses in the Learning Management System (LMS) at `https://lms.internal` within the first week:

| Course | Duration | Due |
|--------|----------|-----|
| Information Security Awareness | 45 min | Day 3 |
| Code of Conduct | 20 min | Day 3 |
| Data Protection & GDPR | 30 min | Day 5 |
| Incident Response Basics | 15 min | Day 5 |

Completion is tracked automatically. Incomplete courses trigger a reminder from HR after 7 days.

## Expense Reimbursement

Submit expenses via the Concur portal at `https://concur.internal`. Receipts must be attached. The company credit card is provided for travel expenses over €100 — request it through HR after your first month. Standard reimbursement cut-off is the 15th of each month (paid with the following month's salary).

## Getting Help

- **IT Helpdesk** — Slack `#it-helpdesk` or ticket via `https://helpdesk.internal`
- **HR queries** — Contact your HR business partner (name in your welcome email)
- **Facilities** — `#facilities` Slack channel for building access, desk issues, etc.

Your buddy (assigned by HR) is your first point of contact for day-to-day questions that don't require a formal ticket.
