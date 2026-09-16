# Deployment (Raspberry Pi)

**Not implemented yet.** This is a placeholder describing the intended
shape; it will be expanded starting Phase 8 (server foundation) and
Phase 9 (synchronization).

## Intended Shape

- Raspberry Pi 5 running Docker / Docker Compose.
- Services: ASP.NET Core sync API + PostgreSQL + filesystem storage for
  attachments/backups.
- All persistent paths derive from a single configurable root
  (`APP_DATA_PATH`-style setting) mapped to a Docker bind mount, so
  migrating from the current microSD card to a future SATA SSD is a
  bind-mount + data-copy operation, not a code or config-schema change.
- Production configuration (`appsettings.Production.json`, secrets,
  connection strings) lives only on the Pi and is never committed to this
  repo.

## Deployment Trigger

Deploys are always explicit and intentional:

```
feature/* -> develop -> main -> version tag -> explicit Pi deploy
```

No commit auto-deploys to production. See [vision.md §7-8](vision.md) for
the reasoning.
