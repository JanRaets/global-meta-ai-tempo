using Tempo.Core.Domain;

namespace Tempo.Core.Nudging;

/// <summary>Quiet-hours window test, shared by `notify.send` and the habit proposal batch
/// (nudging spec: proposals due in quiet hours defer to the next batch).</summary>
public static class QuietHours
{
    public static bool IsIn(TimeOnly nowLocal, UserSettings settings) =>
        settings.QuietHoursStart <= settings.QuietHoursEnd
            ? nowLocal >= settings.QuietHoursStart && nowLocal < settings.QuietHoursEnd
            : nowLocal >= settings.QuietHoursStart || nowLocal < settings.QuietHoursEnd;
}
