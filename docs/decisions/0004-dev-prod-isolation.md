# 0004-dev-prod-isolation

- **Status:** Accepted
- **Date:** 2026-09-15

## Context

Development must never modify production notes or the production
database; running from Visual Studio or the CLI must default to
Development (see
[vision.md §7](../vision.md#7-development-vs-production)).

## Decision

- Separate app-data roots per environment: `%LOCALAPPDATA%\MdBolsa.Dev\`
  vs `%LOCALAPPDATA%\MdBolsa\`.
- `AppEnvironment` resolved at startup, defaulting to Development;
  switching to Production requires an explicit local settings override.
- Development's default vault is the committed `samples/dev-vault/`
  sample, never the user's real vault.
- Configuration is layered via `appsettings.{Environment}.json`, with
  `Development`/`Production` variants gitignored and only `.example`
  templates committed.

## Consequences

- The distinction exists in the client from Phase 0/1 onward, even before
  a server exists (Phase 8+), so there's no later migration where "there
  was only one mode" needs to be retrofitted into two.
- Production credentials/URLs/paths never enter the repository or a
  default build; connecting to Production is always a deliberate act.
