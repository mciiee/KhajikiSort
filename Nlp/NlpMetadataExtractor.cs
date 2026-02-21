using System.Text.RegularExpressions;
using KhajikiSort.Models;

namespace KhajikiSort.Nlp;

public sealed class NlpMetadataExtractor
{
    private static readonly HashSet<char> KazakhSpecificLetters = new()
    {
        'ә', 'ғ', 'қ', 'ң', 'ө', 'ұ', 'ү', 'һ', 'і'
    };
    private static readonly HashSet<string> KazakhLatinSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        "salem", "salemetsiz", "otinemin", "kazaksha", "auystyr", "ozgertu", "derekter", "mekenzhai", "komek", "rakhmet", "zhane", "ushin",
        "men", "ruyxatdan", "utolmayapman", "otolmayapman", "sabab", "nima", "kiralmadym", "akkauntqa", "tirkelu"
    };
    private static readonly HashSet<string> RussianLatinSignals = new(StringComparer.OrdinalIgnoreCase)
    {
        "zdravstvuite", "pozhaluista", "proshu", "srochno", "oshibka", "prilozhenie", "zhaloba", "moshennichestvo", "spasibo", "nedovolen",
        "schet", "prishlo", "vernite", "dengi", "vozvrat", "zachisleno"
    };

    public AiMetadata Extract(string text, string? attachmentContext = null)
    {
        var effectiveText = string.IsNullOrWhiteSpace(attachmentContext) ? text : $"{text}\n{attachmentContext}";
        var normalized = Normalize(effectiveText);
        var language = DetectLanguage(normalized);
        var requestType = DetectRequestType(normalized, language);
        var tone = DetectTone(normalized, language);
        var priority = CalculatePriority(requestType, tone);
        var summary = BuildSummary(text, requestType, tone);
        var recommendation = BuildRecommendation(requestType);

        return new AiMetadata(requestType, tone, priority, language, summary, recommendation);
    }

    private static string Normalize(string text)
    {
        var lower = text.ToLowerInvariant();
        return Regex.Replace(lower, @"[^\p{L}\p{Nd}\s]", " ");
    }

    private static LanguageCode DetectLanguage(string normalizedText)
    {
        var scores = new Dictionary<LanguageCode, int>
        {
            [LanguageCode.RU] = 0,
            [LanguageCode.KZ] = 0,
            [LanguageCode.ENG] = 0
        };

        // Strong signal for KZ: unique Kazakh Cyrillic letters.
        var kzUniqueCharCount = normalizedText.Count(c => KazakhSpecificLetters.Contains(c));
        if (kzUniqueCharCount > 0)
        {
            scores[LanguageCode.KZ] += kzUniqueCharCount * 3;
        }

        var engLetterCount = normalizedText.Count(c => c is >= 'a' and <= 'z');
        if (engLetterCount > 0)
        {
            scores[LanguageCode.ENG] += engLetterCount / 8;
        }

        var tokens = normalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            if (KazakhLatinSignals.Contains(token))
            {
                scores[LanguageCode.KZ] += 2;
            }

            if (RussianLatinSignals.Contains(token))
            {
                scores[LanguageCode.RU] += 2;
            }
        }

        foreach (var (lang, markers) in KeywordDictionaries.LanguageMarkers)
        {
            foreach (var marker in markers)
            {
                if (normalizedText.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    scores[lang]++;
                }

                if (tokens.Contains(marker, StringComparer.OrdinalIgnoreCase))
                {
                    scores[lang] += 2;
                }
            }
        }

        var detected = scores.OrderByDescending(x => x.Value).First();
        return detected.Value == 0 ? LanguageCode.RU : detected.Key;
    }

    private static RequestType DetectRequestType(string normalizedText, LanguageCode language)
    {
        var langKeywords = KeywordDictionaries.RequestTypeKeywords[language];
        var scores = new Dictionary<RequestType, int>();

        foreach (var (type, keywords) in langKeywords)
        {
            var score = keywords.Count(k => normalizedText.Contains(k, StringComparison.OrdinalIgnoreCase));
            scores[type] = score;
        }

        var best = scores.OrderByDescending(x => x.Value).First();
        if (best.Value > 0)
        {
            return best.Key;
        }

        // Fallback across all languages to avoid losing intent on mixed text.
        foreach (var perLanguage in KeywordDictionaries.RequestTypeKeywords.Values)
        {
            foreach (var (type, keywords) in perLanguage)
            {
                if (!scores.ContainsKey(type))
                {
                    scores[type] = 0;
                }

                scores[type] += keywords.Count(k => normalizedText.Contains(k, StringComparison.OrdinalIgnoreCase));
            }
        }

        var fallback = scores.OrderByDescending(x => x.Value).First();
        return fallback.Value == 0 ? RequestType.Consultation : fallback.Key;
    }

    private static Tone DetectTone(string normalizedText, LanguageCode language)
    {
        var positive = KeywordDictionaries.PositiveToneKeywords[language]
            .Count(word => normalizedText.Contains(word, StringComparison.OrdinalIgnoreCase));
        var negative = KeywordDictionaries.NegativeToneKeywords[language]
            .Count(word => normalizedText.Contains(word, StringComparison.OrdinalIgnoreCase));

        if (negative > positive)
        {
            return Tone.Negative;
        }

        if (positive > negative)
        {
            return Tone.Positive;
        }

        return Tone.Neutral;
    }

    private static int CalculatePriority(RequestType requestType, Tone tone)
    {
        var basePriority = requestType switch
        {
            RequestType.FraudulentActivity => 10,
            RequestType.AppFailure => 8,
            RequestType.Claim => 7,
            RequestType.Complaint => 6,
            RequestType.DataChange => 5,
            RequestType.Consultation => 4,
            RequestType.Spam => 1,
            _ => 4
        };

        var adjusted = tone switch
        {
            Tone.Negative => basePriority + 1,
            Tone.Positive => basePriority - 1,
            _ => basePriority
        };

        return Math.Clamp(adjusted, 1, 10);
    }

    private static string BuildSummary(string originalText, RequestType requestType, Tone tone)
    {
        var sentence = originalText
            .Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        sentence ??= originalText.Trim();
        if (string.IsNullOrWhiteSpace(sentence))
        {
            sentence = "No text body provided; check attachments.";
        }

        if (sentence.Length > 180)
        {
            sentence = sentence[..180] + "...";
        }

        return $"Client message: {sentence}. Classified as {requestType} with {tone} tone.";
    }

    private static string BuildRecommendation(RequestType requestType)
    {
        return requestType switch
        {
            RequestType.FraudulentActivity => "Escalate immediately to anti-fraud team and freeze risky operations for verification.",
            RequestType.AppFailure => "Open technical incident, collect logs/screenshots, and provide workaround to the client.",
            RequestType.Claim => "Create formal claim workflow and set response deadline according to policy.",
            RequestType.Complaint => "Assign to responsible manager and provide empathy-first response with concrete resolution steps.",
            RequestType.DataChange => "Verify identity, confirm requested fields, then process data update under compliance rules.",
            RequestType.Consultation => "Route to advisor with relevant product expertise and send clear FAQ links.",
            RequestType.Spam => "Mark as spam, suppress sender/channel if policy allows, and avoid manager workload.",
            _ => "Route to first-line support for manual triage."
        };
    }
}
