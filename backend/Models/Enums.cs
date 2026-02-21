namespace KhajikiSort.Models;

public enum RequestType
{
    Complaint,
    DataChange,
    Consultation,
    Claim,
    AppFailure,
    FraudulentActivity,
    Spam
}

public enum Tone
{
    Positive,
    Neutral,
    Negative
}

public enum LanguageCode
{
    RU,
    KZ,
    ENG
}
