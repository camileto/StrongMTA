# StrongMTA

High-volume Mail Transfer Agent written in C#/.NET 8, inspired by other MTAs. Built for large-scale outbound delivery with a goal of offering more native features and configuration options than traditional MTAs like Sendmail and Exim — without requiring third-party plugins, external scripts, or complex workarounds for common high-volume sending scenarios such as IP warm-up, per-domain delivery tuning, bounce correlation, and parallel fair-share dispatch.

> **Early stage.** This project is under active development and not yet production-ready. Work happens in spare time, so progress is gradual. A first release is expected within 6 months.

## Features (currently supported)

- **Fair-share parallel delivery** — reactive scheduler per `(domain × VirtualMta)` lane, no central coordinator loop; high-volume domains get proportionally more resources, small domains are never starved.
- **Two-level concurrency cap** — global (CPU-core heuristic, default `cores × 100`) and per queue (configurable per domain via `MaxConcurrentConnections`).
- **DKIM signing** — automatic signing at submission time, PEM key per sender domain.
- **IP warm-up** — automatic overflow routing to a "cold" VirtualMta while the main IP's daily per-domain limit has not yet been reached.
- **SMTP response rules engine** — regex over the SMTP response text with actions `ForceBounce`, `ForceExpire`, `ForceRetry`, `SkipMx`, `EnterBackoff`, `ExitBackoff`, `DisableSourceIp`, `BounceQueue`.
- **Bounced vs. Expired distinction** — `Bounced` = explicit permanent verdict (5xx, DSN `Status 5.x.x`, or `ForceBounce` rule); `Expired` = TTL exhausted without a permanent verdict from the remote.
- **Per-queue backoff mode** — alternate retry intervals when a queue is degraded; configurable automatic reversion to normal.
- **Inbound bounce/FBL listener** — dedicated SMTP listener for DSN (RFC 3464) and ARF feedback (RFC 5965); VERP token correlation; category classification.
- **Crash-safe spool** — write-temp + fsync + rename guarantees atomicity; two-level directory sharding prevents `readdir` degradation with millions of files; automatic recovery on boot.
- **JSONL accounting** — one file per day, append-only, easy to consume with `jq`.
- **Admin CLI** — test submission, queue listing/filtering, pause/resume by JobId, accounting tail.

## Roadmap

Planned features for upcoming releases, in no particular order:

- **Message template engine** — full-featured templating with variable substitution, conditionals (`if`/`else`), and loops for per-recipient personalization natively, without external preprocessors.
- **Per-domain rate limiting** — maximum messages per minute per queue (`MaxMessagesPerMinute`), using a token-bucket algorithm.
- **VMTA-wide concurrency cap** — an additional concurrency limit summed across all domains for a given VirtualMta.
- **Suppression list** — native do-not-send list checked at submission and delivery time.
- **Spool auto-purge** — configurable retention policy to clean up delivered and expired messages.
- **Web dashboard** — real-time queue stats, accounting visualization, and domain-level delivery reports.
- **HTTP Transmissions API** — REST endpoint for message submission, compatible with common ESP workflow patterns.
- **DANE / MTA-STS** — opportunistic and enforced TLS policy support for outbound delivery.
- **VirtualMta IP pool** — round-robin across multiple source IPs within a single VirtualMta.
- **Hot-reload of `mta-config.json`** — apply domain and VirtualMta config changes without restarting the daemon.

## Project structure

```
src/
  StrongMTA.Core/          # pure domain types (Message, DomainConfig, VirtualMta, ResponseRule, ...)
  StrongMTA.Spool/         # .msg + .state read/write, sharding, atomic writes, boot scanner
  StrongMTA.Dkim/          # DKIM signing via MimeKit
  StrongMTA.Smtp.Client/   # outbound delivery: MX lookup, STARTTLS, SMTP protocol
  StrongMTA.Smtp.Server/   # inbound listener dedicated to bounce/FBL
  StrongMTA.Bounce/        # DSN/ARF parsers, VERP correlation, response rules engine
  StrongMTA.Accounting/    # JSONL event writer with daily rotation
  StrongMTA.Engine/        # orchestration: fair-share scheduler, retry, warm-up, module wiring
  StrongMTA.Cli/           # admin commands (System.CommandLine)
  StrongMTA.Daemon/        # daemon executable (Generic Host + DI)
tests/
  StrongMTA.Core.Tests/
  StrongMTA.Spool.Tests/
  StrongMTA.Smtp.Client.Tests/
  StrongMTA.Smtp.Server.Tests/
  StrongMTA.Bounce.Tests/
  StrongMTA.Dkim.Tests/
  StrongMTA.Accounting.Tests/
  StrongMTA.Engine.Tests/
```

## Requirements

- .NET 8 SDK (`8.0.128` or later with `latestFeature` rollforward)
- Linux (the daemon uses source IP binding and POSIX paths; the CLI and tests work on any platform)

## Build and test

```bash
dotnet build StrongMTA.sln
dotnet test StrongMTA.sln
```

134 tests, 0 failures.

## Daemon configuration

### `appsettings.json`

```json
{
  "Spool": { "RootDirectory": "/var/spool/strongmta" },
  "MtaConfigPath": "mta-config.json",
  "BounceDomain": "bounce.example.com",
  "SmtpPort": 25,
  "BounceListenPort": 25,
  "Scheduler": { "GlobalMaxConcurrency": null }
}
```

`GlobalMaxConcurrency: null` uses the default `Environment.ProcessorCount × 100` (default of 1200 on a 12-core machine).

### `mta-config.json`

```json
{
  "domains": [
    {
      "domainName": "gmail.com",
      "maxConcurrentConnections": 40,
      "retryIntervals": ["00:10:00", "00:30:00", "01:00:00", "04:00:00"],
      "bounceAfter": "2.00:00:00"
    },
    {
      "domainName": "*",
      "maxConcurrentConnections": 5,
      "retryIntervals": ["00:10:00", "01:00:00", "04:00:00"],
      "bounceAfter": "2.00:00:00"
    }
  ],
  "virtualMtas": [
    {
      "name": "vmta-01",
      "sourceIp": "203.0.113.10",
      "hostName": "mta1.example.com",
      "dkimSelector": "default"
    }
  ]
}
```

`domainName: "*"` is the catch-all applied to domains without an explicit override.

### SMTP response rules

Configured per domain via `responseRules` (an additional field in the domain block of `mta-config.json`, mapped to `DomainConfig.ResponseRules`):

| Action | Effect |
|---|---|
| `ForceBounce` | Bounce immediately, even on a 4xx response |
| `ForceRetry` | Force retry, even on a 5xx response |
| `ForceExpire` | Mark as Expired immediately |
| `SkipMx` | Try the next MX host within the same attempt |
| `EnterBackoff` | Enable backoff retry intervals for the queue |
| `ExitBackoff` | Take the queue out of backoff mode |
| `DisableSourceIp` | Temporarily disable the VirtualMta |
| `BounceQueue` | Bounce all pending recipients for the domain |

An empty list (the default) changes no behavior — an explicit backwards-compatibility guarantee.

## Spool layout

```
/var/spool/strongmta/
  queue/<ab>/<cd>/<guid>.msg       # immutable message (JSON envelope + RFC 822 body)
  queue/<ab>/<cd>/<guid>.state     # mutable per-recipient state
  bounce-tokens/<ab>/<token>.json  # VERP token index → (messageId, recipientId)
  cold/warmup-counters.json        # warm-up counters per (vmta, domain)
  cold/backoff-state.json          # backoff state per (domain × VirtualMta)
  cold/disabled-sources.json       # temporarily disabled VirtualMtas
  accounting/YYYY-MM-DD.jsonl      # accounting events, append-only
```

The two-level sharding (`<ab>/<cd>` = first 4 hex chars of the message GUID) mirrors the Postfix layout to avoid `readdir` degradation at millions of entries.

## CLI

```bash
# Submit a test message to the spool
dotnet run --project src/StrongMTA.Cli -- submit \
  --from sender@example.com \
  --to recipient@gmail.com \
  --bounce-domain bounce.example.com \
  --subject "Test"

# List recipients in the queue
dotnet run --project src/StrongMTA.Cli -- queue list
dotnet run --project src/StrongMTA.Cli -- queue list --status Transient
dotnet run --project src/StrongMTA.Cli -- queue list --domain gmail.com

# Pause/resume a campaign by JobId
dotnet run --project src/StrongMTA.Cli -- queue pause  --job-id campaign-01
dotnet run --project src/StrongMTA.Cli -- queue resume --job-id campaign-01

# Follow accounting in real time
dotnet run --project src/StrongMTA.Cli -- accounting tail --follow
```

## Recipient statuses

| Status | Meaning |
|---|---|
| `Pending` | Awaiting first attempt |
| `InFlight` | Delivery attempt in progress |
| `Delivered` | Successfully delivered (2xx) |
| `Bounced` | Explicit permanent failure (5xx, DSN `Status 5.x.x`, or `ForceBounce` rule) |
| `Expired` | TTL (`BounceAfter`) exhausted with no explicit permanent verdict from the remote |
| `Transient` | Temporary failure, awaiting next retry |
| `Paused` | Administratively paused via CLI |
| `Suppressed` | Suppressed (FBL or categorical bounce) |

## Accounting events

`Received`, `Delivered`, `Bounced`, `Expired`, `Transient`, `RemoteBounce`, `RemoteFeedback` — each event includes `MessageId`, `RecipientId`, `DestinationDomain`, `VirtualMtaName`, SMTP code, and response text.
