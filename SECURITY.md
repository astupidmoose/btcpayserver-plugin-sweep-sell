# Security

## Reporting a vulnerability

Please report security issues privately through GitHub's
[private vulnerability reporting](../../security/advisories/new) for this repository, not in a public
issue. Include the version, what an attacker can do and how to reproduce it. You'll get a reply within
a few days.

## What matters most here

Sweep & Sell can send coins from a store's hot wallet and trade on its exchange account, so these are
the issues we treat as critical:

- Exchange credentials readable by anyone other than the BTCPay server process: in the database
  unencrypted, in logs, in pages, in API responses or in error messages.
- Any way to make the plugin send coins to an address the store owner did not configure, or to an
  exchange address not freshly fetched from the store's own exchange account.
- Any way to trigger a sweep or sale from outside (an unauthenticated request, a forged event) or to
  make one happen twice.
- Access to one store's settings or keys from another store or a user without
  `CanModifyStoreSettings` on it.

See [docs/threat-model.md](docs/threat-model.md) for the full model.

## Guidance for users

- Create exchange API keys **without withdrawal permission**. The plugin never withdraws.
- Use a key only for this plugin, so it can be revoked without affecting anything else.
- Keep daily caps and dry run on until you've watched the activity log for a while.
- Back up BTCPay's data-protection keys with the rest of the server: without them, saved exchange keys
  can't be decrypted and must be entered again.
