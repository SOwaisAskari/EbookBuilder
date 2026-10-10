using System.Text;
using System.Text.RegularExpressions;
using EbookBuilder.Models;
using EbookBuilder.Services;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IEbookRepository, JsonEbookRepository>();
builder.Services.AddSingleton<TemplateStore>();
builder.Services.AddSingleton<ExportService>();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 10 * 1024 * 1024);

var app = builder.Build();

app.UseExceptionHandler(handler => handler.Run(async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { error = "Unexpected server error." });
}));

app.UseDefaultFiles();
app.UseStaticFiles();

const long MaxImageBytes = 8 * 1024 * 1024;

var api = app.MapGroup("/api/ebooks");

// ---- library ---------------------------------------------------------------

api.MapGet("/", async (IEbookRepository repo) => Results.Ok(await repo.ListAsync()));

api.MapPost("/", async (CreateRequest? req, IEbookRepository repo) =>
{
    var book = await repo.CreateAsync(req?.Title);
    return Results.Created($"/api/ebooks/{book.Id}", book);
});

api.MapGet("/{id}", async (string id, IEbookRepository repo) =>
    await repo.GetAsync(id) is { } book ? Results.Ok(book) : Results.NotFound());

api.MapPut("/{id}", async (string id, Ebook incoming, IEbookRepository repo) =>
    await repo.SaveAsync(id, incoming) is { } saved ? Results.Ok(saved) : Results.NotFound());

api.MapDelete("/{id}", async (string id, IEbookRepository repo) =>
    await repo.DeleteAsync(id) ? Results.NoContent() : Results.NotFound());

// ---- images ----------------------------------------------------------------

api.MapPost("/{id}/images", async (string id, HttpContext ctx, IEbookRepository repo) =>
{
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest(new { error = "Expected multipart/form-data." });

    var form = await ctx.Request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "No file uploaded." });
    if (file.Length > MaxImageBytes)
        return Results.BadRequest(new { error = "Image is larger than 8 MB." });

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var bytes = ms.ToArray();

    var ext = ImageSupport.DetectExtension(bytes);
    if (ext is null)
        return Results.BadRequest(new { error = "Only PNG, JPEG, GIF and WebP images are supported." });

    var name = await repo.SaveImageAsync(id, bytes, ext);
    return name is null
        ? Results.NotFound()
        : Results.Ok(new { fileName = name, url = $"/api/ebooks/{id}/images/{name}" });
});

api.MapGet("/{id}/images/{file}", (string id, string file, HttpContext ctx, IEbookRepository repo) =>
{
    var path = repo.GetImagePath(id, file);
    if (path is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "public,max-age=86400";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(path, ImageSupport.MimeType(file));
});

// ---- preview & export ------------------------------------------------------

api.MapGet("/{id}/preview", async (string id, IEbookRepository repo, ExportService export) =>
    await repo.GetAsync(id) is { } book
        ? Results.Content(export.BuildPreview(book), "text/html; charset=utf-8")
        : Results.NotFound());

api.MapGet("/{id}/export", async (string id, string? format, IEbookRepository repo, ExportService export) =>
{
    var book = await repo.GetAsync(id);
    if (book is null) return Results.NotFound();

    var slug = Slugify(book.Title);
    return (format ?? "zip").ToLowerInvariant() switch
    {
        "zip" => Results.File(export.BuildWebsiteZip(book), "application/zip", $"{slug}-website.zip"),
        "single" => Results.File(Encoding.UTF8.GetBytes(export.BuildSingleFile(book)), "text/html; charset=utf-8", $"{slug}.html"),
        "epub" => Results.File(export.BuildEpub(book), "application/epub+zip", $"{slug}.epub"),
        _ => Results.BadRequest(new { error = "format must be zip, single or epub." })
    };
});

app.Run();

static string Slugify(string title)
{
    var slug = Regex.Replace(title.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
    return slug.Length == 0 ? "ebook" : (slug.Length > 60 ? slug[..60] : slug);
}

record CreateRequest(string? Title);
