namespace KhajikiSort.Models;

public sealed record AiMetadata(
    RequestType RequestType,
    Tone Tone,
    int Priority,
    LanguageCode Language,
    string Summary,
    string Recommendation,
    string ImageAnalysis,
    string AnalysisSource
);
