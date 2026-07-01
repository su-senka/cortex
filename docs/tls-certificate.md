---
title: Generating and Renewing TLS Certificates
tags: [tls, ssl, certificates, security, openssl]
owner: Platform Security
last_verified: 2025-05-15
---

# Generating and Renewing TLS Certificates

This document explains how to request, generate, and renew TLS/SSL certificates for internal and public-facing services. The company uses an internal Certificate Authority (CA) for intranet services and Let's Encrypt for public-facing services.

## Internal CA Certificates (Intranet Services)

Use internal CA certificates for anything on `*.internal`, VPN endpoints, and inter-service mTLS.

### Generating a CSR

1. Generate a private key. Use at least 4096-bit RSA or an ECDSA P-256 key:

```bash
# RSA 4096-bit
openssl genrsa -out service.key 4096

# Or ECDSA P-256 (preferred — smaller, faster)
openssl ecparam -name prime256v1 -genkey -noout -out service.key
```

2. Create a Certificate Signing Request (CSR). Fill in the prompts or use a config file:

```bash
openssl req -new -key service.key -out service.csr \
  -subj "/C=DE/ST=Bavaria/L=Munich/O=CompanyName/CN=myservice.internal"
```

To include Subject Alternative Names (SANs) — required if the service has multiple hostnames:

```bash
openssl req -new -key service.key -out service.csr \
  -subj "/CN=myservice.internal" \
  -addext "subjectAltName=DNS:myservice.internal,DNS:myservice-alt.internal"
```

3. Submit the CSR to the internal CA. Open a Helpdesk ticket with category **"Certificate Request"** and attach `service.csr`. The CA team will issue the certificate within one business day. You will receive:
   - `service.crt` — the signed certificate
   - `ca-chain.pem` — the intermediate and root CA chain

### Installing the Certificate

Combine the certificate and chain for deployment:

```bash
cat service.crt ca-chain.pem > service-fullchain.pem
```

Configure your application or web server to use `service-fullchain.pem` and `service.key`.

**Nginx example:**
```nginx
ssl_certificate     /etc/ssl/service-fullchain.pem;
ssl_certificate_key /etc/ssl/service.key;
```

**IIS:** Import `service.crt` and `service.key` into a PFX first:

```bash
openssl pkcs12 -export -out service.pfx \
  -inkey service.key -in service.crt -certfile ca-chain.pem
```

Then import the PFX via the IIS Manager → Server Certificates → Import.

### Certificate Validity and Renewal

Internal CA certificates are issued with a **one-year** validity. You will receive an email from the CA team 30 days before expiry. To renew:

1. Generate a new private key and CSR (do not reuse the old key).
2. Submit the CSR via a new Helpdesk ticket.

## Let's Encrypt Certificates (Public Services)

Public-facing services should use Let's Encrypt via Certbot for automatic renewal. Manual issuance is not recommended.

```bash
# Install Certbot
sudo apt install certbot python3-certbot-nginx

# Issue and install certificate
sudo certbot --nginx -d public-service.companyname.com
```

Certbot installs a systemd timer that renews certificates automatically when they are within 30 days of expiry.

## Checking Certificate Expiry

To quickly check when a certificate expires:

```bash
openssl x509 -in service.crt -noout -dates
```

For a remote server:

```bash
echo | openssl s_client -connect myservice.internal:443 2>/dev/null \
  | openssl x509 -noout -dates
```

## Storing Private Keys

- Store private keys in the team's Vault instance (see the Vault documentation).
- Never commit private keys to Git repositories.
- File permissions on the private key should be `400` (readable only by root/service user).
- Internal CA private keys are managed exclusively by the Platform Security team.

## Revoking a Certificate

If a private key is compromised, notify the Platform Security team immediately via Slack (`#security-incidents`). Provide:
- The certificate's serial number (`openssl x509 -in service.crt -noout -serial`)
- The date and nature of the suspected compromise

The CA team will revoke the certificate and issue a new one under an emergency ticket.
