namespace AppointmentsApi.Domain;

/// <summary>
/// When appointments can be booked. Times are in the clinic's local time: Georgia is UTC+4 all year
/// (no daylight saving since 2005), so a fixed offset is correct and avoids depending on time zone data.
/// </summary>
public sealed class ClinicSchedule
{
    public TimeSpan UtcOffset { get; init; } = TimeSpan.FromHours(4);

    public TimeOnly Opens { get; init; } = new(9, 0);

    public TimeOnly Closes { get; init; } = new(18, 0);

    public int SlotMinutes { get; init; } = 30;

    public IReadOnlySet<DayOfWeek> WorkingDays { get; init; } = new HashSet<DayOfWeek>
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
    };

    /// <summary>Returns null when the slot is bookable, otherwise the reason it is not.</summary>
    public string? Validate(DateTimeOffset startsAt, DateTimeOffset now)
    {
        var local = startsAt.ToOffset(UtcOffset);

        if (startsAt <= now) return "The slot must be in the future.";
        if (local.Second != 0 || local.Millisecond != 0 || local.Minute % SlotMinutes != 0)
            return $"Slots start every {SlotMinutes} minutes, on the hour or half hour.";
        if (!WorkingDays.Contains(local.DayOfWeek)) return "The clinic is closed on that day.";

        var time = TimeOnly.FromDateTime(local.DateTime);
        if (time < Opens || time.AddMinutes(SlotMinutes) > Closes || time.AddMinutes(SlotMinutes) < time)
            return $"Appointments are available between {Opens:HH\\:mm} and {Closes:HH\\:mm} (UTC{FormatOffset()}).";

        return null;
    }

    /// <summary>All slot start times on a local date, as UTC-offset timestamps.</summary>
    public IEnumerable<DateTimeOffset> SlotsOn(DateOnly date)
    {
        if (!WorkingDays.Contains(date.DayOfWeek)) yield break;
        for (var time = Opens; time.AddMinutes(SlotMinutes) <= Closes && time >= Opens; time = time.AddMinutes(SlotMinutes))
            yield return new DateTimeOffset(date.ToDateTime(time), UtcOffset);
    }

    public (DateTimeOffset Start, DateTimeOffset End) DayRange(DateOnly date)
    {
        var start = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), UtcOffset);
        return (start, start.AddDays(1));
    }

    private string FormatOffset() => (UtcOffset >= TimeSpan.Zero ? "+" : "-") + UtcOffset.ToString(@"hh\:mm");
}
