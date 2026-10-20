# What JKSV sends over WebDAV

Notes from reading JKSV's source (J-D-K/JKSV), written down because none of this is documented and two versions behave differently. `tests/SaveKeep.Tests/JksvWebDavTests.cs` replays both sequences request by request.

JKSV reads `sd:/config/JKSV/webdav.json`:

```json
{
  "origin": "http://192.168.1.20:8797",
  "basepath": "dav/jksv",
  "username": "...",
  "password": "..."
}
```

`origin` has no trailing slash and `basepath` has no leading or trailing slash. Credentials go out as HTTP Basic on every request, up front (libcurl's default with `CURLOPT_USERNAME`). Neither version sends OPTIONS. WebDAV only starts if the Switch has internet access.

## 2025 rewrite (all releases from 08/2025)

Files: `source/remote/WebDav.cpp`, `source/appstates/BackupMenuState.cpp`.

- `basepath` itself is the root; there is no `JKSV/` folder.
- At start: `PROPFIND /dav/jksv/` with `Depth: 1`, then the same for every collection it finds, recursively. The XML has to parse, and the first `<response>` must be the folder itself.
- Opening a game whose folder isn't listed: `MKCOL /dav/jksv/<escaped title>/`. Anything other than 201 counts as failure.
- Upload (ZR): `PUT` to the parent id plus `"/" + name`. Because the parent id already ends in a slash, the path has a double slash: `/dav/jksv//Pokemon%20Sword/<file>.zip`. The body has a `Content-Length`, and curl adds `Expect: 100-continue`.
- Only zips are uploaded. A folder backup gets an error pop-up on the Switch.
- If a file of the same name is listed, JKSV "patches" it (an overwriting PUT). SaveKeep never lists files, so that doesn't happen.
- The PUT status is ignored. JKSV says the upload worked even if the server refused it.

## 11/05/2024 release (master branch)

Files: `src/webdav.cpp`, `src/fs/remote.cpp`, `src/ui/fld.cpp`.

- At start: `PROPFIND /dav/jksv/JKSV/` with `Depth: 0`. Without a 207 it sends `MKCOL` and carries on whatever the answer.
- Opening a game: the same check and `MKCOL` for `JKSV/<title>/`, then `PROPFIND` with `Depth: 1` to list it.
- Upload (ZR): `PROPFIND` `Depth: 0` on the file to decide between new and update, then a chunked `PUT` (no length).
- Folder backups are zipped by JKSV itself before upload.

## What SaveKeep answers

- `PROPFIND`: 207 for the root and for folders JKSV created; the listing holds folders only, never files. Unknown paths get 404.
- `MKCOL`: 201 the first time (the name is remembered; nothing is created on disk), 405 if it exists, 409 if the parent doesn't.
- `PUT`: 201 when a new save was stored, 204 when the same save was already there, 4xx with a reason otherwise (413 too large, 415 not a zip, 422 no usable `main`).
- `DELETE`: 403. `GET`, `HEAD`, `MOVE`, `COPY`, `PROPPATCH`, `LOCK` and the rest: 405.

A zip is accepted when it has exactly one `main` at the top or one folder down. The current JKSV puts `.nx_save_meta.bin` next to it; that is ignored, but the whole zip is kept as `original.zip`, since restoring the exact file JKSV made is the safest option.
