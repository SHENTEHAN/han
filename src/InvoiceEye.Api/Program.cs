using System.Text.Json.Serialization;
using InvoiceEye.Api;
using InvoiceEye.Core.Data;
using InvoiceEye.Core.FixedExpenses;
using InvoiceEye.Core.Lottery;
using InvoiceEye.Core.Models;
using InvoiceEye.Core.Services;
using InvoiceEye.Core.Vision;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

var dbPath = builder.Configuration["Database:Path"] ?? Path.Combine(AppContext.BaseDirectory, "data", "invoiceeye.db");
builder.Services.AddSingleton(_ => Db.ForFile(dbPath));
builder.Services.AddSingleton(sp => new LedgerService(sp.GetRequiredService<Db>()));
builder.Services.AddSingleton<IVisionEngine>(sp => VisionEngineFactory.Create(builder.Configuration, sp.GetRequiredService<ILogger<Program>>()));
builder.Services.AddSingleton<RecognitionService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/status", (IVisionEngine engine, LedgerService ledger) => new
{
    engine = engine.Name,
    isDemo = engine is MockVisionEngine,
    today = ledger.Today,
    currentPeriod = InvoicePeriod.FromDate(ledger.Today).Key,
});

// ───── 辨識 ─────
api.MapPost("/recognize", async (IFormFile photo, RecognitionService recognizer) =>
{
    if (photo.Length == 0) return Results.BadRequest(new { error = "沒有收到照片" });
    if (photo.Length > 20 * 1024 * 1024) return Results.BadRequest(new { error = "照片太大（上限 20 MB）" });
    await using var stream = photo.OpenReadStream();
    try
    {
        return Results.Ok(await recognizer.RecognizeAsync(stream));
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "辨識失敗");
        return Results.Problem(title: "辨識失敗", detail: ex.Message, statusCode: 500);
    }
}).DisableAntiforgery();

// ───── 發票記帳 ─────
api.MapPost("/invoices", (SaveInvoiceRequest req, LedgerService ledger) =>
{
    var r = ledger.SaveInvoice(req with { Number = req.Number.Trim().ToUpperInvariant().Replace("-", "") });
    return r.Saved ? Results.Ok(Dto.SaveResult(r)) : Results.Conflict(Dto.SaveResult(r));
});

// ───── 帳本 ─────
api.MapGet("/transactions", (string? month, LedgerService ledger) =>
    ledger.GetTransactions(month ?? Dto.Month(ledger.Today)).Select(Dto.Tx));

api.MapPost("/transactions", (ManualTransactionRequest req, LedgerService ledger) =>
{
    try { return Results.Ok(new { id = ledger.AddManual(req) }); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

api.MapDelete("/transactions/{id:long}", (long id, LedgerService ledger) =>
    ledger.DeleteTransaction(id) ? Results.NoContent() : Results.NotFound());

// ───── 分類 ─────
api.MapGet("/categories", (LedgerService ledger) => ledger.GetCategories());
api.MapPut("/categories/{name}", (string name, BudgetRequest req, LedgerService ledger) =>
{
    ledger.SetBudget(name, req.Budget is > 0 ? req.Budget : null);
    return Results.NoContent();
});

// ───── 固定支出 ─────
api.MapGet("/fixed-expenses", (LedgerService ledger) => ledger.GetFixedExpenses().Select(e => Dto.Fixed(e, ledger.Today)));
api.MapGet("/fixed-expenses/upcoming", (LedgerService ledger) => ledger.UpcomingFixed().Select(u => new
{
    expense = Dto.Fixed(u.Expense, ledger.Today), dueDate = u.DueDate, daysLeft = u.DaysLeft,
}));
api.MapPost("/fixed-expenses", (FixedExpense e, LedgerService ledger) => SaveFixed(e with { Id = 0 }, ledger));
api.MapPut("/fixed-expenses/{id:long}", (long id, FixedExpense e, LedgerService ledger) => SaveFixed(e with { Id = id }, ledger));
api.MapDelete("/fixed-expenses/{id:long}", (long id, LedgerService ledger) =>
    ledger.DeleteFixedExpense(id) ? Results.NoContent() : Results.NotFound());

static IResult SaveFixed(FixedExpense e, LedgerService ledger)
{
    try { return Results.Ok(Dto.Fixed(ledger.SaveFixedExpense(e), ledger.Today)); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
}

// ───── 對獎 ─────
api.MapGet("/lottery", (LedgerService ledger) => ledger.LotteryOverview().Select(Dto.LotteryRow));
api.MapGet("/winning-numbers", (LedgerService ledger) => ledger.GetAllWinningNumbers().Select(Dto.Winning));
api.MapPut("/winning-numbers/{period}", (string period, WinningNumbersRequest req, LedgerService ledger) =>
{
    if (!InvoicePeriod.TryParseKey(period, out var p))
        return Results.BadRequest(new { errors = new[] { "期別格式不正確（例如 11512）" } });
    var w = new WinningNumbers(p, req.Special.Trim(), req.Grand.Trim(),
        req.First.Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
        (req.AdditionalSixth ?? new List<string>()).Select(x => x.Trim()).Where(x => x.Length > 0).ToList());
    var errors = ledger.SaveWinningNumbers(w);
    return errors.Count == 0 ? Results.NoContent() : Results.BadRequest(new { errors });
});

// ───── 報表 ─────
api.MapGet("/report", (string? month, LedgerService ledger) => Dto.Report(ledger.Report(month ?? Dto.Month(ledger.Today))));

// ───── 店家行業資料 ─────
api.MapPost("/registry/import", async (IFormFile file, LedgerService ledger) =>
{
    using var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8);
    try
    {
        var count = await Task.Run(() => ledger.ImportBusinessRegistry(reader));
        return Results.Ok(new { imported = count });
    }
    catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
}).DisableAntiforgery();

app.Run();

public sealed record BudgetRequest(decimal? Budget);
public sealed record WinningNumbersRequest(string Special, string Grand, List<string> First, List<string>? AdditionalSixth);

public partial class Program;
