# Agent instructions

Instructions for coding agents working in this repo (Claude Code, Codex, and others).

## Deploying `bhgrafana`

- **Deploy only from `main`, and only after the change has been reviewed and merged.**
  Never `fly deploy` from a feature branch, even when asked to "deploy now": open a PR,
  get it reviewed and merged, then deploy from an up-to-date `main`. A deploy from an
  unmerged branch is silently reverted by the next deploy from `main`.
- Initialize submodules before building (`git submodule update --init`); the Docker
  build copies `dashboards/` from one.
- Deploy with `fly deploy --flycast`, then confirm `fly ips list` shows no public IPs.
  The private network is the only auth boundary (Grafana is anonymous).

## Records

- Record every decision in `DEVLOG.md`, newest entry first. Explain the reasoning and
  include the numbers you measured.
- Size storage from measured on-disk bytes, not raw bytes × an average compression
  ratio (see the 2026-09-18 entries).
