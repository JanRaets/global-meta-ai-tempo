namespace Tempo.Core.Domain;

/// <summary>
/// The closed set for ContextKey.AdjacentAppointmentKind (design D5, defined 27 Oct 2026):
/// meeting | personal | medical | travel | unknown, derived deterministically from the
/// appointment title. A visit with no adjacent appointment keeps null (matches any kind).
/// </summary>
public static class AppointmentKind
{
    public const string Meeting = "meeting";
    public const string Personal = "personal";
    public const string Medical = "medical";
    public const string Travel = "travel";
    public const string Unknown = "unknown";

    private static readonly (string Kind, string[] Keywords)[] Rules =
    [
        (Medical, ["dentist", "tandarts", "doctor", "huisarts", "arts", "fysio", "hospital", "ziekenhuis", "clinic", "prik", "apotheek"]),
        (Travel, ["flight", "vlucht", "airport", "luchthaven", "check-in", "boarding"]),
        (Meeting, ["meeting", "overleg", "vergadering", "call", "standup", "stand-up", "sync", "1:1", "review", "demo", "interview", "presentatie", "workshop"]),
        (Personal, ["dinner", "diner", "lunch", "borrel", "drinks", "coffee", "koffie", "birthday", "verjaardag", "party", "feest", "gym", "sport", "hockey", "training", "badminton", "voetbal", "quiz", "concert", "film", "kapper"]),
    ];

    public static string Classify(string title)
    {
        foreach (var (kind, keywords) in Rules)
        {
            if (keywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase)))
                return kind;
        }
        return Unknown;
    }
}
