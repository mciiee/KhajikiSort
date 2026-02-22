using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using System.Text.Json.Serialization;
using KhajikiSort.Data;
using KhajikiSort.Models;
using KhajikiSort.Nlp;
using KhajikiSort.Routing;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var extractor = new NlpMetadataExtractor();
var gemmaApiKey = Environment.GetEnvironmentVariable("FIRE_GEMMA_API_KEY") ?? string.Empty;
var gemmaModel = Environment.GetEnvironmentVariable("FIRE_GEMMA_MODEL") ?? "gemma-3-4b-it";
var gemmaMaxRequests = int.TryParse(Environment.GetEnvironmentVariable("FIRE_GEMMA_MAX_REQUESTS"), out var maxReq)
    ? maxReq
    : 0;
var gemmaMinDelayMs = int.TryParse(Environment.GetEnvironmentVariable("FIRE_GEMMA_MIN_DELAY_MS"), out var minDelay)
    ? minDelay
    : 1200;
var gemmaAnalyzer = new GemmaNlpAnalyzer(new HttpClient(), extractor, gemmaApiKey, gemmaModel, gemmaMaxRequests, gemmaMinDelayMs);

var backendDir = AppContext.BaseDirectory;
var projectDir = Path.GetFullPath(Path.Combine(backendDir, "..", "..", "..", ".."));
var datasetsDir = Path.Combine(projectDir, "datasets");
var frontendDir = Path.Combine(projectDir, "frontend");
var dictionariesDir = Path.Combine(projectDir, "dictionaries");
var outputPath = Path.Combine(datasetsDir, "routing_results.csv");

var tickets = DatasetLoader.LoadTickets(Path.Combine(datasetsDir, "tickets.csv"));
var baseManagers = DatasetLoader.LoadManagers(Path.Combine(datasetsDir, "managers.csv"));
var businessUnits = DatasetLoader.LoadBusinessUnits(Path.Combine(datasetsDir, "business_units.csv"));

var stateLock = new object();
var results = new List<ProcessedTicket>();
var isAnalyzing = false;
var analyzedCount = 0;
var analysisStartedAtUtc = (DateTime?)null;
var analysisFinishedAtUtc = (DateTime?)null;
var lastError = string.Empty;
var currentRunLog = new ConcurrentQueue<string>();

Console.WriteLine($"Startup: tickets={tickets.Count}, managers={baseManagers.Count}, offices={businessUnits.Count}");
Console.WriteLine($"Gemma config: key={(string.IsNullOrWhiteSpace(gemmaApiKey) ? "missing" : "provided")}, model={gemmaModel}, maxRequests={gemmaMaxRequests}, minDelayMs={gemmaMinDelayMs}");

async Task RunAnalysisAsync(CancellationToken cancellationToken = default)
{
    var localRouter = new RoutingEngine();
    var managers = baseManagers.Select(m => new Manager
    {
        FullName = m.FullName,
        Position = m.Position,
        Office = m.Office,
        Skills = new HashSet<string>(m.Skills, StringComparer.OrdinalIgnoreCase),
        CurrentLoad = m.CurrentLoad
    }).ToList();

    var localResults = new List<ProcessedTicket>(tickets.Count);
    var runTimer = Stopwatch.StartNew();

    lock (stateLock)
    {
        isAnalyzing = true;
        analyzedCount = 0;
        analysisStartedAtUtc = DateTime.UtcNow;
        analysisFinishedAtUtc = null;
        lastError = string.Empty;
        results = [];
        while (currentRunLog.TryDequeue(out _)) { }
    }

    try
    {
        for (var i = 0; i < tickets.Count; i++)
        {
            var ticket = tickets[i];
            var ticketTimer = Stopwatch.StartNew();
            var startMsg = $"[{i + 1}/{tickets.Count}] Start ticket={ticket.ClientId}";
            currentRunLog.Enqueue(startMsg);
            Console.WriteLine(startMsg);

            var attachmentInsights = AttachmentAnalyzer.Analyze(ticket.AttachmentsRaw);
            var metadata = await gemmaAnalyzer.AnalyzeAsync(ticket.Description, ticket.AttachmentsRaw, projectDir, cancellationToken);
            var processed = localRouter.Route(ticket, metadata, attachmentInsights, managers, businessUnits);
            localResults.Add(processed);

            lock (stateLock)
            {
                analyzedCount = i + 1;
            }

            var doneMsg =
                $"[{i + 1}/{tickets.Count}] Done ticket={ticket.ClientId} " +
                $"source={metadata.AnalysisSource} type={metadata.RequestType} tone={metadata.Tone} " +
                $"priority={metadata.Priority} office={processed.SelectedOffice} manager={processed.SelectedManager ?? "UNASSIGNED"} " +
                $"elapsedMs={ticketTimer.ElapsedMilliseconds}";
            currentRunLog.Enqueue(doneMsg);
            Console.WriteLine(doneMsg);
        }

        RoutingResultWriter.Write(outputPath, localResults);

        var pgConn = Environment.GetEnvironmentVariable("FIRE_PG_CONN")
            ?? "Host=localhost;Port=5432;Database=firedb;Username=fire;Password=fire";
        try
        {
            await PostgresExporter.ExportAsync(
                pgConn,
                tickets,
                managers,
                businessUnits,
                localResults,
                dictionariesDir,
                cancellationToken);
            Console.WriteLine("PostgreSQL export: OK");
            currentRunLog.Enqueue("PostgreSQL export: OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PostgreSQL export failed: {ex.Message}");
            currentRunLog.Enqueue($"PostgreSQL export failed: {ex.Message}");
        }

        lock (stateLock)
        {
            results = localResults;
            analysisFinishedAtUtc = DateTime.UtcNow;
            Console.WriteLine($"Processed {localResults.Count} tickets");
            Console.WriteLine($"Total processing time: {runTimer.Elapsed}");
            Console.WriteLine($"Output CSV: {outputPath}");
            currentRunLog.Enqueue($"Processed {localResults.Count} tickets");
            currentRunLog.Enqueue($"Total processing time: {runTimer.Elapsed}");
            currentRunLog.Enqueue($"Output CSV: {outputPath}");
        }
    }
    catch (Exception ex)
    {
        lock (stateLock)
        {
            lastError = ex.Message;
            analysisFinishedAtUtc = DateTime.UtcNow;
        }
        Console.WriteLine($"Analysis failed: {ex}");
        currentRunLog.Enqueue($"Analysis failed: {ex.Message}");
    }
    finally
    {
        lock (stateLock)
        {
            isAnalyzing = false;
        }
    }
}

object BuildDashboardPayload()
{
    List<ProcessedTicket> snapshot;
    int analyzedSnapshot;
    bool isAnalyzingSnapshot;
    DateTime? startedAt;
    DateTime? finishedAt;
    string errorSnapshot;

    lock (stateLock)
    {
        snapshot = [..results];
        analyzedSnapshot = analyzedCount;
        isAnalyzingSnapshot = isAnalyzing;
        startedAt = analysisStartedAtUtc;
        finishedAt = analysisFinishedAtUtc;
        errorSnapshot = lastError;
    }

    return new
    {
        generatedAtUtc = DateTime.UtcNow,
        analysis = new
        {
            isAnalyzing = isAnalyzingSnapshot,
            analyzedCount = analyzedSnapshot,
            totalTickets = tickets.Count,
            startedAtUtc = startedAt,
            finishedAtUtc = finishedAt,
            lastError = errorSnapshot,
            logs = currentRunLog.Reverse().Take(60).Reverse().ToArray()
        },
        totalTickets = snapshot.Count,
        gemmaAnalyzedTickets = snapshot.Count(r => r.AiMetadata.AnalysisSource.StartsWith("Gemma", StringComparison.OrdinalIgnoreCase)),
        fallbackAnalyzedTickets = snapshot.Count(r => !r.AiMetadata.AnalysisSource.StartsWith("Gemma", StringComparison.OrdinalIgnoreCase)),
        unassignedTickets = snapshot.Count(r => r.SelectedManager is null),
        ticketsWithImages = snapshot.Count(r => r.AttachmentInsights.HasImageAttachment),
        requestTypeBreakdown = snapshot
            .GroupBy(r => r.AiMetadata.RequestType.ToString())
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { key = g.Key, count = g.Count() }),
        toneBreakdown = snapshot
            .GroupBy(r => r.AiMetadata.Tone.ToString())
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { key = g.Key, count = g.Count() }),
        officeBreakdown = snapshot
            .GroupBy(r => r.SelectedOffice)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { key = g.Key, count = g.Count() }),
        tickets = snapshot.Select(r => new
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
            summary = BuildClientTicketSummary(r.Ticket.Description),
            aiSummary = r.AiMetadata.Summary,
            managerSummary = ManagerSummaryFormatter.Build(r.AiMetadata.Summary, r.AiMetadata.Recommendation),
            managerSummaryShort = ManagerSummaryFormatter.Build(r.AiMetadata.Summary, r.AiMetadata.Recommendation, 130),
            managerSummaryFull = ManagerSummaryFormatter.Build(r.AiMetadata.Summary, r.AiMetadata.Recommendation, 5000),
            recommendation = r.AiMetadata.Recommendation,
            imageAnalysis = r.AiMetadata.ImageAnalysis,
            analysisSource = r.AiMetadata.AnalysisSource
        })
    };
}

static string BuildClientTicketSummary(string description)
{
    if (string.IsNullOrWhiteSpace(description))
    {
        return "No client text provided.";
    }

    var clean = description.Replace('\r', ' ').Replace('\n', ' ').Trim();
    return clean.Length <= 220 ? clean : $"{clean[..217].TrimEnd()}...";
}

var app = builder.Build();
app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = new PhysicalFileProvider(frontendDir)
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(frontendDir)
});

app.MapGet("/api/dashboard", () => Results.Ok(BuildDashboardPayload()));
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", datasetTickets = tickets.Count }));
app.MapPost("/api/analyze", () =>
{
    lock (stateLock)
    {
        if (isAnalyzing)
        {
            return Results.Conflict(new { status = "already-running" });
        }
    }

    _ = Task.Run(() => RunAnalysisAsync());
    return Results.Accepted("/api/dashboard", new { status = "started" });
});

Console.WriteLine("Analysis is now manual. Use UI button or POST /api/analyze to start processing.");
var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:5000";
Console.WriteLine($"UI: {urls}");
Console.WriteLine($"API: {urls.TrimEnd('/')}/api/dashboard");
app.Run(urls);
