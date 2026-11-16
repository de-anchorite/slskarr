# Slskarr

Slskarr (Soulseek-*arr) bridges the *arr stack to the Soulseek network through
[slskd](https://github.com/slskd/slskd). It presents itself to the *arrs as both an **indexer**
(Newznab API) and a **download client** (SABnzbd API), so no *arr plugin is needed. This first
version supports **ebooks** (Chaptarr / Readarr) and **music** (Lidarr).

How it works: the *arr searches the Slskarr "indexer"; Slskarr searches Soulseek via slskd and returns
releases named after the Soulseek files. When the *arr grabs one, it hands Slskarr a stub NZB; Slskarr
queues the download in slskd, reports progress as a SABnzbd queue/history, and the *arr imports the
finished files from the shared downloads path.

Slskarr is gentle with the Soulseek network: searches are rate-limited and cached, and an empty
`t=search` (an *arr RSS poll) never triggers a Soulseek search.

## Quick start (Docker)

See [`examples/docker-compose.yml`](examples/docker-compose.yml) for slskd + Chaptarr + Lidarr + Slskarr.
The important part: **slskd's downloads directory must be mounted at the same path in slskd,
Chaptarr and Lidarr**, and `Slskarr__Slskd__DownloadsPath` must be that path, so the storage paths Slskarr reports
are valid for Chaptarr and Lidarr. Slskarr never reads or writes the downloaded files itself (it asks slskd to
delete them), so it does not need the downloads volume.

```bash
docker run -d --name slskarr -p 8080:8080 \
  -e Slskarr__Slskd__Url=http://slskd:5030 \
  -e Slskarr__Slskd__ApiKey=<slskd api key> \
  -e Slskarr__ApiKey=<key the *arrs will use> \
  -v slskarr-data:/data \
  ghcr.io/<owner>/slskarr:latest
```

The container listens on port 8080 and runs as the non-root `app` user (UID 1654). `/data` holds the
SQLite database. A named volume (as above) works as is.

**Bind-mounting a host folder** at `/data`: the container's user must be able to write to it. The image
does not use `PUID`/`PGID`. Either run the container as the folder's owner (usually what you want, and
what you already use for your other *arr containers):

```bash
mkdir -p /path/to/slskarr-data            # owned by you; `id` shows your uid:gid
docker run ... --user 1000:1000 -v /path/to/slskarr-data:/data ...
```

or in compose, `user: "1000:1000"` on the `slskarr` service. Or keep the default user and hand the
folder to it: `sudo chown 1654:1654 /path/to/slskarr-data`.

If `/data` is not writable, Slskarr exits at startup with a message naming the folder and the user it
runs as.

## slskd setup

- **API key role:** create the key with the `administrator` role (recommended). A `readwrite` key may
  not see completed transfers (unverified).
- **File management:** set `remote_file_management: true` in slskd's configuration (env
  `SLSKD_REMOTE_FILE_MANAGEMENT=true`). This is needed so Slskarr can delete imported files, and the
  partial files of removed downloads, through slskd. Without it the files stay in place and the Slskarr
  status page shows a warning.
- slskd >= 0.26.

## Chaptarr setup

**Indexer** (Settings > Indexers > Add > Newznab):

| Field | Value |
| --- | --- |
| URL | `http://slskarr:8080/newznab` |
| API Path | `/api` |
| API Key | your `Slskarr__ApiKey` (blank if unset) |
| Categories | `7020` |

**Download client** (Settings > Download Clients > Add > SABnzbd):

| Field | Value |
| --- | --- |
| Host | `slskarr` |
| Port | `8080` |
| URL Base | `sabnzbd` |
| API Key | your `Slskarr__ApiKey` (blank if unset) |
| Category | `ebooks` |

Use the hostname or IP your *arr can reach (`slskarr` is the compose service name).

**Placeholder item:** Chaptarr and Prowlarr only save an indexer whose RSS feed returns at least one
item, and a fresh Slskarr has none. Until real hits are stored, the feed returns one item titled
`Slskarr placeholder (no Soulseek results stored yet)`. It doesn't parse as a book, so RSS sync rejects
it (you may see it in the debug log), and grabbing it fails with "No such item". It stops appearing as
soon as the first search stores hits.

## Lidarr setup

**Indexer** (Settings > Indexers > Add > Newznab):

| Field | Value |
| --- | --- |
| URL | `http://slskarr:8080/newznab` |
| API Path | `/api` |
| API Key | your `Slskarr__ApiKey` (blank if unset) |
| Categories | `3000,3010,3040` |

**Download client** (Settings > Download Clients > Add > SABnzbd):

| Field | Value |
| --- | --- |
| Host | `slskarr` |
| Port | `8080` |
| URL Base | `sabnzbd` |
| API Key | your `Slskarr__ApiKey` (blank if unset) |
| Category | `music` |

How music works:

- **One Soulseek search per album request**: Slskarr searches `<artist> <album>` (for Various Artists
  releases, the album only), and Lidarr's follow-up `t=search` retry reuses that search. There is no
  album-only fallback search.
- A release is one folder, named from the Soulseek folder, e.g. `Artist - Album (1997) [FLAC]`. Disc
  subfolders (`CD1`, `Disc 2`, ...) are joined into one release. The bracketed tag is what Lidarr reads
  its quality from: `[FLAC]`, `[FLAC 24bit]`, `[MP3 320]`, `[MP3 VBR V0]`, and so on.
- **A failed track fails the whole album**: Slskarr reports the download as failed, so Lidarr can
  retry or pick another release. One slskd batch is queued per disc subfolder.
- **Cue/image rips** (one big file plus a `.cue`) are skipped.
- Lidarr imports the album folder from the shared downloads path (reported as the storage path).

The same placeholder item described for Chaptarr applies to a fresh Slskarr here too.

## Prowlarr (Generic Newznab)

Add an indexer of type **Generic Newznab** with URL `http://slskarr:8080/newznab`, API path `/api`,
your API key and categories `3000,3010,3040,7020`. Prowlarr syncs it to **Lidarr** (audio) and to
**Chaptarr** as the "Readarr" app type (books); the Chaptarr sync was confirmed. Prowlarr offers each
app only the categories it understands.

## Configuration

Configuration binds from the `Slskarr` section; environment variables use `Slskarr__Section__Key`.
`TimeSpan` values are `hh:mm:ss` (or `d.hh:mm:ss`). Array values use indexes
(`Slskarr__Ebook__Extensions__0=epub`, `...__1=pdf`); a configured array **replaces** its default.

Required: `Slskarr__Slskd__Url` and `Slskarr__Slskd__ApiKey`. Slskarr refuses to start without them.

| Variable | Default | Description |
| --- | --- | --- |
| `Slskarr__ApiKey` | (unset) | Key the *arrs must send as `apikey`. If unset, endpoints are unauthenticated (a warning is logged). |
| `Slskarr__DataPath` | `/data` | Directory for the SQLite database. |
| `Slskarr__Slskd__Url` | (required) | slskd base URL, e.g. `http://slskd:5030`. |
| `Slskarr__Slskd__ApiKey` | (required) | slskd API key. |
| `Slskarr__Slskd__DownloadsPath` | `/downloads` | slskd's downloads directory as the *arr sees it (the start of reported storage paths). |
| `Slskarr__Paths__StorageRewriteFrom` | (unset) | Path prefix to replace in storage paths reported to the *arr. |
| `Slskarr__Paths__StorageRewriteTo` | (unset) | Replacement prefix. |
| `Slskarr__Search__SyncWait` | `00:01:00` | How long a search request waits for results before returning. |
| `Slskarr__Search__Concurrency` | `1` | Concurrent Soulseek searches. |
| `Slskarr__Search__MinGap` | `00:00:10` | Minimum gap between searches. |
| `Slskarr__Search__HourlyCap` | `30` | Maximum searches per hour. |
| `Slskarr__Search__ResultCap` | `50` | Maximum releases returned per search. |
| `Slskarr__Search__CacheTtl` | `06:00:00` | How long search results are cached. |
| `Slskarr__Search__QueuedExpiry` | `1.00:00:00` | How long a queued search stays valid. |
| `Slskarr__Search__SlskdSearchTimeoutSeconds` | `15` | Search timeout passed to slskd (at least 5). |
| `Slskarr__Search__ResponseLimit` | `200` | Maximum peer responses requested from slskd. |
| `Slskarr__Search__RssWindow` | `7.00:00:00` | Window of recent releases served for RSS requests. |
| `Slskarr__Search__Retention` | `30.00:00:00` | How long old search results and finished searches are kept. |
| `Slskarr__Downloads__StallTimeout` | `01:00:00` | A download making no progress for this long is failed. |
| `Slskarr__Downloads__PollInterval` | `00:00:05` | How often download status is polled from slskd. |
| `Slskarr__Ebook__SabCategory` | `ebooks` | SABnzbd category for ebooks. |
| `Slskarr__Ebook__Extensions__N` | `epub azw3 azw mobi pdf fb2 djvu cbz cbr` | Accepted file extensions (case and a leading `.` are ignored). |
| `Slskarr__Ebook__FormatPreference__N` | `epub azw3 mobi azw pdf fb2 djvu cbz cbr` | Format preference order (used by the `Format` ranking criterion). |
| `Slskarr__Ebook__MinSizeBytes` | `10240` | Minimum file size. |
| `Slskarr__Ebook__MaxSizeBytes` | `209715200` | Maximum file size (200 MiB). |
| `Slskarr__Ebook__Ranking__N` | `NameMatch FreeSlot QueueLength UploadSpeed Format` | Ranking criteria, in order. Allowed: those five. |
| `Slskarr__Music__SabCategory` | `music` | SABnzbd category for music. |
| `Slskarr__Music__Extensions__N` | `flac mp3 m4a aac ogg opus wav aiff aif ape wv alac` | Accepted file extensions (case and a leading `.` are ignored). |
| `Slskarr__Music__FormatPreference__N` | `flac mp3 m4a alac ogg opus aac wav aiff aif ape wv` | Format preference order (used by the `Format` ranking criterion). |
| `Slskarr__Music__MinSizeBytes` | (unset) | Minimum total size of a release (all files). |
| `Slskarr__Music__MaxSizeBytes` | (unset) | Maximum total size of a release (all files). |
| `Slskarr__Music__Ranking__N` | `NameMatch FreeSlot QueueLength UploadSpeed Format` | Ranking criteria, in order. Allowed: those five. |

## Path mapping

Slskarr reports slskd's storage paths to the *arr, so slskd and the *arr need the downloads directory
at the same path (Slskarr itself does not mount it). If you cannot do that, set `Slskarr__Paths__StorageRewriteFrom` / `...RewriteTo` to
rewrite the prefix Slskarr reports, or use a Remote Path Mapping in the *arr.

A slskd hosted on **Windows** replaces `: * ? " < > |` in file names with `_`, so reported storage
paths may not match the files on disk. Linux/Docker slskd is not affected.

## Development

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
docker build -t slskarr:dev .
```

## Versioning and releases

Slskarr uses [semantic versioning](https://semver.org). The version comes from the git tag and is
not stored in the code: local and branch builds report `0.0.0-dev`. The running version shows in
the startup log, on the status page (`/`) and in `GET /health`.

Branches: `develop` is the only long-lived branch. Features branch off it and merge back by PR.
A release is a tag on `develop`.

To release:

1. Make sure CI is green on the `develop` commit you want to release.
2. Tag that commit and push the tag:
   ```bash
   git tag v0.1.0
   git push origin v0.1.0
   ```
3. CI runs the tests, then builds and pushes `ghcr.io/<owner>/slskarr`, then creates a GitHub
   Release with generated notes.

| Tag | Image tags | GitHub Release |
|---|---|---|
| `v1.2.3` | `1.2.3`, `1.2`, `latest` | normal |
| `v1.2.3-rc.1` | `1.2.3-rc.1` | pre-release |

Before 1.0, bump the minor version for features and the patch version for fixes. Tag `v1.0.0` once
the checklist below passes against real apps.

To build a versioned image by hand: `docker build --build-arg VERSION=1.2.3 -t slskarr:1.2.3 .`

## Testing against real apps

Automated tests use a fake slskd only. Before a release, with a throwaway Soulseek account:

1. Both Chaptarr connection tests (indexer and download client) pass.
2. Prowlarr Generic Newznab can be added and syncs to Chaptarr as the "Readarr" app type.
3. One interactive search shows Soulseek hits with Chaptarr's own rejection reasons.
4. One grab imports end to end.
5. Both Lidarr connection tests (indexer and download client) pass.
6. Prowlarr syncs the indexer to Lidarr.
7. A Lidarr interactive album search shows Soulseek hits with the right quality (`[FLAC]`, `[MP3 320]`, ...).
8. One album grab (including a multi-disc one) imports end to end in Lidarr.
