using System.Text.RegularExpressions;
using KhajikiSort.Models;

namespace KhajikiSort.Nlp;

public sealed class NlpMetadataExtractor
{
    public AiMetadata Extract(string text, string? attachmentContext = null)
    {
        var effectiveText = string.IsNullOrWhiteSpace(attachmentContext) ? text : $"{text}\n{attachmentContext}";
        var normalized = Normalize(effectiveText);
        var language = DetectLanguage(normalized);
        var requestType = DetectRequestType(normalized, language);
        var tone = DetectTone(normalized, language);
        var priority = CalculatePriority(requestType, tone);
        var summary = BuildSummary(text, requestType, tone, language);
        var recommendation = BuildRecommendation(requestType, language);

        return new AiMetadata(requestType, tone, priority, language, summary, recommendation, string.Empty, "RulesFallback");
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
        var kzUniqueCharCount = normalizedText.Count(c => KeywordDictionaries.KazakhSpecificLetters.Contains(c));
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
            if (KeywordDictionaries.KazakhLatinSignals.Contains(token))
            {
                scores[LanguageCode.KZ] += 2;
            }

            if (KeywordDictionaries.RussianLatinSignals.Contains(token))
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
        var hardOverride = DetectHardOverrideRequestType(normalizedText);
        if (hardOverride is not null)
        {
            return hardOverride.Value;
        }

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
        if (fallback.Value == 0)
        {
            var secondary = DetectSecondaryOverrideRequestType(normalizedText);
            return secondary ?? RequestType.Consultation;
        }

        if (fallback.Key == RequestType.Consultation)
        {
            var secondary = DetectSecondaryOverrideRequestType(normalizedText);
            return secondary ?? fallback.Key;
        }

        return fallback.Key;
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

    private static string BuildSummary(string originalText, RequestType requestType, Tone tone, LanguageCode language)
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

        return language switch
        {
            LanguageCode.RU => $"Сообщение клиента: {sentence}. Класс: {ToRuRequestType(requestType)}, тон: {ToRuTone(tone)}.",
            LanguageCode.KZ => $"Клиент хабары: {sentence}. Санаты: {ToKzRequestType(requestType)}, тон: {ToKzTone(tone)}.",
            _ => $"Client message: {sentence}. Classified as {requestType} with {tone} tone."
        };
    }

    private static string BuildRecommendation(RequestType requestType, LanguageCode language)
    {
        return language switch
        {
            LanguageCode.RU => requestType switch
            {
                RequestType.FraudulentActivity => "Срочно эскалируйте в антифрод и временно ограничьте рискованные операции до проверки.",
                RequestType.AppFailure => "Откройте техинцидент, соберите логи/скриншоты и дайте клиенту временный обходной путь.",
                RequestType.Claim => "Запустите процесс претензии и зафиксируйте срок ответа по регламенту.",
                RequestType.Complaint => "Назначьте ответственному менеджеру и дайте эмпатичный ответ с конкретными шагами решения.",
                RequestType.DataChange => "Проверьте личность клиента и выполните изменение данных по процедуре комплаенса.",
                RequestType.Consultation => "Передайте профильному менеджеру и отправьте понятные материалы/FAQ.",
                RequestType.Spam => "Пометьте как спам и исключите из операционной очереди менеджеров.",
                _ => "Передайте на ручную первичную проверку."
            },
            LanguageCode.KZ => requestType switch
            {
                RequestType.FraudulentActivity => "Anti-fraud тобына дереу жіберіп, тексеріс біткенше тәуекел операцияларын шектеңіз.",
                RequestType.AppFailure => "Техникалық инцидент ашып, лог/скриншот жинап, клиентке уақытша шешім беріңіз.",
                RequestType.Claim => "Ресми шағым процесін бастап, жауап мерзімін регламент бойынша бекітіңіз.",
                RequestType.Complaint => "Жауапты менеджерге беріп, нақты шешу қадамдарымен эмпатиялық жауап дайындаңыз.",
                RequestType.DataChange => "Клиентті верификациялап, дерек өзгерісін комплаенс талабымен орындаңыз.",
                RequestType.Consultation => "Тиісті маманға бағыттап, қысқа әрі нақты FAQ/нұсқаулық беріңіз.",
                RequestType.Spam => "Спам ретінде белгілеп, менеджер кезегінен алып тастаңыз.",
                _ => "Қолмен бастапқы тексеруге жіберіңіз."
            },
            _ => requestType switch
            {
                RequestType.FraudulentActivity => "Escalate immediately to anti-fraud and freeze risky operations pending verification.",
                RequestType.AppFailure => "Open a technical incident, collect logs/screenshots, and provide a workaround to the client.",
                RequestType.Claim => "Create a formal claim workflow and set response deadline according to policy.",
                RequestType.Complaint => "Assign to a responsible manager and provide an empathy-first response with concrete steps.",
                RequestType.DataChange => "Verify identity, confirm requested fields, and process the data update under compliance rules.",
                RequestType.Consultation => "Route to an advisor with relevant product expertise and share concise FAQ guidance.",
                RequestType.Spam => "Mark as spam and suppress from manager workload.",
                _ => "Route to first-line support for manual triage."
            }
        };
    }

    private static string ToRuRequestType(RequestType requestType) => requestType switch
    {
        RequestType.Complaint => "Жалоба",
        RequestType.DataChange => "Смена данных",
        RequestType.Consultation => "Консультация",
        RequestType.Claim => "Претензия",
        RequestType.AppFailure => "Неработоспособность приложения",
        RequestType.FraudulentActivity => "Мошеннические действия",
        RequestType.Spam => "Спам",
        _ => requestType.ToString()
    };

    private static string ToRuTone(Tone tone) => tone switch
    {
        Tone.Positive => "Позитивный",
        Tone.Negative => "Негативный",
        _ => "Нейтральный"
    };

    private static string ToKzRequestType(RequestType requestType) => requestType switch
    {
        RequestType.Complaint => "Шағым",
        RequestType.DataChange => "Дерек өзгерту",
        RequestType.Consultation => "Кеңес",
        RequestType.Claim => "Талап/Претензия",
        RequestType.AppFailure => "Қосымша істемейді",
        RequestType.FraudulentActivity => "Алаяқтық әрекеттер",
        RequestType.Spam => "Спам",
        _ => requestType.ToString()
    };

    private static string ToKzTone(Tone tone) => tone switch
    {
        Tone.Positive => "Позитивті",
        Tone.Negative => "Негативті",
        _ => "Бейтарап"
    };

    private static RequestType? DetectHardOverrideRequestType(string normalizedText)
    {
        var hasLink = ContainsAny(normalizedText, "http", "https", "utm_", "safelinks", "protection outlook");
        var hasPromo = ContainsAny(normalizedText,
            "выгодное предложение", "в наличии", "акция", "скидк", "купить", "реклама",
            "promo", "special offer", "marketing", "advertis");
        if (hasLink && hasPromo)
        {
            return RequestType.Spam;
        }

        if (ContainsAny(normalizedText,
                "спам", "spam", "junk", "unsolicited", "реклама", "акция", "скидка", "buy now", "promo"))
        {
            return RequestType.Spam;
        }

        if (ContainsAny(normalizedText,
                "мошенн", "fraud", "scam", "unauthorized", "подозр", "взлом", "ukrali", "алаяқ", "alaiak"))
        {
            return RequestType.FraudulentActivity;
        }

        if (ContainsAny(normalizedText,
                "не работает", "ошибка", "вылет", "не могу войти", "cannot login", "cannot register", "blocked in app",
                "код не приходит", "sms не приходит", "ruyxatdan utolmayapman", "app failure", "application not working"))
        {
            return RequestType.AppFailure;
        }

        if (ContainsAny(normalizedText,
                "верните деньги", "refund", "не пришло на счет", "не зачис", "компенса", "претенз", "chargeback", "money not received"))
        {
            return RequestType.Claim;
        }

        if (ContainsAny(normalizedText,
                "смена данных", "изменить", "обновить номер", "телефонды өзгерту", "мекенжайды өзгерту", "derekterdi ozgertu", "update phone", "change data"))
        {
            return RequestType.DataChange;
        }

        return null;
    }

    private static RequestType? DetectSecondaryOverrideRequestType(string normalizedText)
    {
        if (ContainsAny(normalizedText,
                "жалоб", "недовол", "возмущ", "ужасн", "суд подам", "не имеете права", "unhappy", "frustrated"))
        {
            return RequestType.Complaint;
        }

        return null;
    }

    private static bool ContainsAny(string text, params string[] probes)
    {
        return probes.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
    }
}
