# v2rayN

A GUI client for Windows, Linux and macOS, support [Xray](https://github.com/XTLS/Xray-core)
and [sing-box](https://github.com/SagerNet/sing-box)
and [others](https://github.com/2dust/v2rayN/wiki/List-of-supported-cores)

[![GitHub commit activity](https://img.shields.io/github/commit-activity/m/2dust/v2rayN)](https://github.com/2dust/v2rayN/commits/master)
[![CodeFactor](https://www.codefactor.io/repository/github/2dust/v2rayn/badge)](https://www.codefactor.io/repository/github/2dust/v2rayn)
[![GitHub Releases](https://img.shields.io/github/downloads/2dust/v2rayN/latest/total?logo=github)](https://github.com/2dust/v2rayN/releases)
[![Chat on Telegram](https://img.shields.io/badge/Chat%20on-Telegram-brightgreen.svg)](https://t.me/v2rayn)

## Features

- Cross-platform GUI client (Windows, Linux, macOS)
- Multi-protocol support: VMess, VLESS, Shadowsocks, Trojan, Hysteria2, TUIC, WireGuard, and more
- Multi-core support: Xray, sing-box, mihomo, and others
- Subscription management with auto-update
- Routing rules and DNS configuration
- Speed and latency testing
- TUN mode support
- **Cloudflare Best IP** — automatically probe and select optimal Cloudflare CDN IPs
- **Adaptive Node Scheduler** — health-based active-set scheduling, auto-eject failed nodes

## Cloudflare Best IP

One-click Cloudflare CDN node optimization:

1. Configure probe parameters in Settings (origin SNI, test paths, data source URLs)
2. Click "Cloudflare Best IP" from the menu
3. The two-phase pipeline automatically:
   - Fetches historical best IPs and general IP sources
   - Probes TCP+TLS+HTTP latency and download speed with Chrome fingerprint
   - Scores and ranks results (speed-passed IPs get top scores)
   - Exports optimized nodes and adds them to the `[CF优选]` group
   - POSTs all probe results to configured report URLs
4. Select any node from the `[CF优选]` group to start using the optimized Cloudflare connection

## Adaptive Node Scheduler

Health-driven active-set scheduling for proxy nodes. **Conservative Failure Isolation System** — eliminates obviously bad nodes, not a smart load balancer.

1. Enable "Adaptive" in a custom group's settings (right-click → Edit Server → Adaptive)
2. The scheduler automatically:
   - Probes all nodes on startup (Bootstrap TCP connect + DNS cache)
   - Monitors node health via active HTTP probes (HEAD/GET) and passive observation
   - Ejects failed nodes from the active set (cooldown with exponential backoff)
   - Recovers nodes via 4-stage Recovery Confirmation FSM (probing → stability → active)
   - Freezes control plane during mass outages (>60% nodes fail) to prevent self-oscillation
   - Distinguishes DNS failures from node failures (no penalty for DNS issues)
   - Uses hysteresis (Entry=60/Exit=35) + hash-based cooldown jitter to prevent flapping
3. All nodes in the active set share traffic uniformly (xray random balancer)
4. Emergency bypass: disable "Adaptive" checkbox to instantly restore default config

Architecture: v2rayN C# control plane maintains scores + manages active set; xray-core handles routing. See `docs/superpowers/specs/CLAUDE-loadbalance.md` (v7.3) for full design.

## How to use

Read the [Wiki](https://github.com/2dust/v2rayN/wiki) for details.

## Telegram Channel

[github_2dust](https://t.me/github_2dust)
