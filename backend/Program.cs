using Microsoft.Extensions.FileProviders;
using System.Text.Json.Serialization;
using KhajikiSort.Data;
using KhajikiSort.Nlp;
using KhajikiSort.Routing;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var extractor = new NlpMetadataExtractor();
var router = new RoutingEngine();

var backendDir = AppContext.BaseDirectory;
var projectDir = Path.GetFullPath(Path.Combine(backendDir, "..", "..", "..", ".."));
var datasetsDir = Path.Combine(projectDir, "datasets");
var frontendDir = Path.Combine(projectDir, "frontend");
var outputPath = Path.Combine(datasetsDir, "routing_results.csv");

var tickets = DatasetLoader.LoadTickets(Path.Combine(datasetsDir, "tickets.csv"));
var managers = DatasetLoader.LoadManagers(Path.Combine(datasetsDir, "managers.csv"));
var businessUnits = DatasetLoader.LoadBusinessUnits(Path.Combine(datasetsDir, "business_units.csv"));

var results = new List<KhajikiSort.Models.ProcessedTicket>(tickets.Count);

foreach (var ticket in tickets)
{
    var attachmentInsights = AttachmentAnalyzer.Analyze(ticket.AttachmentsRaw);
    var metadata = extractor.Extract(ticket.Description, attachmentInsights.ContextForNlp);
    var processed = router.Route(ticket, metadata, attachmentInsights, managers, businessUnits);
    results.Add(processed);
}

RoutingResultWriter.Write(outputPath, results);
var app = builder.Build();
app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = new PhysicalFileProvider(frontendDir)
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(frontendDir)
});

var generatedAtUtc = DateTime.UtcNow;
var dashboardPayload = new
{
    generatedAtUtc,
    totalTickets = results.Count,
    unassignedTickets = results.Count(r => r.SelectedManager is null),
    ticketsWithImages = results.Count(r => r.AttachmentInsights.HasImageAttachment),
    requestTypeBreakdown = results
        .GroupBy(r => r.AiMetadata.RequestType.ToString())
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new { key = g.Key, count = g.Count() }),
    toneBreakdown = results
        .GroupBy(r => r.AiMetadata.Tone.ToString())
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new { key = g.Key, count = g.Count() }),
    officeBreakdown = results
        .GroupBy(r => r.SelectedOffice)
        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
        .Select(g => new { key = g.Key, count = g.Count() }),
    tickets = results.Select(r => new
    {
        clientId = r.Ticket.ClientId,
        segment = r.Ticket.Segment,
        requestType = r.AiMetadata.RequestType.ToString(),
        tone = r.AiMetadata.Tone.ToString(),
        priority = r.AiMetadata.Priority,
        language = r.AiMetadata.Language.ToString(),
        hasImageAttachment = r.AttachmentInsights.HasImageAttachment,
        imageAttachmentCount = r.AttachmentInsights.ImageAttachmentCount,
        selectedOffice = r.SelectedOffice,
        selectedManager = r.SelectedManager ?? "UNASSIGNED",
        assignmentReason = r.AssignmentReason,
        summary = r.AiMetadata.Summary
    })
};

app.MapGet("/api/dashboard", () => Results.Ok(dashboardPayload));
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", totalTickets = results.Count }));

Console.WriteLine($"Processed {results.Count} tickets");
Console.WriteLine($"Output CSV: {outputPath}");
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000";
Console.WriteLine($"UI: {urls}");
Console.WriteLine($"API: {urls.TrimEnd('/')}/api/dashboard");
app.Run(urls);
