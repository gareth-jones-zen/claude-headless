using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinBackground;

namespace ClaudeHeadlessTool;

/// <summary>When a job starts by itself (0.4.0), from a [[job.schedule]]: `on` a day rule, `at` a time of day (local),
/// with `input` fields added to the input it's given. The day rules:
///
///     daily, weekdays               every day; Monday to Friday
///     Wednesday                     every week, on that day
///     first Wednesday               every month: first, second, third, fourth or last, then the day
///     day 1                         every month, on that date (the month's last day if it's shorter)
/// </summary>
public sealed partial class ScheduleSpec
{
    private static readonly string[] Ordinals = ["first", "second", "third", "fourth"];

    private readonly Func<DateOnly, bool> matches;

    private ScheduleSpec(string on, TimeOnly at, IReadOnlyDictionary<string, string> input, Func<DateOnly, bool> matches)
    {
        On = on;
        At = at;
        Input = input;
        this.matches = matches;
    }

    public string On { get; }
    public TimeOnly At { get; }
    public IReadOnlyDictionary<string, string> Input { get; }

    public override string ToString() => $"{On} {At.ToString("HH:mm", CultureInfo.InvariantCulture)}";

    [GeneratedRegex(@"^\s*(?:(?<n>first|second|third|fourth|last)\s+)?(?<day>monday|tuesday|wednesday|thursday|friday|saturday|sunday)s?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DayRule();

    [GeneratedRegex(@"^\s*day\s+(?<d>\d{1,2})\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateRule();

    public static ScheduleSpec Parse(string on, string at, IReadOnlyDictionary<string, string>? input = null)
    {
        if (!TimeOnly.TryParseExact(at.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new FormatException($"[[job.schedule]] at should be a time of day, such as \"07:30\" (not \"{at}\")");
        var rule = on.Trim().ToLowerInvariant();
        Func<DateOnly, bool>? matches = rule switch
        {
            "daily" or "every day" => _ => true,
            "weekdays" => d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            _ => null,
        };
        if (matches is null && DayRule().Match(rule) is { Success: true } m)
        {
            var day = Enum.Parse<DayOfWeek>(m.Groups["day"].Value, ignoreCase: true);
            var n = m.Groups["n"].Success ? m.Groups["n"].Value.ToLowerInvariant() : null;
            matches = n switch
            {
                null => d => d.DayOfWeek == day,
                "last" => d => d.DayOfWeek == day && d.AddDays(7).Month != d.Month,
                _ => d => d.DayOfWeek == day && (d.Day - 1) / 7 == Array.IndexOf(Ordinals, n),
            };
        }
        if (matches is null && DateRule().Match(rule) is { Success: true } dm)
        {
            var date = int.Parse(dm.Groups["d"].Value, CultureInfo.InvariantCulture);
            if (date is < 1 or > 31)
                throw new FormatException($"[[job.schedule]] on: \"day {date}\" should be day 1 to day 31");
            matches = d => d.Day == Math.Min(date, DateTime.DaysInMonth(d.Year, d.Month));
        }
        if (matches is null)
            throw new FormatException($"[[job.schedule]] on should be daily, weekdays, a day (\"Wednesday\"), \"first Wednesday\" (first to fourth, or last) or \"day 1\" (not \"{on}\")");
        return new ScheduleSpec(on.Trim(), time, input ?? new Dictionary<string, string>(), matches);
    }

    public bool Matches(DateOnly day) => matches(day);

    /// <summary>The latest time it was due, at or before now (wall clock); null if none in the last year.</summary>
    public DateTime? Latest(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        for (var back = 0; back <= 400; back++)
        {
            var day = today.AddDays(-back);
            if (matches(day) && day.ToDateTime(At) <= now)
                return day.ToDateTime(At);
        }
        return null;
    }

    /// <summary>The next time it's due, after now (wall clock); null if none in the next year.</summary>
    public DateTime? Next(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        for (var ahead = 0; ahead <= 400; ahead++)
        {
            var day = today.AddDays(ahead);
            if (matches(day) && day.ToDateTime(At) > now)
                return day.ToDateTime(At);
        }
        return null;
    }
}

/// <summary>Which settings files' schedules run-queue checks, and when each schedule last started its job (0.4.0).
///
///     %LOCALAPPDATA%\claude-headless\schedules.json   {"settings": [path, ...], "last": {"path|job|n": iso}}
///     %LOCALAPPDATA%\claude-headless\scheduled\       the inputs it writes, deleted by each job when it's done
///
/// The queue task (claude-headless run-queue, every 15 minutes, at logon and after an unlock) is registered without
/// settings, so `schedule on` records the settings file here instead, and no new task is needed. A schedule seen for
/// the first time starts from now: nothing that fell due before it was switched on is run.</summary>
public static class Schedules
{
    public static string StateFile(string home) => Path.Combine(home, "schedules.json");

    private static string LockName(string home) => $"claude-headless-schedules-{Locks.Key(home)}";

    private static string Key(string settings, string job, int n) => $"{settings}|{job}|{n}";

    internal static string Iso(DateTime wall) =>
        new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private sealed record State(List<string> Settings, Dictionary<string, string> Last);

    private static State Read(string home)
    {
        try
        {
            if (File.Exists(StateFile(home)) && JsonNode.Parse(File.ReadAllText(StateFile(home), Encoding.UTF8)) is JsonObject o)
                return new State(
                    o["settings"] is JsonArray s ? s.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : [],
                    o["last"] is JsonObject l ? l.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "") : []);
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
        }
        return new State([], []);
    }

    private static void Write(string home, State state)
    {
        Directory.CreateDirectory(home);
        var last = new JsonObject();
        foreach (var (key, value) in state.Last.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            last[key] = value;
        var data = new JsonObject
        {
            ["settings"] = new JsonArray(state.Settings.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["last"] = last,
        };
        var temp = $"{StateFile(home)}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, StateFile(home), overwrite: true);
    }

    private static DateTime? Parse(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.LocalDateTime : null;

    /// <summary>The settings files recorded by `schedule on`.</summary>
    public static IReadOnlyList<string> Recorded(string home) => Read(home).Settings;

    /// <summary>`schedule on`: record the settings file, and start its schedules from now.</summary>
    public static void On(string home, JobsConfig settings, DateTime now)
    {
        using var held = Locks.Exclusive(LockName(home), 60, "schedule check");
        var state = Read(home);
        if (!state.Settings.Contains(settings.Path, StringComparer.OrdinalIgnoreCase))
            state.Settings.Add(settings.Path);
        foreach (var job in settings.Kinds.Values)
            for (var n = 0; n < job.Schedules.Count; n++)
                state.Last.TryAdd(Key(settings.Path, job.Name, n), Iso(now));
        Write(home, state);
    }

    /// <summary>`schedule off`: forget the settings file and its schedules. False if it wasn't recorded.</summary>
    public static bool Off(string home, string settingsPath)
    {
        using var held = Locks.Exclusive(LockName(home), 60, "schedule check");
        var state = Read(home);
        var removed = state.Settings.RemoveAll(s => string.Equals(s, settingsPath, StringComparison.OrdinalIgnoreCase)) > 0;
        foreach (var key in state.Last.Keys.Where(k => k.StartsWith(settingsPath + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            state.Last.Remove(key);
        Write(home, state);
        return removed;
    }

    /// <summary>When the schedule last started its job (or was switched on), if it has.</summary>
    public static DateTime? Last(string home, string settingsPath, string job, int n) =>
        Read(home).Last.TryGetValue(Key(settingsPath, job, n), out var iso) ? Parse(iso) : null;

    /// <summary>run-queue's check: for each recorded settings file (and `extra`, the --config given, if any), each
    /// schedule that has fallen due since it last started its job is queued once (however many times it fell due
    /// meanwhile), with an input {"received": when it was due, "queued": now, "schedule": "first Wednesday 07:30",
    /// plus its input fields}. Returns how many jobs it queued. A settings file it can't read is logged and skipped.</summary>
    public static int Check(string home, string? extra, DateTime now, Func<string, string?> env, Func<string> exe,
        Func<IEnumerable<string>, string, bool> queue, Action<string> log)
    {
        using var held = Locks.Exclusive(LockName(home), 60, "schedule check");
        var state = Read(home);
        var files = state.Settings.ToList();
        if (extra is not null && !files.Contains(extra, StringComparer.OrdinalIgnoreCase))
            files.Add(extra);
        var queued = 0;
        var changed = false;
        foreach (var file in files)
        {
            JobsConfig settings;
            try
            {
                settings = JobsConfig.Load(file, env);
            }
            catch (Exception error)
            {
                log($"schedule: couldn't read {file}: {error.Message}");
                continue;
            }
            foreach (var job in settings.Kinds.Values)
                for (var n = 0; n < job.Schedules.Count; n++)
                {
                    var spec = job.Schedules[n];
                    var key = Key(settings.Path, job.Name, n);
                    if (!state.Last.TryGetValue(key, out var lastIso) || Parse(lastIso) is not { } last)
                    {
                        state.Last[key] = Iso(now);   // new: from now on
                        changed = true;
                        continue;
                    }
                    if (spec.Latest(now) is not { } due || due <= last)
                        continue;
                    var input = new JsonObject();
                    foreach (var (name, value) in spec.Input)
                        input[name] = value;
                    input["received"] = Iso(due);
                    input["queued"] = Iso(now);
                    input["schedule"] = spec.ToString();
                    var folder = Path.Combine(home, "scheduled");
                    Directory.CreateDirectory(folder);
                    var path = Path.Combine(folder, $"{job.Name}-{due:yyyyMMdd-HHmm}.json");
                    File.WriteAllText(path, input.ToJsonString(), new UTF8Encoding(false));
                    queue([exe(), "--config", settings.Path, "job", job.Name, "--input", path], settings.Dir);
                    state.Last[key] = Iso(now);
                    changed = true;
                    queued++;
                    log($"schedule: queued job {job.Name} ({spec}), due {due:ddd d MMM HH:mm}");
                }
        }
        if (changed)
            Write(home, state);
        return queued;
    }
}
