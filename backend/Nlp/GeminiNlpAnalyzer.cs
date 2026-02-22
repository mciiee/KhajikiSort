using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KhajikiSort.Models;

namespace KhajikiSort.Nlp;

public sealed class GemmaNlpAnalyzer
{
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private readonly HttpClient _httpClient;
    private readonly NlpMetadataExtractor _fallback;
    private readonly string _apiKey;
    private readonly string[] _models;
    private readonly int _maxRequestsPerRun;
    private readonly int _minDelayMs;
    private readonly object _stateLock = new();
    private string? _resolvedModel;
    private bool _modelResolutionAttempted;
    private int _requestsSent;
    private DateTimeOffset _nextAllowedRequestAtUtc = DateTimeOffset.MinValue;

    public GemmaNlpAnalyzer(
        HttpClient httpClient,
        NlpMetadataExtractor fallback,
        string apiKey,
        string model,
        int maxRequestsPerRun,
        int minDelayMs)
    {
        _httpClient = httpClient;
        _fallback = fallback;
        _apiKey = apiKey;
        var preferred = string.IsNullOrWhiteSpace(model) ? "gemma-3-4b-it" : model;
        _models = [preferred];
        _maxRequestsPerRun = maxRequestsPerRun <= 0 ? int.MaxValue : maxRequestsPerRun;
        _minDelayMs = minDelayMs < 0 ? 0 : minDelayMs;
    }

    public async Task<AiMetadata> AnalyzeAsync(
        string text,
        string attachmentsRaw,
        string projectDir,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            var attachmentHints = AttachmentAnalyzer.Analyze(attachmentsRaw).ContextForNlp;
            return WithSource(_fallback.Extract(text, attachmentHints), "RulesFallback(NoGemmaApiKey)");
        }

        try
        {
            await RequestGate.WaitAsync(cancellationToken);
            var attachmentHints = AttachmentAnalyzer.Analyze(attachmentsRaw).ContextForNlp;
            var model = await ResolveModelAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(model))
            {
                return WithSource(_fallback.Extract(text, attachmentHints), "RulesFallback(GemmaModelUnavailable)");
            }

            if (!CanSendAnotherRequest())
            {
                return WithSource(_fallback.Extract(text, attachmentHints), "RulesFallback(GemmaBudgetExceeded)");
            }

            var useJsonMime = !IsJsonModeUnsupportedModel(model);
            var requestWithImages = BuildRequest(
                text,
                attachmentsRaw,
                projectDir,
                attachmentHints,
                includeImages: true,
                includeAttachmentHints: true,
                includeResponseMimeType: useJsonMime);
            var response = await SendGemmaAsync(model, requestWithImages, countAgainstBudget: true, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Some Gemma variants reject image parts / responseMimeType / larger prompts.
                if ((int)response.StatusCode == 400)
                {
                    await LogBadRequestAsync(response, "attempt#1 images+jsonmime", cancellationToken);
                    response.Dispose();

                    var requestTextOnly = BuildRequest(
                        text,
                        attachmentsRaw,
                        projectDir,
                        attachmentHints,
                        includeImages: false,
                        includeAttachmentHints: true,
                        includeResponseMimeType: useJsonMime);
                    response = await SendGemmaAsync(model, requestTextOnly, countAgainstBudget: false, cancellationToken);

                    if ((int)response.StatusCode == 400)
                    {
                        await LogBadRequestAsync(response, "attempt#2 text-only+jsonmime", cancellationToken);
                        response.Dispose();

                        var requestTextOnlyNoMime = BuildRequest(
                            text,
                            attachmentsRaw,
                            projectDir,
                            attachmentHints,
                            includeImages: false,
                            includeAttachmentHints: true,
                            includeResponseMimeType: false);
                        response = await SendGemmaAsync(model, requestTextOnlyNoMime, countAgainstBudget: false, cancellationToken);
                    }

                    if ((int)response.StatusCode == 400)
                    {
                        await LogBadRequestAsync(response, "attempt#3 text-only+no-mime", cancellationToken);
                        response.Dispose();

                        var requestMinimal = BuildRequest(
                            text,
                            attachmentsRaw,
                            projectDir,
                            attachmentHints: string.Empty,
                            includeImages: false,
                            includeAttachmentHints: false,
                            includeResponseMimeType: false);
                        response = await SendGemmaAsync(model, requestMinimal, countAgainstBudget: false, cancellationToken);
                    }
                }

                if (response.IsSuccessStatusCode)
                {
                    // Retry path may recover from 400 to 200; continue to parse phase.
                }
                else
                {
                if ((int)response.StatusCode == 429)
                {
                    var retryAfter = ParseRetryAfterSeconds(response);
                    SetBackoff(retryAfter);
                    response.Dispose();
                    return WithSource(_fallback.Extract(text, attachmentHints), "RulesFallback(GemmaHttp429)");
                }

                if ((int)response.StatusCode == 404)
                {
                    lock (_stateLock)
                    {
                        _resolvedModel = null;
                        _modelResolutionAttempted = false;
                    }
                    response.Dispose();
                    return WithSource(_fallback.Extract(text, attachmentHints), "RulesFallback(GemmaHttp404)");
                }

                var status = (int)response.StatusCode;
                response.Dispose();
                return WithSource(_fallback.Extract(text, attachmentHints), $"RulesFallback(GemmaHttp{status})");
                }
            }

            var responseBody = await ReadResponseBodyAsync(response, cancellationToken);
            response.Dispose();
            var rawText = ExtractModelTextFromResponse(responseBody);

            if (string.IsNullOrWhiteSpace(rawText))
            {
                LogResponseSnippet("Gemma 200 but empty text payload", responseBody);
                return WithSource(_fallback.Extract(text, attachmentHints), "RulesFallback(GemmaEmpty)");
            }

            var parsed = ParseGeminiJson(rawText);
            if (parsed is null)
            {
                var heuristic = ParseGeminiLoose(rawText, text, attachmentHints);
                if (heuristic is not null)
                {
                    return heuristic;
                }

                LogResponseSnippet("Gemma 200 parse failure, raw model text", rawText);
                return WithSource(_fallback.Extract(text, attachmentHints), "RulesFallback(GemmaParse)");
            }

            return parsed;
        }
        catch
        {
            var attachmentHints = AttachmentAnalyzer.Analyze(attachmentsRaw).ContextForNlp;
            return WithSource(_fallback.Extract(text, attachmentHints), "RulesFallback(GemmaException)");
        }
        finally
        {
            if (RequestGate.CurrentCount == 0)
            {
                RequestGate.Release();
            }
        }
    }

    private static GeminiRequestEnvelope BuildRequest(
        string text,
        string attachmentsRaw,
        string datasetsDir,
        string attachmentHints,
        bool includeImages,
        bool includeAttachmentHints,
        bool includeResponseMimeType)
    {
        var prompt = """
            You are a strict ticket classifier for a brokerage support queue.
            Return ONLY valid JSON with this schema:
            {
              "requestType": "Complaint|DataChange|Consultation|Claim|AppFailure|FraudulentActivity|Spam",
              "tone": "Positive|Neutral|Negative",
              "priority": 1-10,
              "language": "RU|KZ|ENG",
              "summary": "1-2 short sentences",
              "recommendation": "clear next action for manager",
              "imageAnalysis": "short analysis of attached image(s), or empty string if no images"
            }

            Classification notes:
            - Choose exactly one requestType from the provided list.
            - Priority must be integer 1..10.
            - If language is uncertain, use RU.
            - summary, recommendation, and imageAnalysis must be written in the same language as the ticket (RU/KZ/ENG).
            - Keep summary and recommendation concise (max about 25 words each).
            - If text includes money not received/refund demand, lean to "Claim".
            - If cannot login/register/app broken, lean to "AppFailure".
            - If suspicious unauthorized activity, lean to "FraudulentActivity".
            """;

        var parts = new List<GeminiPart>
        {
            new() { Text = prompt },
            new() { Text = $"Ticket text:\n{text}" }
        };

        if (includeAttachmentHints && !string.IsNullOrWhiteSpace(attachmentHints))
        {
            parts.Add(new GeminiPart { Text = $"Attachment hints:\n{attachmentHints}" });
        }

        if (includeImages)
        {
            foreach (var imagePath in ResolveImagePaths(attachmentsRaw, datasetsDir))
            {
                var bytes = File.ReadAllBytes(imagePath);
                parts.Add(new GeminiPart
                {
                    InlineData = new GeminiInlineData
                    {
                        MimeType = GetMimeType(imagePath),
                        Data = Convert.ToBase64String(bytes)
                    }
                });
            }
        }

        return new GeminiRequestEnvelope
        {
            Contents = [new GeminiContent { Parts = parts }],
            GenerationConfig = new GeminiGenerationConfig
            {
                Temperature = 0.1,
                ResponseMimeType = includeResponseMimeType ? "application/json" : null
            }
        };
    }

    private static async Task LogBadRequestAsync(HttpResponseMessage response, string stage, CancellationToken cancellationToken)
    {
        var body = await ReadResponseBodyAsync(response, cancellationToken);
        if (body.Length > 500)
        {
            body = body[..500];
        }

        Console.WriteLine($"Gemma 400 at {stage}: {body}");
    }

    private static async Task<string> ReadResponseBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void LogResponseSnippet(string title, string body)
    {
        var snippet = body ?? string.Empty;
        if (snippet.Length > 500)
        {
            snippet = snippet[..500];
        }

        Console.WriteLine($"{title}: {snippet}");
    }

    private static string? ExtractModelTextFromResponse(string responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("candidates", out var candidates) &&
                candidates.ValueKind == JsonValueKind.Array)
            {
                foreach (var candidate in candidates.EnumerateArray())
                {
                    if (!candidate.TryGetProperty("content", out var content) ||
                        content.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (!content.TryGetProperty("parts", out var parts) ||
                        parts.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.Object &&
                            part.TryGetProperty("text", out var text) &&
                            text.ValueKind == JsonValueKind.String)
                        {
                            var value = text.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                return value;
                            }
                        }
                    }
                }
            }

            if (root.TryGetProperty("text", out var directText) && directText.ValueKind == JsonValueKind.String)
            {
                return directText.GetString();
            }
        }
        catch
        {
            // ignore and return null
        }

        return null;
    }

    private static AiMetadata? ParseGeminiJson(string raw)
    {
        try
        {
            var json = raw.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                var lines = json.Split('\n').ToList();
                if (lines.Count >= 2)
                {
                    lines.RemoveAt(0);
                    if (lines[^1].TrimStart().StartsWith("```", StringComparison.Ordinal))
                    {
                        lines.RemoveAt(lines.Count - 1);
                    }

                    json = string.Join('\n', lines).Trim();
                }
            }

            if (!json.StartsWith("{", StringComparison.Ordinal))
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    json = json[start..(end + 1)];
                }
            }
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var requestTypeRaw = root.TryGetProperty("requestType", out var rtEl) ? rtEl.GetString() : null;
            var toneRaw = root.TryGetProperty("tone", out var toneEl) ? toneEl.GetString() : null;
            var priorityRaw = root.TryGetProperty("priority", out var prEl) ? prEl.GetInt32() : 4;
            var langRaw = root.TryGetProperty("language", out var langEl) ? langEl.GetString() : "RU";
            var summary = root.TryGetProperty("summary", out var sEl) ? sEl.GetString() ?? string.Empty : string.Empty;
            var recommendation = root.TryGetProperty("recommendation", out var rEl) ? rEl.GetString() ?? string.Empty : string.Empty;
            var imageAnalysis = root.TryGetProperty("imageAnalysis", out var iEl) ? iEl.GetString() ?? string.Empty : string.Empty;

            var requestType = MapRequestType(requestTypeRaw);
            var tone = MapTone(toneRaw);
            var language = MapLanguage(langRaw);
            var priority = Math.Clamp(priorityRaw, 1, 10);

            return new AiMetadata(
                requestType,
                tone,
                priority,
                language,
                string.IsNullOrWhiteSpace(summary) ? "No summary provided." : summary,
                string.IsNullOrWhiteSpace(recommendation) ? "Manual review by manager is required." : recommendation,
                imageAnalysis,
                "Gemma");
        }
        catch
        {
            return null;
        }
    }

    private AiMetadata? ParseGeminiLoose(string rawText, string ticketText, string attachmentHints)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        var fallback = _fallback.Extract(ticketText, attachmentHints);
        var compact = rawText
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        var summary = compact.Length <= 180 ? compact : $"{compact[..177].TrimEnd()}...";

        var recommendation = ExtractFieldValue(rawText, "recommendation")
            ?? ExtractFieldValue(rawText, "action")
            ?? fallback.Recommendation;
        if (recommendation.Length > 180)
        {
            recommendation = $"{recommendation[..177].TrimEnd()}...";
        }

        var requestType = MapRequestType(rawText);
        var tone = MapTone(rawText);
        var language = MapLanguage(rawText);
        var priority = ExtractPriority(rawText) ?? fallback.Priority;

        return new AiMetadata(
            requestType == RequestType.Consultation ? fallback.RequestType : requestType,
            tone == Tone.Neutral ? fallback.Tone : tone,
            Math.Clamp(priority, 1, 10),
            language == LanguageCode.RU ? fallback.Language : language,
            summary,
            recommendation,
            string.Empty,
            "GemmaTextHeuristic");
    }

    private static int? ExtractPriority(string text)
    {
        var m = Regex.Match(text, @"priority\D{0,8}([1-9]|10)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var p))
        {
            return p;
        }

        var any = Regex.Match(text, @"\b([1-9]|10)\b");
        if (any.Success && int.TryParse(any.Groups[1].Value, out p))
        {
            return p;
        }

        return null;
    }

    private static string? ExtractFieldValue(string text, string field)
    {
        var m = Regex.Match(text, $@"{Regex.Escape(field)}\s*[:=-]\s*(.+)", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return null;
        }

        return m.Groups[1].Value.Trim();
    }

    private static bool IsJsonModeUnsupportedModel(string model)
    {
        // Current Gemma hosted variants often reject responseMimeType JSON mode.
        return model.Contains("gemma", StringComparison.OrdinalIgnoreCase);
    }

    private static RequestType MapRequestType(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (v.Contains("жалоб") || v.Contains("complaint"))
        {
            return RequestType.Complaint;
        }

        if (v.Contains("смен") || v.Contains("data change") || v.Contains("update data"))
        {
            return RequestType.DataChange;
        }

        if (v.Contains("консультац") || v.Contains("consult"))
        {
            return RequestType.Consultation;
        }

        if (v.Contains("претенз") || v.Contains("claim") || v.Contains("refund"))
        {
            return RequestType.Claim;
        }

        if (v.Contains("неработоспособ") || v.Contains("app") || v.Contains("application") || v.Contains("login") || v.Contains("register"))
        {
            return RequestType.AppFailure;
        }

        if (v.Contains("мошен") || v.Contains("fraud") || v.Contains("scam") || v.Contains("unauthorized"))
        {
            return RequestType.FraudulentActivity;
        }

        if (v.Contains("спам") || v.Contains("spam") || v.Contains("unsolicited"))
        {
            return RequestType.Spam;
        }

        return v switch
        {
            "жалоба" => RequestType.Complaint,
            "смена данных" => RequestType.DataChange,
            "консультация" => RequestType.Consultation,
            "претензия" => RequestType.Claim,
            "неработоспособность приложения" => RequestType.AppFailure,
            "мошеннические действия" => RequestType.FraudulentActivity,
            "спам" => RequestType.Spam,
            _ => RequestType.Consultation
        };
    }

    private static Tone MapTone(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (v.Contains("позитив") || v.Contains("positive"))
        {
            return Tone.Positive;
        }

        if (v.Contains("негатив") || v.Contains("negative"))
        {
            return Tone.Negative;
        }

        return v switch
        {
            "позитивный" => Tone.Positive,
            "негативный" => Tone.Negative,
            _ => Tone.Neutral
        };
    }

    private static LanguageCode MapLanguage(string? value)
    {
        var v = (value ?? "RU").Trim().ToUpperInvariant();
        if (v.Contains("KZ") || v.Contains("KAZ"))
        {
            return LanguageCode.KZ;
        }

        if (v.Contains("EN"))
        {
            return LanguageCode.ENG;
        }

        return v switch
        {
            "KZ" => LanguageCode.KZ,
            "ENG" => LanguageCode.ENG,
            _ => LanguageCode.RU
        };
    }

    private static IEnumerable<string> ResolveImagePaths(string raw, string projectDir)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var imageExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic"
        };

        var tokens = raw
            .Replace('\r', '\n')
            .Split([';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<string>();
        foreach (var token in tokens)
        {
            var ext = Path.GetExtension(token);
            if (!imageExt.Contains(ext))
            {
                continue;
            }

            var candidates = BuildCandidatePaths(token, projectDir);
            var existing = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                result.Add(existing);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildCandidatePaths(string token, string projectDir)
    {
        if (Path.IsPathRooted(token))
        {
            yield return token;
            yield break;
        }

        yield return Path.Combine(projectDir, token);
        yield return Path.Combine(projectDir, "datasets", token);
        yield return Path.Combine(projectDir, "datasets", "attachments", token);
    }

    private static string GetMimeType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".tif" => "image/tiff",
            ".tiff" => "image/tiff",
            ".heic" => "image/heic",
            _ => "application/octet-stream"
        };
    }

    private sealed class GeminiRequestEnvelope
    {
        [JsonPropertyName("contents")]
        public required List<GeminiContent> Contents { get; init; }
        [JsonPropertyName("generationConfig")]
        public GeminiGenerationConfig? GenerationConfig { get; init; }
    }

    private sealed class GeminiContent
    {
        [JsonPropertyName("parts")]
        public required List<GeminiPart> Parts { get; init; }
    }

    private sealed class GeminiPart
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }
        [JsonPropertyName("inlineData")]
        public GeminiInlineData? InlineData { get; init; }
    }

    private sealed class GeminiInlineData
    {
        [JsonPropertyName("mimeType")]
        public required string MimeType { get; init; }
        [JsonPropertyName("data")]
        public required string Data { get; init; }
    }

    private sealed class GeminiGenerationConfig
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; init; }
        [JsonPropertyName("responseMimeType")]
        public string? ResponseMimeType { get; init; }
    }

    private sealed class GeminiResponseEnvelope
    {
        [JsonPropertyName("candidates")]
        public List<GeminiCandidate>? Candidates { get; init; }
    }

    private sealed class GeminiCandidate
    {
        [JsonPropertyName("content")]
        public GeminiContentOut? Content { get; init; }
    }

    private sealed class GeminiContentOut
    {
        [JsonPropertyName("parts")]
        public List<GeminiPartOut>? Parts { get; init; }
    }

    private sealed class GeminiPartOut
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }

    private static AiMetadata WithSource(AiMetadata metadata, string source)
    {
        return metadata with { AnalysisSource = source };
    }

    private bool CanSendAnotherRequest()
    {
        lock (_stateLock)
        {
            return _requestsSent < _maxRequestsPerRun;
        }
    }

    private void MarkRequestSent()
    {
        lock (_stateLock)
        {
            _requestsSent++;
            if (_minDelayMs > 0)
            {
                _nextAllowedRequestAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(_minDelayMs);
            }
        }
    }

    private void SetBackoff(int seconds)
    {
        var delay = seconds <= 0 ? 15 : Math.Min(seconds, 120);
        lock (_stateLock)
        {
            _nextAllowedRequestAtUtc = DateTimeOffset.UtcNow.AddSeconds(delay);
        }
    }

    private async Task RespectRateLimitAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset until;
        lock (_stateLock)
        {
            until = _nextAllowedRequestAtUtc;
        }

        var now = DateTimeOffset.UtcNow;
        if (until > now)
        {
            var delay = until - now;
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task<string?> ResolveModelAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_modelResolutionAttempted)
            {
                return _resolvedModel;
            }
            _modelResolutionAttempted = true;
        }

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={_apiKey}";
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var models = await response.Content.ReadFromJsonAsync<GeminiModelsEnvelope>(cancellationToken: cancellationToken);
            var available = (models?.Models ?? [])
                .Select(m => m.Name?.Replace("models/", string.Empty))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var chosen = _models
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(available.Contains);

            lock (_stateLock)
            {
                _resolvedModel = chosen;
            }

            return chosen;
        }
        catch
        {
            return null;
        }
    }

    private static int ParseRetryAfterSeconds(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var raw = values.FirstOrDefault();
            if (int.TryParse(raw, out var seconds))
            {
                return seconds;
            }
        }

        return 0;
    }

    private async Task<HttpResponseMessage> SendGemmaAsync(string model, GeminiRequestEnvelope request, bool countAgainstBudget, CancellationToken cancellationToken)
    {
        await RespectRateLimitAsync(cancellationToken);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={_apiKey}";
        var response = await _httpClient.PostAsJsonAsync(url, request, cancellationToken);
        if (countAgainstBudget)
        {
            MarkRequestSent();
        }
        return response;
    }

    private sealed class GeminiModelsEnvelope
    {
        [JsonPropertyName("models")]
        public List<GeminiModelItem>? Models { get; init; }
    }

    private sealed class GeminiModelItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}
