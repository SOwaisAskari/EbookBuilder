# Bindery — e-book builder in ASP.NET Core 8

Write a book in the browser, then export it as:

| Export | What you get |
|---|---|
| **Website (.zip)** | `index.html` + `assets/css/reader.css` + `assets/js/reader.js` + `assets/images/*`. Opens from disk, or upload to any static host. |
| **Single HTML file** | One file with CSS, JS and images inlined. Easy to email. |
| **EPUB** | EPUB 3 (+ NCX for older readers) with cover, TOC and images. |

The exported reader has: contents drawer, full-text search, previous/next + arrow keys, light/sepia/dark,
serif/sans, text size, remembers your place, progress bar, a print-the-whole-book stylesheet, and RTL support
(Urdu, Sindhi, Arabic, Persian, ...).

## Run

```bash
dotnet run          # http://localhost:5080
```
Requires only the .NET 8 SDK. **No NuGet packages** are used (everything is in the BCL).
Or: `docker build -t bindery . && docker run -p 8080:8080 -v bindery-data:/data bindery`

## Layout

```
Program.cs                     minimal-API endpoints
Models/Ebook.cs                Ebook, Chapter, ReaderTheme
Services/
  EbookRepository.cs           IEbookRepository + JSON-on-disk implementation
  ContentSanitizer.cs          whitelist HTML sanitizer (also makes output valid XHTML)
  ExportService.cs             website zip / single file / EPUB / preview
  ImageSupport.cs              magic-byte image sniffing, MIME helpers
  TemplateStore.cs             loads Templates/*
Templates/                     reader.css, reader.js, epub.css  (edit these to restyle exports)
wwwroot/                       editor UI (plain HTML/CSS/JS, no build step)
```

## API

| Method | Route | |
|---|---|---|
| GET / POST | `/api/ebooks` | list / create |
| GET / PUT / DELETE | `/api/ebooks/{id}` | read / save whole book / delete |
| POST | `/api/ebooks/{id}/images` | multipart `file` (PNG, JPEG, GIF, WebP, max 8 MB) |
| GET | `/api/ebooks/{id}/images/{file}` | serve image |
| GET | `/api/ebooks/{id}/preview` | live reader page |
| GET | `/api/ebooks/{id}/export?format=zip\|single\|epub` | download |

Data lives in `App_Data/books/{id}/book.json` (+ `images/`). Change the folder with `Storage:Path`.

## Before going public

* **No authentication**: every visitor sees every book. Add ASP.NET Core Identity (or any OIDC provider),
  add `OwnerId` to `Ebook`, and filter in the repository.
* Replace `JsonEbookRepository` with EF Core + SQL by implementing `IEbookRepository`.
* `ContentSanitizer` is a strict whitelist, but for a multi-tenant site add the `HtmlSanitizer` NuGet package as a second layer.
* Add rate limiting (`AddRateLimiter`) on the upload and export endpoints, and serve over HTTPS.

## Ideas to extend

PDF export (QuestPDF), Markdown import, drag-and-drop image upload, per-chapter word counts, footnotes,
custom CSS per book, translated reader labels (currently English: "Contents", "Previous", "Next").
