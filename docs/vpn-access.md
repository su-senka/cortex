---
title: Connecting to the Corporate VPN
tags: [vpn, remote-access, network, security]
owner: IT Infrastructure
last_verified: 2025-06-01
---

# Connecting to the Corporate VPN

This guide covers how to connect to the corporate VPN from Windows, macOS, and Linux endpoints. The VPN is required for all remote access to internal systems, including GitLab, the internal wiki, and on-premise databases.

## Prerequisites

Before connecting, ensure you have:

- A valid Active Directory account with VPN access (request via the IT helpdesk ticket system if you don't have one).
- The GlobalProtect VPN client installed (see the Installation section below).
- Your device registered in Intune or the equivalent MDM system. Unmanaged personal devices cannot authenticate to the VPN.
- MFA enrolled via Microsoft Authenticator. The VPN uses Azure MFA for the second factor.

## Installing the Client

### Windows

1. Open the IT portal at **https://portal.internal/software** (accessible only on-premises or via existing VPN).
2. Search for **GlobalProtect** and download the latest approved version.
3. Run the installer as administrator.
4. When prompted for the **Portal Address**, enter: `vpn.companyname.internal`
5. Reboot is not required but recommended.

### macOS

1. Download GlobalProtect from the IT portal or from Self Service (if Jamf-enrolled).
2. Open the downloaded `.pkg` file and follow the prompts. macOS may require you to allow the system extension in **System Settings → Privacy & Security**.
3. Launch GlobalProtect from the menu bar icon after installation.
4. Enter the Portal Address: `vpn.companyname.internal`

### Linux

Linux is supported via OpenConnect. Install it from your distribution's package manager:

```bash
# Ubuntu / Debian
sudo apt install openconnect

# Fedora / RHEL
sudo dnf install openconnect
```

Then connect:

```bash
sudo openconnect --protocol=gp vpn.companyname.internal
```

You will be prompted for your AD username and password, followed by an MFA push notification.

## Connecting

1. Open the GlobalProtect client (or run the `openconnect` command on Linux).
2. If not pre-populated, enter the portal: `vpn.companyname.internal`
3. Enter your Active Directory credentials (same as your Windows login).
4. Approve the MFA push notification in Microsoft Authenticator within 60 seconds.
5. GlobalProtect will show **Connected** with your assigned IP address.

## Split Tunneling

The VPN uses **split tunneling**. Only traffic destined for the `10.0.0.0/8` and `192.168.0.0/16` networks routes through the VPN tunnel. General internet traffic goes directly from your device. This reduces VPN load and is intentional.

## Troubleshooting

**"Authentication failed" error**
- Verify your AD account is not locked (check with IT helpdesk).
- Ensure Authenticator is synced and your device clock is correct (MFA is time-sensitive).

**Connected but cannot reach internal services**
- Try running `nslookup internal-hostname.companyname.internal` to verify DNS is resolving via VPN.
- The internal DNS server is `10.1.1.53`. If not receiving DNS from the VPN, disconnect and reconnect.

**High latency or packet loss**
- The VPN gateway is in the Frankfurt data centre. Overseas offices may experience higher latency — this is expected.
- If latency is consistently > 200 ms from within Europe, contact the networking team.

## Disconnecting

Click the GlobalProtect tray icon and select **Disconnect**. Sessions are automatically terminated after 8 hours of inactivity.
