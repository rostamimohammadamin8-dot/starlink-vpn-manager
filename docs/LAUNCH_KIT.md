# Community Launch Kit

Copy, adapt, and verify each draft before posting. Replace any wording that no longer matches the released version.

> **License:** Starlink VPN Manager is open-source software released under the MIT License. See the repository's `LICENSE` file for the terms. The project is not affiliated with SpaceX, Starlink, WireGuard, or the AI providers mentioned below.

## Reddit

### r/Starlink

**Title:** I built a Windows dashboard for VPN diagnostics and Starlink connection troubleshooting

**Post:**

I put together Starlink VPN Manager, an open-source Windows 10/11 WPF app released under the MIT License, for managing WireGuard profiles and diagnosing connection quality.

It runs on-demand ICMP, TCP, and HTTP latency checks, reports a transport-probe success score, and can launch sing-box with configurable routing rules for AI-service domains. There is also an optional Windows Firewall kill switch with an explicit UAC prompt and saved-policy restoration.

This does **not** automatically detect Starlink outages or fail over to another connection yet; outage auto-failover is on the roadmap. The diagnostics can help gather evidence when ping drops happen, but they cannot repair a Starlink or ISP issue.

Repository: https://github.com/rostamimohammadamin8-dot/starlink-vpn-manager

The firewall option is system-wide and deliberately opt-in. Please read the safety notes before enabling it. Feedback on diagnostics, Windows behavior, and feature priorities is welcome.

### r/csharp

**Title:** .NET 8 WPF project: MVVM dashboard, async network diagnostics, and Windows Firewall rollback

**Post:**

I have been building an open-source .NET 8/WPF Windows utility, released under the MIT License, around WireGuard and sing-box. The current implementation includes:

- CommunityToolkit.Mvvm view model and command bindings
- Async TCP-handshake, HTTP-response, ICMP, and DNS diagnostics
- sing-box AI-domain routing rule generation
- WireGuard profile encryption using current-user DPAPI
- An opt-in Windows Firewall kill switch with UAC, saved state, and rollback

The firewall control affects outbound traffic system-wide, so it is disabled by default and documents recovery steps. I would appreciate code/design feedback, particularly on testability, firewall policy edge cases, and separating the UI from existing profile workflows.

Repository: https://github.com/rostamimohammadamin8-dot/starlink-vpn-manager

### r/WireGuard

**Title:** Windows WireGuard profile manager with DPAPI storage and connection diagnostics

**Post:**

I built an open-source Windows dashboard, released under the MIT License, for importing and managing WireGuard `.conf` profiles. Imported profile contents are protected with current-user DPAPI, and the app can show tunnel service state plus peer handshake/transfer summaries when `wg.exe` is available.

It also measures ICMP/TCP/HTTP latency to help diagnose ping drops. An optional Windows Firewall guard can block outbound traffic outside an identified tunnel interface and configured VPN endpoint addresses; enabling it requires confirmation and administrator approval, and the app saves/restores the previous local firewall settings.

There is a sing-box integration for users who want domain-based AI-service routing. This is not part of WireGuard itself, does not guarantee access to any service, and does not bypass provider account or usage restrictions. Starlink outage failover and per-app split tunneling remain roadmap items.

Repository: https://github.com/rostamimohammadamin8-dot/starlink-vpn-manager

I would value feedback from people familiar with Windows tunnel services and firewall behavior. The kill switch is system-wide, so please review its safety documentation before trying it.

## Twitter / X

**Post:**

Built a Windows VPN dashboard for WireGuard + sing-box:

• DPAPI-protected WireGuard profiles
• TCP/HTTP/ICMP diagnostics
• Configurable AI-domain routing
• Optional Windows Firewall kill switch with rollback

Starlink auto-failover and per-app split tunneling are on the roadmap—not shipped yet.

https://github.com/rostamimohammadamin8-dot/starlink-vpn-manager

## LinkedIn

**Post:**

I’ve been developing Starlink VPN Manager, a .NET 8 WPF application for Windows users who manage WireGuard profiles and sing-box tunnels.

The dashboard combines current-user DPAPI profile storage, ICMP/TCP/HTTP network diagnostics, configurable AI-service domain routing, and an optional system-wide Windows Firewall kill switch. Firewall changes require explicit confirmation and administrator approval; previous local policy is saved for restoration.

Automatic Starlink outage failover and per-application split tunneling are planned, but are not implemented in the current release. Project and technical details: https://github.com/rostamimohammadamin8-dot/starlink-vpn-manager

Feedback on the architecture, Windows networking edge cases, and roadmap priorities is welcome.
