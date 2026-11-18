using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PyCompat;
using RepoCommit;
using Tomlyn;
using Tomlyn.Model;

namespace ClaudeHeadlessTool;

/// <summary>Settings that can't be used, or a request that can't be met: exit 2.</summary>
public class UsageException(string message) : Exception(message);

/// <summary>Where a job's date comes from: a pattern on an input field ("13/11" in a subject: day, then month; the
/// year that puts it nearest before the input's received date), otherwise the received date.</summary>
public sealed record DateRule(string Field, Regex Pattern, string Otherwise);

/// <summary>One [[job]] kind: take an input, give it to Claude with a prompt, check the reply, put the output here.</summary>
public sealed class JobKind
{
    public required string Name { get; init; }
    public required string Prompt { get; init; }                                  // a template file
    public IReadOnlyDictionary<string, string> Context { get; init; } = new Dictionary<string, string>();   // name: file
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();    // name: text
    public DateRule? Date { get; init; }
    public required string Output { get; init; }                                  // a path template
    public required string Work { get; init; }                                    // Claude's folder: empty
    public IReadOnlyList<string> Claude { get; init; } = [];
    public int TimeoutMinutes { get; init; } = 15;
    public IReadOnlyList<(string Name, bool Required)> Sections { get; init; } = [];
    public string? Body { get; init; }
    public IReadOnlyList<Regex> MustMatch { get; init; } = [];
    public IReadOnlyList<string> EmptyItems { get; init; } = ["none", "nothing"];
    public IReadOnlyList<(string From, string To)> Replace { get; init; } = [];
    public IReadOnlyList<string> Frontmatter { get; init; } = [];
    public string? Marker { get; init; }
    public string? Commit { get; init; }
    public string? CommitBody { get; init; }
    public ReviewTemplate? Review { get; init; }
    public ReviewTemplate? Failed { get; init; }
    public bool DeleteInput { get; init; } = true;
}

/// <summary>A queue item to add: source, type, and fields, each a template.</summary>
public sealed record ReviewTemplate(string Source, string Type, IReadOnlyList<(string Key, string Value)> Fields);

/// <summary>config/claude-headless.toml: the job kinds, and the shared [git] and [review] they use.</summary>
public sealed class JobsConfig
{
    public static readonly string[] KnownKeys = ["job", "git", "review", "log_file"];

    public required string Path { get; init; }
    public required string Dir { get; init; }
    public required IReadOnlyDictionary<string, JobKind> Kinds { get; init; }
    public GitSettings? Git { get; init; }
    public string? ReviewFile { get; init; }
    public string? ReviewLock { get; init; }
    public string? LogFile { get; init; }

    public static JobsConfig Load(string path, Func<string, string?> env)
    {
        var full = PyPath.Resolve(path);
        if (!File.Exists(full))
            throw new UsageException($"No settings file at {full}.");
        TomlTable raw;
        try
        {
            var text = PyStr.ReadText(full);
            if (text.StartsWith('﻿'))
                text = text[1..];
            var read = BaseSettings.Read(text, full, env, KnownKeys);
            raw = read.Raw;
            env = read.Env;
        }
        catch (TomlException error)
        {
            throw new UsageException($"{full}: {error.Message.TrimEnd()}");
        }
        catch (FormatException error)
        {
            throw new UsageException(error.Message.TrimEnd());
        }
        foreach (var key in raw.Keys)
            if (!KnownKeys.Contains(key))
                throw new UsageException($"{full}: unknown setting '{key}' (known: {string.Join(", ", KnownKeys)}).");
        var dir = System.IO.Path.GetDirectoryName(full)!;
        var vars = env;
        string Resolve(string value)
        {
            var expanded = PyPath.ExpandVars(PyPath.ExpandUser(value, vars), vars).Replace('/', '\\');
            return PyPath.Absolute(PyPath.IsAbsolute(expanded) ? expanded : System.IO.Path.Join(dir, expanded));
        }
        UsageException Bad(string where, string what) => new($"{full}: {where} {what}.");
        string Text(TomlTable t, string key, string where, string? fallback = null) =>
            t.TryGetValue(key, out var v) ? v is string { Length: > 0 } s ? s : throw Bad(where, $"{key} should be text, and not empty")
                : fallback ?? throw Bad(where, $"needs {key}");
        List<string> Texts(TomlTable t, string key, string where) =>
            !t.TryGetValue(key, out var v) ? []
                : v is TomlArray a && a.All(x => x is string) ? a.Cast<string>().ToList() : throw Bad(where, $"{key} should be a list of text");
        Dictionary<string, string> Table(TomlTable t, string key, string where)
        {
            if (!t.TryGetValue(key, out var v))
                return [];
            if (v is not TomlTable table || table.Values.Any(x => x is not string))
                throw Bad(where, $"{key} should be a table of text");
            return table.ToDictionary(kv => kv.Key, kv => (string)kv.Value!);
        }
        ReviewTemplate? Review(TomlTable t, string key, string where)
        {
            if (!t.TryGetValue(key, out var v))
                return null;
            if (v is not TomlTable r)
                throw Bad(where, $"{key} should be a table: source, type, title and any other fields");
            var fields = r.Where(kv => kv.Key is not ("source" or "type")).Select(kv =>
                (kv.Key, kv.Value as string ?? throw Bad(where, $"{key}.{kv.Key} should be text"))).ToList();
            return new ReviewTemplate(Text(r, "source", $"{where} {key}"), Text(r, "type", $"{where} {key}"), fields);
        }

        var kinds = new Dictionary<string, JobKind>(StringComparer.Ordinal);
        if (raw.TryGetValue("job", out var j))
        {
            if (j is not TomlTableArray jobs)
                throw new UsageException($"{full}: job should be [[job]] tables.");
            foreach (var t in jobs)
            {
                var where = $"[[job]] {(t.TryGetValue("name", out var n) ? n : "(no name)")}";
                string[] keys = ["name", "prompt", "context", "values", "date", "output", "work", "claude", "timeout_minutes", "sections",
                    "body", "must_match", "empty_items", "replace", "frontmatter", "marker", "commit", "commit_body", "review", "failed", "delete_input"];
                foreach (var key in t.Keys)
                    if (!keys.Contains(key))
                        throw Bad(where, $"has an unknown setting '{key}'");
                DateRule? date = null;
                if (t.TryGetValue("date", out var d))
                {
                    if (d is not TomlTable dt)
                        throw Bad(where, "date should be a table: field, pattern, otherwise");
                    date = new DateRule(Text(dt, "field", where), new Regex(Text(dt, "pattern", where), RegexOptions.CultureInvariant),
                        Text(dt, "otherwise", where, "received"));
                }
                var sections = Texts(t, "sections", where).Select(s => s.EndsWith('?') ? (s[..^1], false) : (s, true)).ToList();
                var body = t.TryGetValue("body", out _) ? Text(t, "body", where) : null;
                if (body is not null && !sections.Any(s => s.Item1 == body))
                    throw Bad(where, $"body '{body}' isn't one of its sections");
                var name = Text(t, "name", where);
                kinds[name] = new JobKind
                {
                    Name = name,
                    Prompt = Resolve(Text(t, "prompt", where)),
                    Context = Table(t, "context", where).ToDictionary(kv => kv.Key, kv => Resolve(kv.Value)),
                    Values = Table(t, "values", where),
                    Date = date,
                    Output = Text(t, "output", where),
                    Work = Resolve(Text(t, "work", where)),
                    Claude = Texts(t, "claude", where) is { Count: > 0 } c ? c : ["-p", "--output-format", "json"],
                    TimeoutMinutes = t.TryGetValue("timeout_minutes", out var tm) ? tm is long m && m is >= 1 and <= 240 ? (int)m : throw Bad(where, "timeout_minutes should be 1 to 240") : 15,
                    Sections = sections,
                    Body = body,
                    MustMatch = Texts(t, "must_match", where).Select(p => new Regex(p, RegexOptions.CultureInvariant)).ToList(),
                    EmptyItems = t.TryGetValue("empty_items", out _) ? Texts(t, "empty_items", where) : ["none", "nothing"],
                    Replace = Table(t, "replace", where).Select(kv => (kv.Key, kv.Value)).ToList(),
                    Frontmatter = Texts(t, "frontmatter", where),
                    Marker = t.TryGetValue("marker", out _) ? Text(t, "marker", where) : null,
                    Commit = t.TryGetValue("commit", out _) ? Text(t, "commit", where) : null,
                    CommitBody = t.TryGetValue("commit_body", out _) ? Text(t, "commit_body", where) : null,
                    Review = Review(t, "review", where),
                    Failed = Review(t, "failed", where),
                    DeleteInput = !t.TryGetValue("delete_input", out var di) || (di is bool b ? b : throw Bad(where, "delete_input should be true or false")),
                };
            }
        }
        GitSettings? git = null;
        if (raw.TryGetValue("git", out var g))
        {
            try
            {
                git = GitSettings.Read(g, full, Resolve);
            }
            catch (FormatException error)
            {
                throw new UsageException(error.Message);
            }
        }
        string? reviewFile = null, reviewLock = null;
        if (raw.TryGetValue("review", out var rv))
        {
            if (rv is not TomlTable r)
                throw new UsageException($"{full}: [review] should be a table: file, and lock.");
            reviewFile = r.TryGetValue("file", out var f) && f is string { Length: > 0 } fs ? Resolve(fs) : throw new UsageException($"{full}: [review] needs file.");
            reviewLock = r.TryGetValue("lock", out var l) && l is string ls ? ls : null;
        }
        return new JobsConfig
        {
            Path = full, Dir = dir, Kinds = kinds, Git = git, ReviewFile = reviewFile, ReviewLock = reviewLock,
            LogFile = raw.TryGetValue("log_file", out var lf) && lf is string { Length: > 0 } lfs ? Resolve(lfs) : null,
        };
    }
}

/// <summary>A job's run: its input, the values its templates can use, and what it did.</summary>
public static partial class Jobs
{
    /// <summary>A template's placeholders: {name}, {name:format}, {name|text if empty}; {{ and }} for braces.</summary>
    [GeneratedRegex(@"\{\{|\}\}|\{([^{}:|]+)(?::([^{}|]*))?(?:\|([^{}]*))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    /// <summary>Fills a template. Values may be text, a list (joined with "; ") or a date (formatted). A line template
    /// ending "?" is left out (null) when any of its placeholders came out empty. single: values trimmed to one line.</summary>
    public static string? Fill(string template, Func<string, object?> value, bool single = false)
    {
        var optional = template.EndsWith('?');
        if (optional)
            template = template[..^1];
        var empty = false;
        var text = Placeholder().Replace(template, m =>
        {
            if (m.Value == "{{")
                return "{";
            if (m.Value == "}}")
                return "}";
            var v = value(m.Groups[1].Value.Trim());
            var format = m.Groups[2].Success ? m.Groups[2].Value : null;
            var shown = v switch
            {
                null => "",
                DateTimeOffset at => at.DateTime.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateOnly day => day.ToString(format ?? "yyyy-MM-dd", CultureInfo.InvariantCulture),
                IEnumerable<string> list => string.Join("; ", list),
                _ => format is not null && Iso(v.ToString()!) is { } parsed ? parsed.DateTime.ToString(format, CultureInfo.InvariantCulture) : v.ToString()!,
            };
            if (single)
                shown = shown.Trim();
            if (shown.Length == 0 && m.Groups[3].Success)
                shown = m.Groups[3].Value;
            if (shown.Length == 0)
                empty = true;
            return shown;
        });
        return optional && empty ? null : text;
    }

    internal static DateTimeOffset? Iso(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>The job's date: the rule's pattern on its field (day, then month; the year that puts it nearest before
    /// the received date, a year back if it would be more than a week after it), else the received date.</summary>
    public static DateOnly DateOf(DateRule? rule, JsonObject input)
    {
        var received = Iso(Field(input, rule?.Otherwise ?? "received")) is { } r ? DateOnly.FromDateTime(r.DateTime) : DateOnly.FromDateTime(DateTime.Today);
        if (rule is null || rule.Pattern.Match(Field(input, rule.Field)) is not { Success: true } m || m.Groups.Count < 3)
            return received;
        if (!int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var day) || !int.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out var month))
            return received;
        try
        {
            var when = new DateOnly(received.Year, month, day);
            return when > received.AddDays(7) ? when.AddYears(-1) : when;
        }
        catch (ArgumentOutOfRangeException)
        {
            return received;
        }
    }

    internal static string Field(JsonObject input, string name) =>
        input[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : input[name]?.ToJsonString() ?? "";

    /// <summary>The reply taken apart: each section's lines, by name. A line "NAME:" (or "NAME: text") starts a
    /// section; anything before the first is ignored. Missing a required section is an error.</summary>
    public static Dictionary<string, List<string>> Sections(string reply, IReadOnlyList<(string Name, bool Required)> sections)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        var names = string.Join("|", sections.Select(s => Regex.Escape(s.Name)));
        var head = new Regex($@"^\s*({names}):\s*(.*)$", RegexOptions.CultureInvariant);
        foreach (var line in reply.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (sections.Count > 0 && head.Match(line) is { Success: true } m && !found.ContainsKey(m.Groups[1].Value))
            {
                current = found[m.Groups[1].Value] = [];
                if (m.Groups[2].Value.Trim().Length > 0)
                    current.Add(m.Groups[2].Value);
                continue;
            }
            current?.Add(line);
        }
        var missing = sections.Where(s => s.Required && !found.ContainsKey(s.Name)).Select(s => s.Name + ":").ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"the reply has no {string.Join(" / ", missing)} section{(missing.Count > 1 ? "s" : "")}");
        return found;
    }

    /// <summary>A section as items: its lines without a leading "- " or "* ", blank ones and "none"-like ones left out.</summary>
    public static List<string> Items(IEnumerable<string>? lines, IReadOnlyList<string> empty) =>
        (lines ?? []).Select(l => Regex.Replace(l, @"^\s*[-*]\s*", "").Trim())
            .Where(l => l.Length > 0 && !empty.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
}
