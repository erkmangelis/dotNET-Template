# Files

Uploads, downloads and signed links on private S3-compatible storage. The bucket is never public:
a client reaches a file only through the API, or through a short-lived link the API signs after it
checked who is asking. Each file has a record in the database (`Files` table) with its owner, name,
detected type, size, SHA-256 and status; the content lives in storage under a key built from the
file's id alone, so nothing a client sends ever names an object.

## Storage

| Where | `Storage:ServiceUrl` | `Storage:Region` | Notes |
| --- | --- | --- | --- |
| Compose (RustFS) | `http://storage:9000` | any | Started by `docker compose up`, data in the `storage-data` volume |
| Development | `http://localhost:9000` | any | `docker compose up -d storage`, then user secrets for the keys |
| Cloudflare R2 | `https://<account>.r2.cloudflarestorage.com` | `auto` | Create the bucket and an API token with object read and write |
| Amazon S3 | `https://s3.<region>.amazonaws.com` | the region | `ForcePathStyle` may stay true |

```bash
docker compose up -d storage
dotnet user-secrets set Storage:AccessKey "<STORAGE_ACCESS_KEY from .env>" --project src/Acme.Notes.Api
dotnet user-secrets set Storage:SecretKey "<STORAGE_SECRET_KEY from .env>" --project src/Acme.Notes.Api
```

The compose file runs [RustFS](https://github.com/rustfs/rustfs) (Apache-2.0), which speaks the
S3 API and runs on a Raspberry Pi. MinIO no longer publishes container images, so it is not used.
Development and tests create the bucket at startup (`Storage:CreateBucket`); create production
buckets yourself and give the keys only object permissions.

`/health/ready` fails while the bucket does not answer.

## Three ways to upload

1. **Through the API**: `POST /v1/files` with a multipart form, field `file`. Simple, and the
   right choice for files of a few megabytes. Add `?temporary=true` for a file that should expire.
2. **Signed upload link**: `POST /v1/files/uploads` with `fileName`, `contentType` and `size` returns
   a pending record and an `uploadUrl`. The client PUTs the bytes there with exactly that
   `Content-Type`, then calls `POST /v1/files/{id}/complete`. The bytes never pass through the API,
   which suits large files and serverless frontends. A link works for `Files:UploadLinkMinutes`.
3. **Resumable (tus)**: [tus](https://tus.io) clients such as `tus-js-client` and Uppy upload to
   `/v1/files/tus` in pieces and resume after a dropped connection. Send the name as `filename` in
   `Upload-Metadata`. The upload's id is the file id. Unfinished pieces wait in `Files:Tus:Directory`
   (the `upload-data` volume in compose); with more than one API instance, share that volume or
   send a client to the same instance.

Whatever the route, every file takes the same checks in `FileProcessor` before it becomes
`available`:

- **Size**: at most `Files:MaxSizeBytes` (25 MB by default). The request body limit is raised for
  the upload endpoints only; every other endpoint keeps the 1 MB limit.
- **Type by signature**: `FileSignatures` reads the first bytes. The name and the client's
  `Content-Type` never decide the type, so a renamed executable stays refused. Only types in
  `Files:AllowedTypes` pass. To accept a new type, add its signature to `FileSignatures` and the
  type to the list.
- **Images**: more than `Files:MaxImagePixels` pixels is refused, so a tiny file cannot expand into
  gigabytes in memory. Accepted images get a WebP thumbnail (`GET /v1/files/{id}/thumbnail`,
  `Files:ThumbnailSize` on the longest side). The thumbnail drops the original's metadata, such as
  a phone's GPS position; the original is kept as uploaded.
- **Virus scan**: off by default. Set `Files:Scanning:Enabled` (compose: `FILES_SCANNING=true`) and
  start clamd with `docker compose --profile scan up -d`. When clamd cannot be reached, uploads fail
  rather than skip the scan.
- **Hash**: the SHA-256 is stored with the record.

A refused file answers 422 (413 when too large), its content is deleted, and a signed or tus upload
keeps a `rejected` record with the reason for `Files:RejectedFileHours`.

## Downloads

- `GET /v1/files/{id}/content` streams the file through the API as an attachment.
- `GET /v1/files/{id}/link` returns a signed link to the storage that works for
  `Files:DownloadLinkMinutes` (5 by default). Anyone holding the link can use it until then, so
  keep it short and never log it.

Signed links point at `Storage:PublicServiceUrl` (`STORAGE_PUBLIC_URL`). With the bundled RustFS,
clients outside the machine need a public hostname routed to `storage:9000`, for example a second
hostname on the Cloudflare Tunnel. Files are always served as attachments, never inline, so an
uploaded HTML or SVG file cannot run script on your domain.

## Ownership

A file belongs to the user who uploaded it. Every endpoint looks files up by id and owner together,
so another user's file is not found (404), the same as one that does not exist. API keys reach
files with the `files:read` and `files:write` scopes. `FileOwnershipTests` and `TusUploadTests`
prove one user cannot read, download, link, complete, continue or delete another user's file.

## Cleanup and retention

`FileCleanupLoop` runs `FileJanitor` every `Files:CleanupIntervalMinutes`:

- files whose `expiresAt` has passed: temporary files after `Files:TemporaryFileHours`, uploads never
  completed, and refused files;
- objects in the bucket that no record points to and that are older than `Files:OrphanGraceHours`,
  left behind by a crash between storing and saving, or written to the bucket by hand;
- tus uploads with no new piece for `Files:Tus:ExpirationHours`.

Every step is safe to repeat, so each API instance runs it.

## Backups

The database backup (`scripts/backup.sh`) holds the records, not the content. Back up the storage
too: for the bundled RustFS, the `storage-data` volume; for R2 or S3, the provider's replication or
versioning.
