# 0014 - Per-device tokens

Date: 2026-09-27
Status: accepted
Supersedes: nothing. Extends 0011 (the shared token), which stays valid.

## The problem

Phase 9 put one shared token in front of the sync API: every device presents the
same string, and whoever holds it can do everything. That was a deliberate
"the simplest thing that closes *the API is on my network*", and 0011 said out
loud what it did not buy.

Two things have changed since, and both moved the cost of the shared token up.

**Attachments (0011-attachments).** With a shared token, anyone holding it can now
*upload*. That is not just reading somebody's notes any more - it is spending
their disk, filling their database with data they will then have to store and back
up, and putting arbitrary bytes where a client will render them. A read-only leak
is bad. A write channel into a self-hosted vault is a different kind of problem.

**It is a single point of revocation.** Losing a laptop with the shared token on it
means rotating the secret, and the rotated secret has to reach every other device
by hand. Every device that does not get it stops syncing, so the recovery is
"revoke the thing that makes everything work", which is why nobody does it.

## The decision

Per-device tokens, revocable one at a time.

The bootstrap credential (`Sync:Token`) stays exactly as it is and stays valid.
It is the only credential that can mint or revoke a device. A device gets its own
token once, stores that instead, and the shared token is never written to disk
again. After that, losing a device is one row in a list.

Three properties this has to have, or it is theatre:

1. **A device token cannot mint a device token.** Otherwise one stolen laptop
   escalates into permanent access, and the phase has made nothing safer. This is
   enforced by a separate check (`TokenAuthenticator.IsSharedToken`) rather than by
   reusing the general "is this a valid token" path, precisely so the difference is
   hard to remove by accident.
2. **A revoked token stops working immediately**, with no effect on any other
   device. This is the property the shared token could never have.
3. **The server cannot show a token again.** It stores `sha256(token)` and nothing
   else. The consequence is real and accepted: a lost device's token cannot be
   recovered, it is revoked and a new one minted. Recovery by reading a secret off
   a server is the thing this whole phase is about not doing.

Revoking sets `revoked_at` rather than deleting the row. A token that vanishes
from the list is indistinguishable from one that never existed, and "which of
these was the phone I gave away" needs an answer.

## What this deliberately does not do

- **No user accounts, no login, no password reset.** This is a self-hosted server
  for one person. Devices are the unit, not people.
- **No expiry.** A token lasts until revoked. Automatic expiry would mean the
  device list is a thing to maintain on a schedule, and for a home deployment the
  honest threat model is a lost machine, not a slow leak.
- **No per-device scopes.** Every device can read and write the whole vault. A
  read-only "phone" mode is a plausible next step and is not in this phase; it
  would need scope enforcement on every endpoint, and half-enforced scopes are
  worse than none.
- **The shared token still works.** It is what lets a second device be set up at
  all. Removing it would mean a bootstrap flow, and a bootstrap flow is more
  machinery than this problem needs. Its blast radius is now "can add a device",
  not "is the vault".

## Consequences

- The three endpoint groups no longer take a `string token` and each do their own
  comparison. They take a `TokenAuthenticator` and share `TokenAuthFilter`. Three
  copies of a check that is no longer a string comparison is three places to
  forget that a device token is not allowed to manage devices.
- Every authenticated request now carries an `AuthenticatedCaller` in
  `HttpContext.Items`, though nothing reads it yet. It is there so that adding an
  audit log is a read rather than a re-derivation from headers a client can lie
  about.
- `/health` reports the auth *mode* but never a device count: it needs no token,
  so a count would tell anyone who can reach the port how many credentials exist.
- The app's token box now has a button on it. The flow is: paste the shared
  token, press **Create this device's token**, and the box is replaced with a
  token only that machine can use. `DeviceTokenClient` is the client half.

## Testing

Integration tests against the real server and the real database
(`DeviceTokenTests`), because both properties above are properties of the whole
arrangement - a unit test of the store alone would have passed against an
authenticator that let devices mint devices.

The dev database is shared with every other integration test, so these revoke
every device they mint.
