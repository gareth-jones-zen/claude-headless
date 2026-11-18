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
/// year that puts it nearest before the input's received date), otherwise the received date (with no pattern, always).</summary>
public sealed record DateRule(string? Field, Regex? Pattern, string Otherwise);

/// <summary>One turn of a conversation (0.3.0): a prompt (a template file, or text), with replacements made after
/// it's filled. Turn 2 on resumes turn 1's conversation, in the same folder.</summary>
public sealed record TurnSpec(string? PromptFile, string? Text, IReadOnlyList<(string From, string To)> Replace);

/// <summary>A value cut from the input or a file (0.3.0), as {extract.NAME}: the text up to `upto`, spaces tidied if
/// asked, then what's after `after` and before `before` (each if it matches), then `pattern`'s first group (which must
/// match), trimmed.</summary>
public sealed record ExtractSpec(string Name, string? Field, string? File, Regex? Upto, bool Tidy, Regex? After, Regex? Before, Regex? Pattern);

/// <summary>A step before Claude (0.3.0): copy a file, or run a command, each a template.</summary>
public sealed record PrepareStep(IReadOnlyList<string>? Run, string? Copy, string? To);

/// <summary>A table (CSV) a reply adds rows to (0.3.0): the last fenced block in a turn's reply that matches block
/// (its first group, from the header line on). Rows whose key (the first column) is new are added; existing rows are
/// never changed: where the reply's copy differs, the difference is reported ({table.changes}) for a person to decide.</summary>
public sealed record TableSpec(string File, int Turn, Regex Block);

/// <summary>One [[job]] kind: take an input, give it to Claude with a prompt, check the reply, put the output here.</summary>
public sealed class JobKind
{
    public required string Name { get; init; }
    public string? Prompt { get; init; }                                          // a template file (one turn)
    public IReadOnlyList<TurnSpec> Turns { get; init; } = [];                       // or a conversation (0.3.0)
    public IReadOnlyList<ExtractSpec> Extracts { get; init; } = [];
    public IReadOnlyList<PrepareStep> Prepare { get; init; } = [];
    public bool FreshWork { get; init; }                                            // empty the work folder first
    public string? BodyTemplate { get; init; }                                      // the output's body, from a template file
    public TableSpec? Table { get; init; }
    public IReadOnlyList<string> CommitAlso { get; init; } = [];
    public (string Title, string Text)? Notify { get; init; }
    public IReadOnlyDictionary<string, string> Context { get; init; } = new Dictionary<string, string>();   // name: file
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();    // name: text
    public DateRule? Date { get; init; }
    public required string Output { get; init; }                                  // a path template
    public required string Work { get; init; }                                    // Claude's folder: a path template
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

    /// <summary>A path setting as a full path, relative to the settings file, with ~ and %VARIABLES%.</summary>
    public required Func<string, string> Resolve { get; init; }

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
                    "body", "must_match", "empty_items", "replace", "frontmatter", "marker", "commit", "commit_body", "review", "failed", "delete_input",
                    "turn", "extract", "prepare", "fresh_work", "body_template", "table", "commit_also", "notify"];
                foreach (var key in t.Keys)
                    if (!keys.Contains(key))
                        throw Bad(where, $"has an unknown setting '{key}'");
                DateRule? date = null;
                if (t.TryGetValue("date", out var d))
                {
                    if (d is not TomlTable dt)
                        throw Bad(where, "date should be a table: field, pattern, otherwise");
                    var field = dt.TryGetValue("field", out _) ? Text(dt, "field", where) : null;
                    var pattern = dt.TryGetValue("pattern", out _) ? new Regex(Text(dt, "pattern", where), RegexOptions.CultureInvariant) : null;
                    if ((field is null) != (pattern is null))
                        throw Bad(where, "date needs field and pattern together, or neither (the received date)");
                    date = new DateRule(field, pattern, Text(dt, "otherwise", where, "received"));
                }
                var sections = Texts(t, "sections", where).Select(s => s.EndsWith('?') ? (s[..^1], false) : (s, true)).ToList();
                var body = t.TryGetValue("body", out _) ? Text(t, "body", where) : null;
                if (body is not null && !sections.Any(s => s.Item1 == body))
                    throw Bad(where, $"body '{body}' isn't one of its sections");
                var name = Text(t, "name", where);
                IEnumerable<TomlTable> Tables(string key) => !t.TryGetValue(key, out var v) ? []
                    : v as TomlTableArray ?? throw Bad(where, $"{key} should be [[job.{key}]] tables");
                Regex? Rx(TomlTable table, string key) =>
                    table.TryGetValue(key, out _) ? new Regex(Text(table, key, where), RegexOptions.CultureInvariant) : null;
                var turns = Tables("turn").Select(tt => new TurnSpec(
                    tt.TryGetValue("prompt", out _) ? Resolve(Text(tt, "prompt", where)) : null,
                    tt.TryGetValue("text", out _) ? Text(tt, "text", where) : null,
                    Table(tt, "replace", where).Select(kv => (kv.Key, kv.Value)).ToList())).ToList();
                if (turns.Any(tu => (tu.PromptFile is null) == (tu.Text is null)))
                    throw Bad(where, "each [[job.turn]] needs prompt (a file) or text, not both");
                if (turns.Count == 0 && !t.TryGetValue("prompt", out _))
                    throw Bad(where, "needs prompt, or [[job.turn]]s");
                var extracts = Tables("extract").Select(e => new ExtractSpec(Text(e, "name", where),
                    e.TryGetValue("field", out _) ? Text(e, "field", where) : null, e.TryGetValue("file", out _) ? Resolve(Text(e, "file", where)) : null,
                    Rx(e, "upto"), e.TryGetValue("tidy", out var ti) && ti is true, Rx(e, "after"), Rx(e, "before"), Rx(e, "pattern"))).ToList();
                if (extracts.Any(e => (e.Field is null) == (e.File is null)))
                    throw Bad(where, "each [[job.extract]] needs field (from the input) or file, not both");
                var prepare = Tables("prepare").Select(p => new PrepareStep(
                    p.TryGetValue("run", out _) ? Texts(p, "run", where) : null,
                    p.TryGetValue("copy", out _) ? Text(p, "copy", where) : null,
                    p.TryGetValue("to", out _) ? Text(p, "to", where) : null)).ToList();
                if (prepare.Any(p => (p.Run is null) == (p.Copy is null) || (p.Copy is not null && p.To is null)))
                    throw Bad(where, "each [[job.prepare]] needs run, or copy with to");
                TableSpec? tableSpec = null;
                if (t.TryGetValue("table", out var tb))
                {
                    if (tb is not TomlTable tt)
                        throw Bad(where, "table should be a table: file, turn, block");
                    tableSpec = new TableSpec(Text(tt, "file", where),
                        tt.TryGetValue("turn", out var tn) ? tn is long ln && ln >= 1 ? (int)ln : throw Bad(where, "table.turn should be 1 or more") : 1,
                        Rx(tt, "block") ?? throw Bad(where, "table needs block, a pattern for the fenced block"));
                }
                (string, string)? notify = null;
                if (t.TryGetValue("notify", out var no))
                    notify = no is TomlTable nt ? (Text(nt, "title", where), Text(nt, "text", where)) : throw Bad(where, "notify should be a table: title, text");
                kinds[name] = new JobKind
                {
                    Name = name,
                    Prompt = t.TryGetValue("prompt", out _) ? Resolve(Text(t, "prompt", where)) : null,
                    Turns = turns,
                    Extracts = extracts,
                    Prepare = prepare,
                    FreshWork = t.TryGetValue("fresh_work", out var fw) && (fw is bool fb ? fb : throw Bad(where, "fresh_work should be true or false")),
                    BodyTemplate = t.TryGetValue("body_template", out _) ? Resolve(Text(t, "body_template", where)) : null,
                    Table = tableSpec,
                    CommitAlso = Texts(t, "commit_also", where),
                    Notify = notify,
                    Context = Table(t, "context", where).ToDictionary(kv => kv.Key, kv => Resolve(kv.Value)),
                    Values = Table(t, "values", where),
                    Date = date,
                    Output = Text(t, "output", where),
                    Work = Text(t, "work", where),
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
            Path = full, Dir = dir, Kinds = kinds, Git = git, ReviewFile = reviewFile, ReviewLock = reviewLock, Resolve = Resolve,
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
        if (rule?.Pattern is null || rule.Pattern.Match(Field(input, rule.Field!)) is not { Success: true } m || m.Groups.Count < 3)
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

    /// <summary>An extract's value (see ExtractSpec): from the input's field or the file.</summary>
    public static string Extract(ExtractSpec spec, JsonObject input)
    {
        var text = spec.Field is not null ? Field(input, spec.Field) : File.ReadAllText(spec.File!, Encoding.UTF8);
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (spec.Upto?.Match(text) is { Success: true } upto)
            text = text[..upto.Index];
        if (spec.Tidy)
        {
            text = Regex.Replace(text, "[ \t\u00a0]+", " ");
            text = Regex.Replace(text, @"\n\s*\n+", "\n\n").Trim();
        }
        if (spec.After?.Match(text) is { Success: true } after)
            text = text[(after.Index + after.Length)..];
        if (spec.Before?.Match(text) is { Success: true } before)
            text = text[..before.Index];
        if (spec.Pattern is not null)
            text = spec.Pattern.Match(text) is { Success: true } m && m.Groups.Count > 1 ? m.Groups[1].Value
                : throw new InvalidDataException($"extract {spec.Name}: {spec.Pattern} doesn't match");
        return text.Trim();
    }

    /// <summary>A table merge (see TableSpec): the file's text with the reply's new rows added, the rows added, and how
    /// the reply's copy differs from the file's existing rows (a line each; never applied). No block: null.</summary>
    public static (string Text, List<string> Added, List<string> Changes)? Merge(string existing, string reply, Regex block)
    {
        var matches = block.Matches(reply);
        if (matches.Count == 0)
            return null;
        var proposed = matches[^1].Groups[1].Value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split('\n')
            .Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (existing.Trim().Length == 0)   // no table yet: the reply's is the start of it
            return (string.Join(newline, proposed) + newline, proposed.Skip(1).ToList(), []);
        var lines = existing.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n').ToList();
        static string Key(string row) => row.Split(',')[0].Trim();
        var changes = new List<string>();
        if (proposed.Count > 0 && proposed[0] != lines[0])
            changes.Add($"header: {lines[0]} -> {proposed[0]}");
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in lines.Skip(1))
            rows.TryAdd(Key(row), row);
        var added = new List<string>();
        foreach (var row in proposed.Skip(1))
        {
            if (!rows.TryGetValue(Key(row), out var mine))
            {
                added.Add(row);
                rows[Key(row)] = row;
            }
            else if (mine != row)
                changes.Add($"{Key(row)}: {Differences(lines[0], mine, row)}");
        }
        var text = string.Join(newline, lines.Concat(added)) + newline;
        return (text, added, changes);
    }

    /// <summary>How two rows differ, column by column, named from the header: "base 104 -> 105, churn (empty) -> 1.2".</summary>
    private static string Differences(string header, string mine, string theirs)
    {
        var names = header.Split(',');
        var (a, b) = (mine.Split(','), theirs.Split(','));
        var parts = new List<string>();
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var (x, y) = (i < a.Length ? a[i] : "", i < b.Length ? b[i] : "");
            if (x != y)
                parts.Add($"{(i < names.Length ? names[i] : $"column {i + 1}")} {(x.Length > 0 ? x : "(empty)")} -> {(y.Length > 0 ? y : "(empty)")}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>A section as items: its lines without a leading "- " or "* ", blank ones and "none"-like ones left out.</summary>
    public static List<string> Items(IEnumerable<string>? lines, IReadOnlyList<string> empty) =>
        (lines ?? []).Select(l => Regex.Replace(l, @"^\s*[-*]\s*", "").Trim())
            .Where(l => l.Length > 0 && !empty.Contains(l, StringComparer.OrdinalIgnoreCase)).ToList();
}
