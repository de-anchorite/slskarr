# Slskarr

Slskarr (Soulseek-*arr) bridges the *arr stack (Sonarr, Radarr, Lidarr, Readarr/Chaptarr, …) to the
Soulseek network via [slskd](https://github.com/slskd/slskd). It presents itself to the *arrs as both
an **indexer** and a **download client** by emulating existing, well-known API contracts, so no *arr
plugin is needed.

> Status: v1 ebook implementation (Chaptarr/Readarr) is built and covered by unit and in-process
> integration tests. Pre-release: the manual checklist against real slskd/Chaptarr/Prowlarr is pending
> (see README).

## Design (v1)

- Indexer face: **Newznab** API (`t=caps`, `search`, `book`), RSS/XML output. `tvsearch`, `movie` and `music` are not implemented yet.
- Download client face: **SABnzbd** API (`mode=version|get_config|fullstatus|addfile|queue|history|retry`).
- The "NZB" handed out is a stub carrying a Slskarr release ID. *arr validation requires
  well-formed XML, root `<nzb>`, and at least one `<file>` element.
- Backend: slskd HTTP API (v0), API-key auth. slskd >= 0.26.
- Stack: .NET / C# (ASP.NET Core minimal API), Docker image as primary distribution.

## Hard rules

- **Never commit design specs or implementation plans.** They live under `docs/superpowers/`
  (gitignored) or elsewhere locally. Never `git add` them.
- Be gentle with the Soulseek network: rate-limit searches, never search on *arr RSS polls
  (empty `t=search` with no query), cache results.

## Development

Requires the .NET 10 SDK (`global.json`).

```bash
dotnet build                      # build the solution
dotnet test                       # run all tests
dotnet run --project src/Slskarr  # run (needs Slskarr__Slskd__Url and Slskarr__Slskd__ApiKey)
docker build -t slskarr:dev .     # image (listens on 8080, data in /data)
docker compose -f examples/docker-compose.yml config   # validate the example stack
```

Configuration binds from the `Slskarr` section; env vars use `Slskarr__Section__Key`
(e.g. `Slskarr__Slskd__Url`).

Versioning: the version is not stored in code. It comes from the `v*` git tag: CI passes it as
`-p:Version` (Docker `VERSION` build arg). Untagged builds are `0.0.0-dev`. `AppVersion.Current`
exposes it. Features branch off `develop`. A release is `develop` merged to `main`, then a tag on
`main`; see README "Versioning and releases".

## Conventions

- Follow standard .NET conventions (nullable enabled, warnings as errors, file-scoped namespaces).
- TDD, contract first. Build test fixtures from the request and response shapes in the
  Chaptarr, Readarr, Sonarr and Prowlarr source.
- Integration tests run Slskarr in-process against a **fake slskd**. They assert that the
  correct requests were made.
- No real Soulseek and no real *arr apps in automated tests.
- Release names come only from Soulseek file and folder names, never from the search request.
  The *arr app is the judge.

## Prior art

- slskd-lidarr-bridge (Python, AGPL-3.0) — same Newznab+SABnzbd approach, Lidarr only.
- Lidarr.Plugin.Slskd, Tubifarry — Lidarr plugins (indexer + download client).
- Soularr / soulseekarr — scripts polling Lidarr's wanted list.
- slskdN — slskd fork calling Lidarr's API directly.
