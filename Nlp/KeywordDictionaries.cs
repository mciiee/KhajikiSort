using KhajikiSort.Models;

namespace KhajikiSort.Nlp;

internal static class KeywordDictionaries
{
    public static readonly Dictionary<LanguageCode, HashSet<string>> LanguageMarkers = new()
    {
        [LanguageCode.RU] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "здравствуйте", "прошу", "ошибка", "приложение", "данные", "жалоба", "мошенничество", "консультация",
            "zdravstvuite", "pozhaluista", "proshu", "oshibka", "prilozhenie", "dannie", "zhaloba", "moshennichestvo",
            "konsultaciya", "srochno", "ne rabotaet", "ne mogu voiti", "smena nomera", "izmenit dannye",
            "na schet", "ne prishlo", "vernite dengi", "vozvrat", "deneg net", "platezh"
        },
        [LanguageCode.KZ] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "сәлем", "сәлеметсіз", "сәлеметсізбе", "өтінемін", "қате", "қосымша", "деректер", "шағым", "алаяқтық", "кеңес",
            "мекенжай", "нөмір", "көмек", "құрметпен", "рахмет", "өзгерту", "ауыстыру", "қазақша",
            "salem", "salemetsiz", "otinemin", "kate", "kosymsha", "derekter", "shagym", "alaiaktyk", "kenes",
            "mekenzhai", "nomir", "komek", "kurmetpen", "rakhmet", "ozgertu", "auystyru", "kazaksha",
            "men", "ruyxatdan", "utolmayapman", "otolmayapman", "sabab", "nima", "akkaunt", "kiralmai", "tirkeu"
        },
        [LanguageCode.ENG] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hello", "please", "error", "application", "data", "complaint", "fraud", "consultation"
        }
    };

    public static readonly Dictionary<LanguageCode, Dictionary<RequestType, string[]>> RequestTypeKeywords = new()
    {
        [LanguageCode.RU] = new Dictionary<RequestType, string[]>
        {
            [RequestType.Complaint] = ["жалоба", "возмущен", "недоволен", "ужасный", "плохой сервис", "жалуюсь", "безобразие", "vozmushen", "zhaloba"],
            [RequestType.DataChange] = ["смена данных", "изменить данные", "обновить номер", "сменить адрес", "изменить телефон", "smena dannyh", "smena nomera", "izmenit dannye", "izmenit telefon"],
            [RequestType.Consultation] = ["подскажите", "консультация", "как оформить", "какие условия", "уточнить", "vopros", "pomogite razobratsya"],
            [RequestType.Claim] = ["претензия", "требую", "компенсация", "возмещение", "нарушены права", "не пришло на счет", "деньги не пришли", "верните деньги", "возврат средств", "не зачислены", "125$", "refund", "chargeback"],
            [RequestType.AppFailure] = ["не работает приложение", "ошибка", "вылетает", "не могу войти", "bug", "oshibka", "ne rabotaet", "ne mogu voiti", "viletayet", "не могу зарегистрироваться", "не получается войти", "не приходит смс", "код не приходит"],
            [RequestType.FraudulentActivity] = ["мошенничество", "подозрительная операция", "украли", "взлом", "fraud", "moshennichestvo", "podozritelnaya operaciya", "vzlom", "ukrali"],
            [RequestType.Spam] = ["реклама", "спам", "ненужная рассылка", "подписка без согласия", "акция только сегодня", "reklama", "spam", "navyazchivaya rassylka"]
        },
        [LanguageCode.KZ] = new Dictionary<RequestType, string[]>
        {
            [RequestType.Complaint] = ["шағым", "наразы", "қызмет нашар", "өте жаман", "ренжідім", "narazy", "kyzmet nashar"],
            [RequestType.DataChange] = ["деректерді өзгерту", "телефонды өзгерту", "мекенжайды өзгерту", "жаңарту", "мекенжай", "телефон", "ауыстыр", "derekterdi ozgertu", "telefondy ozgertu", "mekenzhaidy ozgertu", "auystyr"],
            [RequestType.Consultation] = ["кеңес", "түсіндіріп беріңіз", "шарттары қандай", "қалай рәсімдеймін", "kalai", "suraq"],
            [RequestType.Claim] = ["талап", "өтемақы", "залалды өтеу", "құқық бұзылды", "претензия", "aqsha tuspedi", "aqsha kelmedi", "aksha kelmedi", "kaitaryp beriniz", "dengi kelmedi"],
            [RequestType.AppFailure] = ["қосымша жұмыс істемейді", "қате", "кіре алмаймын", "істен шықты", "kosymsha zhymys istemeidi", "kate", "kire almaimyn", "ruyxatdan utolmayapman", "ruyxatdan otolmayapman", "sabab nima", "akkauntqa kiralmadym"],
            [RequestType.FraudulentActivity] = ["алаяқтық", "күдікті операция", "шот бұзылды", "ақша жоғалды", "alaiaktyk", "kudikti operaciya", "shot buzildy", "aksha zhogaldy"],
            [RequestType.Spam] = ["спам", "жарнама", "қажетсіз хабарлама", "келісімсіз жазылу", "zharnama", "kazhetsiz habarlama", "kelisimsiz zhazylu"]
        },
        [LanguageCode.ENG] = new Dictionary<RequestType, string[]>
        {
            [RequestType.Complaint] = ["complaint", "unhappy", "disappointed", "bad service", "frustrated"],
            [RequestType.DataChange] = ["change data", "update phone", "update address", "change personal info"],
            [RequestType.Consultation] = ["consultation", "please advise", "how can i", "what are the terms", "need clarification"],
            [RequestType.Claim] = ["claim", "compensation", "reimbursement", "rights violated", "money not received", "funds not credited", "return my money", "refund my money", "125$", "did not arrive"],
            [RequestType.AppFailure] = ["app failure", "application not working", "error", "cannot login", "crash", "cannot register", "signup failed", "verification code not received"],
            [RequestType.FraudulentActivity] = ["fraud", "suspicious transfer", "unauthorized", "account hacked", "scam"],
            [RequestType.Spam] = ["spam", "unsolicited", "junk message", "unwanted marketing"]
        }
    };

    public static readonly Dictionary<LanguageCode, string[]> PositiveToneKeywords = new()
    {
        [LanguageCode.RU] = ["спасибо", "благодарю", "доволен", "отлично", "приятно", "spasibo", "blagodaryu", "otlichno"],
        [LanguageCode.KZ] = ["рахмет", "қуаныштымын", "өте жақсы", "ризамын", "rakhmet", "kuanyshtymyn", "ote zhaksy", "rizamyn"],
        [LanguageCode.ENG] = ["thank you", "great", "happy", "satisfied", "excellent"]
    };

    public static readonly Dictionary<LanguageCode, string[]> NegativeToneKeywords = new()
    {
        [LanguageCode.RU] = ["ужасно", "возмущен", "недоволен", "срочно", "проблема", "невозможно", "uzhasno", "vozmushen", "nedovolen", "srochno", "problema", "верните", "не пришло", "не зачислено"],
        [LanguageCode.KZ] = ["нашар", "наразы", "шұғыл", "мәселе", "мүмкін емес", "nashar", "narazy", "shugyl", "masele", "mumkin emes", "kelmedi", "utolmayapman", "sabab nima"],
        [LanguageCode.ENG] = ["angry", "urgent", "bad", "issue", "unacceptable", "impossible", "return my money", "not received"]
    };
}
