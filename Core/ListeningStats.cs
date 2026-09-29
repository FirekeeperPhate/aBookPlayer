namespace aBookPlayer;

/// <summary>The seconds listened per day ("yyyy-MM-dd", local date): what the statistics are computed from.</summary>
interface IListeningHistory
{
    Dictionary<string, double> ListeningDays { get; }
}

/// <summary>The numbers behind the statistics window (kept apart so they can be tested).</summary>
static class ListeningStats
{
    /// <summary>Days kept: a bit more than a year.</summary>
    const int KeptDays = 400;

    static string Key(DateTime day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static void Add(IListeningHistory settings, DateTime when, double seconds)
    {
        var key = Key(when);
        settings.ListeningDays[key] = settings.ListeningDays.GetValueOrDefault(key) + seconds;
        if (settings.ListeningDays.Count > KeptDays)
            foreach (var old in settings.ListeningDays.Keys.Order(StringComparer.Ordinal).Take(settings.ListeningDays.Count - KeptDays).ToList())
                settings.ListeningDays.Remove(old);
    }

    public static TimeSpan Day(IListeningHistory settings, DateTime day) =>
        TimeSpan.FromSeconds(settings.ListeningDays.GetValueOrDefault(Key(day)));

    /// <summary>The last <paramref name="days"/> days, today included.</summary>
    public static TimeSpan LastDays(IListeningHistory settings, DateTime today, int days) =>
        TimeSpan.FromSeconds(Enumerable.Range(0, days).Sum(i => settings.ListeningDays.GetValueOrDefault(Key(today.AddDays(-i)))));

    public static TimeSpan Total(IListeningHistory settings) => TimeSpan.FromSeconds(settings.ListeningDays.Values.Sum());

    /// <summary>
    /// Days in a row reaching the goal (with no goal, listening at least a minute), up to today. Today counts once
    /// reached, and does not break the streak while it can still be reached.
    /// </summary>
    public static int Streak(IListeningHistory settings, DateTime today, int goalMinutes)
    {
        var need = TimeSpan.FromMinutes(Math.Max(1, goalMinutes));
        var day = today.Date;
        if (Day(settings, day) < need) day = day.AddDays(-1);
        int days = 0;
        while (Day(settings, day) >= need)
        {
            days++;
            day = day.AddDays(-1);
        }
        return days;
    }

    /// <summary>The longest streak in the days kept.</summary>
    public static int BestStreak(IListeningHistory settings, int goalMinutes)
    {
        var need = TimeSpan.FromMinutes(Math.Max(1, goalMinutes)).TotalSeconds;
        var reached = settings.ListeningDays.Where(d => d.Value >= need)
            .Select(d => DateTime.ParseExact(d.Key, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Order().ToList();
        int best = 0, run = 0;
        for (int i = 0; i < reached.Count; i++)
        {
            run = i > 0 && reached[i] == reached[i - 1].AddDays(1) ? run + 1 : 1;
            best = Math.Max(best, run);
        }
        return best;
    }

    /// <summary>
    /// When the book will be finished at the recent pace (the average of the last 14 days, days off included);
    /// null without enough listening to tell.
    /// </summary>
    public static DateTime? EstimatedFinish(IListeningHistory settings, DateTime today, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return today.Date;
        var perDay = LastDays(settings, today, 14) / 14;
        if (perDay < TimeSpan.FromMinutes(1)) return null;
        return today.Date.AddDays(Math.Ceiling(remaining / perDay) - 1);
    }
}
