# Documents

CSV and Excel import and export, and PDF reports, shown on the notes feature. Copy the pattern in
`Features/Notes/NoteDocuments.cs` for your own features.

| Endpoint | What it does |
| --- | --- |
| `GET /v1/notes/export/csv` | The matching notes as CSV, streamed as they are read |
| `GET /v1/notes/export/xlsx` | The matching notes as an Excel workbook |
| `GET /v1/notes/report` | The matching notes as a PDF table, at most 5,000 rows |
| `POST /v1/notes/imports/file` | Creates notes from a `.csv` or `.xlsx` file (multipart, field `file`) |

The exports take the same filters as `GET /v1/notes` (`search`, `createdFrom`, `createdBefore`,
`sort`).

## Libraries

- [MiniExcel](https://github.com/mini-software/MiniExcel) (Apache-2.0) reads and writes `.xlsx` and
  reads CSV, without Office. CSV is written by `Spreadsheets.WriteCsvAsync`, a row at a time.
- [PdfPig](https://github.com/UglyToad/PdfPig) (Apache-2.0) builds the PDF. Text is set in
  Liberation Sans (SIL Open Font License, `Infrastructure/Documents/Fonts/OFL.txt`), embedded and
  subset in each PDF, so Turkish and other Latin, Greek and Cyrillic text prints the same everywhere.

Not used, for their licences: QuestPDF (paid above a revenue limit), PDFsharp 6.5 and later (AGPL),
ClosedXML (needs SixLabors.Fonts, paid above a revenue limit), EPPlus (paid). Check a library's
licence again before you upgrade it across a major version.

## Import rules

- The first row holds the column names; `Title` and `Content` are read, in any case, other columns
  are ignored.
- Each row is checked like `POST /v1/notes`. Rows that fail come back in `errors` with their row
  number as a spreadsheet shows it (the header is row 1); a title the owner already uses is counted
  in `skipped`. The other rows are created.
- At most 1,000 rows, and the request body limit (1 MB) bounds the file. For larger imports, upload
  the file and process it in a background job.

## Safe exports

A cell that starts with `=`, `+`, `-` or `@` is a formula to Excel and LibreOffice, which can run
commands or send data away when the file is opened (CSV injection). `Spreadsheets.Defused` prefixes
such cells with an apostrophe in both CSV and Excel exports. Keep every export going through it.

CSV is written in UTF-8 with a byte order mark, which Excel needs to show accented letters.
