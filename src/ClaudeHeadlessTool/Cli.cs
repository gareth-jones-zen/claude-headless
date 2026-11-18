using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHeadless;
using PyCompat;
using RepoCommit;
using ReviewQueue;
using WinBackground;

namespace ClaudeHeadlessTool;

/// <summary>Everything the command line touches outside itself, so tests can give it fakes.</summary>
public sealed class CliHost
{
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Err { get; init; } = Console.Error;
    public TextWriter? LogEcho { get; init; }
    public Func<string, string?> Env { get; init; } = name => string.IsNullOrEmpty(name) ? null : Environment.GetEnvironmentVariable(name);
    public JobQueue Queue { get; init; } = JobQueue.Default;
    public IClaudeRunner? Claude { get; init; }
    public Func<string, Committer> Committer { get; init; } = repo => new Committer(repo);
    public Action<string, IEnumerable<string>> Notify { get; init; } = Toast.Notify;
    public Func<DateOnly> Today { get; init; } = () => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>Runs a prepare step's command in a folder: (exit code, what it said). Tests give a fake.</summary>
    public Func<IReadOnlyList<string>, string, (int Exit, string Output)> RunStep { get; init; } = (argv, cwd) =>
    {
        var done = Shell.Run(argv[0], argv.Skip(1), TimeSpan.FromMinutes(10), workingDirectory: cwd, utf8: true);
        return (done.ExitCode, (done.StandardError.Trim().Length > 0 ? done.StandardError : done.StandardOutput).Trim());
    };

    /// <summary>The command line that queued this run, for putting it back on the queue after a usage limit.</summary>
    public Func<IReadOnlyList<string>> Self { get; init; } = () => [Environment.ProcessPath ?? "claude-headless.exe", .. Environment.GetCommandLineArgs().Skip(1)];
}

/// <summary>claude-headless: the seat's queue of Claude Code jobs (status, run-queue, autostart: background-tools-lib's
/// HeadlessCli), and jobs defined in settings (job). Exit codes: 0 done; 1 failed; 2 settings or arguments.</summary>
public static class Cli
{
    public const string Usage =
        "usage: claude-headless [-h] [--version] [--config CONFIG] {status,run-queue,autostart,job} ...\n\n" +
        "claude-headless: the seat's queue of Claude Code jobs waiting out a usage limit, and jobs defined in settings.\n\n" +
        "    claude-headless status          the limit, if one is in force, and what's queued\n" +
        "    claude-headless run-queue       run queued jobs one at a time, until done or the limit is back\n" +
        "    claude-headless autostart on    run the queue every 15 minutes (Task Scheduler); off, status\n" +
        "    claude-headless --config FILE job KIND --input FILE\n" +
        "                                    a job from the settings: the input and the prompt to Claude, the reply\n" +
        "                                    checked and written where the settings say, committed, and a review item\n" +
        "        --out FILE                  write the output there instead: no commit, no review item, input kept\n" +
        "        --force                     run it even if its output is there already\n";

    /// <summary>Kept beside the work folder (not in it: Claude reads there) between a usage limit and the rerun: the
    /// input, and each finished turn's session and reply, so the rerun carries on from the turn that was cut off.</summary>
    public static string Progress(string work) => Path.TrimEndingDirectorySeparator(work) + ".claude-headless-progress.json";

    public static string Version =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static int Run(IReadOnlyList<string> argv, CliHost host)
    {
        var args = argv.ToList();
        string? config = null;
        var at = args.IndexOf("--config");
        if (at >= 0 && at + 1 < args.Count)
        {
            config = args[at + 1];
            args.RemoveRange(at, 2);
        }
        if (args.Count > 0 && args[0] is "-h" or "--help")
        {
            host.Out.Write(Usage);
            return 0;
        }
        if (args.Count > 0 && args[0] == "--version")
        {
            host.Out.WriteLine(Version);
            return 0;
        }
        if (args.Count == 0 || args[0] != "job")
            return HeadlessCli.Run(args, new HeadlessHost
            {
                Out = host.Out, Err = host.Err, LogEcho = host.LogEcho, Queue = host.Queue, Version = Version,
                BackgroundCommand = () => (Path.Combine(AppContext.BaseDirectory, "claude-headless-background.exe"), []),
            });

        string? kind = null, input = null, outFile = null;
        var force = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--input" && i + 1 < args.Count)
                input = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Count)
                outFile = args[++i];
            else if (args[i] == "--force")
                force = true;
            else if (kind is null && !args[i].StartsWith('-'))
                kind = args[i];
            else
                return Bad(host, $"unrecognized arguments: {args[i]}");
        }
        if (config is null || kind is null || input is null)
            return Bad(host, "job needs --config FILE, the kind, and --input FILE");
        try
        {
            var settings = JobsConfig.Load(config, host.Env);
            if (!settings.Kinds.TryGetValue(kind, out var job))
                throw new UsageException($"No job called '{kind}' in {settings.Path} (there are: {string.Join(", ", settings.Kinds.Keys)}).");
            return RunJob(settings, job, Path.GetFullPath(input), outFile is null ? null : Path.GetFullPath(outFile), host, force);
        }
        catch (UsageException error)
        {
            host.Err.WriteLine(error.Message);
            return 2;
        }
        catch (Exception error)
        {
            host.Err.WriteLine($"{error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    private static int Bad(CliHost host, string message)
    {
        host.Err.WriteLine("usage: claude-headless [-h] [--version] [--config CONFIG] {status,run-queue,autostart,job} ...");
        host.Err.WriteLine($"claude-headless: error: {message}");
        return 2;
    }

    /// <summary>One job, start to finish. The output already there: nothing to do (unless forced). A usage limit: back
    /// on the queue for after the reset, carrying on from the turn it reached. A reply that isn't the shape the settings
    /// ask for, a step or Claude failing: a "failed" review item (if the settings give one) and exit 1, the input kept.</summary>
    internal static int RunJob(JobsConfig settings, JobKind job, string inputPath, string? outFile, CliHost host, bool force = false)
    {
        var log = Logger.Create(settings.LogFile, host.Out, stampEcho: false);
        var input = JsonNode.Parse(File.ReadAllText(inputPath, Encoding.UTF8)) as JsonObject
                    ?? throw new UsageException($"{inputPath} isn't a JSON object.");
        var date = Jobs.DateOf(job.Date, input);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["date"] = date, ["today"] = host.Today(), ["input.path"] = inputPath,
        };
        object? Value(string name)
        {
            if (values.TryGetValue(name, out var v))
                return v;
            if (name.StartsWith("input.", StringComparison.Ordinal))
                return Jobs.Field(input, name[6..]);
            if (name.StartsWith("value.", StringComparison.Ordinal))
                return job.Values.GetValueOrDefault(name[6..]);
            if (name.StartsWith("context.", StringComparison.Ordinal) && job.Context.TryGetValue(name[8..], out var file))
                return File.ReadAllText(file, Encoding.UTF8);
            return null;
        }
        string Path1(string template) => settings.Resolve(Jobs.Fill(template, Value, single: true)!);

        var target = outFile ?? Path1(job.Output);
        string Shown(string path) => settings.Git is { } g && path.StartsWith(g.Repo, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(g.Repo, path).Replace('\\', '/') : path;
        values["output"] = Shown(target);
        if (outFile is null && File.Exists(target) && !force)
        {
            log($"job {job.Name}: {Shown(target)} exists already, nothing to do");
            return 0;
        }

        var work = Path1(job.Work);
        values["work"] = work;
        string reply;
        List<string>? body = null;
        try
        {
            foreach (var extract in job.Extracts)
                values[$"extract.{extract.Name}"] = Jobs.Extract(extract, input);

            // A conversation: the turns, each resuming the last. One prompt is one turn.
            var turns = job.Turns.Count > 0 ? job.Turns : [new TurnSpec(job.Prompt, null, [])];
            var progressPath = Progress(work);
            var done = LoadProgress(progressPath, inputPath);
            if (done.Count == 0)
            {
                if (job.FreshWork && Directory.Exists(work))
                    Directory.Delete(work, true);   // nothing left from another run: the method may depend on what's here
                Directory.CreateDirectory(work);
                foreach (var step in job.Prepare)
                    Prepare(step, Value, settings, host, log, job.Name);
            }
            else
                log($"job {job.Name}: carrying on from turn {done.Count + 1} (turns before it finished earlier)");
            for (var n = 0; n < done.Count; n++)
                (values[$"turn.{n + 1}"], values[$"turn.{n + 1}.session"]) = (done[n].Reply, done[n].Session);

            var runner = host.Claude ?? new ClaudeRunner(host.Queue);
            for (var n = done.Count; n < turns.Count; n++)
            {
                var turn = turns[n];
                var prompt = Jobs.Fill(turn.PromptFile is not null ? File.ReadAllText(turn.PromptFile, Encoding.UTF8) : turn.Text!, Value)!;
                foreach (var (from, to) in turn.Replace)
                    prompt = prompt.Replace(from, Jobs.Fill(to, Value), StringComparison.Ordinal);
                var command = n == 0 ? job.Claude : [.. job.Claude, "--resume", (string)values["turn.1.session"]!];
                JsonObject result;
                try
                {
                    result = runner.Run(command, prompt, work, TimeSpan.FromMinutes(job.TimeoutMinutes));
                }
                catch (LimitReachedException limit)
                {
                    host.Queue.Retry(host.Self(), Environment.CurrentDirectory, limit.Reset);
                    log($"job {job.Name}: usage limit{(turns.Count > 1 ? $" at turn {n + 1}" : "")}, queued again for after {limit.Reset.ToLocalTime():HH:mm}");
                    return 0;
                }
                var text = (result["result"]?.ToString() ?? "").Trim();
                var session = result["session_id"]?.ToString() ?? (n > 0 ? (string)values["turn.1.session"]! : "");
                (values[$"turn.{n + 1}"], values[$"turn.{n + 1}.session"]) = (text, session);
                done.Add((session, text));
                if (turns.Count > 1)
                    SaveProgress(progressPath, inputPath, done);
                log($"job {job.Name}: turn {n + 1} of {turns.Count} done" + (result["total_cost_usd"] is JsonValue c ? $" (${c.GetValue<double>():0.00})" : ""));
            }
            reply = done[^1].Reply;

            if (job.Sections.Count > 0)
            {
                var sections = Jobs.Sections(reply, job.Sections);
                foreach (var (name, _) in job.Sections.Where(s => s.Name != job.Body))
                    values[name] = Jobs.Items(sections.GetValueOrDefault(name), job.EmptyItems);
                if (job.Body is not null)
                    body = sections[job.Body].Where(l => l.Trim().Length > 0).Select(l => l.TrimEnd()).ToList();
            }
            if (body is null && job.BodyTemplate is not null)
                body = Jobs.Fill(File.ReadAllText(job.BodyTemplate, Encoding.UTF8), Value)!.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
            body ??= reply.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).Select(l => l.TrimEnd()).ToList();
            var bodyText = string.Join("\n", body);
            foreach (var pattern in job.MustMatch)
                if (!pattern.IsMatch(bodyText))
                    throw new InvalidDataException($"the reply's {job.Body ?? "text"} doesn't match {pattern}: {PyStr.Head(bodyText, 80)}");
        }
        catch (Exception error) when (error is InvalidDataException or ClaudeRunException or TimeoutException or IOException)
        {
            return Failed(settings, job, Value, values, error.Message, log, host);
        }
        foreach (var (from, to) in job.Replace)
            body = body.Select(l => l.Replace(from, to, StringComparison.Ordinal)).ToList();

        // The table: new rows added, existing rows never changed (Claude's changes are reported instead).
        string? tablePath = null, tableText = null;
        if (job.Table is { } table)
        {
            tablePath = Path1(table.File);
            var turnReply = values.GetValueOrDefault($"turn.{table.Turn}") as string ?? "";
            if (Jobs.Merge(File.Exists(tablePath) ? File.ReadAllText(tablePath, Encoding.UTF8) : "", turnReply, table.Block) is { } merged)
            {
                tableText = merged.Text;
                values["table.added"] = merged.Added;
                values["table.changes"] = merged.Changes;
                if (merged.Changes.Count > 0)
                    log($"job {job.Name}: the reply's table differs from {Shown(tablePath)} in {merged.Changes.Count} existing row(s): not applied, reported");
            }
            else
                log($"job {job.Name}: WARNING no table block in turn {table.Turn}'s reply; {Shown(tablePath)} left as it is");
        }

        var lines = new List<string>();
        if (job.Frontmatter.Count > 0)
        {
            lines.Add("---");
            lines.AddRange(job.Frontmatter.Select(f => Jobs.Fill(f, Value, single: true)).OfType<string>());
            lines.Add("---");
            if (job.BodyTemplate is not null && job.Marker is null)
                lines.Add("");
        }
        if (job.Marker is not null)
            lines.Add(job.Marker);
        lines.AddRange(body);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        log($"job {job.Name}: drafted {Shown(target)}");
        if (outFile is not null)
            return 0;
        if (tablePath is not null && tableText is not null)
            File.WriteAllText(tablePath, tableText, new UTF8Encoding(false));

        if (settings.Git is { Commit: true } git && job.Commit is not null)
        {
            var paths = new List<string> { target };
            if (tablePath is not null && tableText is not null)
                paths.Add(tablePath);
            paths.AddRange(job.CommitAlso.Select(Path1).Where(File.Exists));
            var subject = Jobs.Fill(job.Commit, Value, single: true)!;
            var committed = host.Committer(git.Repo).Commit(Committer.Message(subject, job.CommitBody is null ? null : Jobs.Fill(job.CommitBody, Value)),
                paths, trailers: git.Trailers, push: git.Push);
            log(committed.Error is { } e ? $"  commit failed: {e}" : committed.Committed
                ? $"  committed {committed.Commit}{(git.Push ? committed.Pushed ? ", pushed" : $", push failed: {committed.PushError}" : "")}" : "  nothing to commit");
        }
        if (job.Review is { } review && settings.ReviewFile is { } file)
            new QueueFile(file, settings.ReviewLock).Add(review.Source, review.Type, Fields(review, Value));
        if (job.Notify is { } notify)
            host.Notify(Jobs.Fill(notify.Title, Value, single: true)!, [Jobs.Fill(notify.Text, Value)!]);
        if (job.DeleteInput)
        {
            File.Delete(inputPath);
            var attachments = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + "-attachments");
            if (Directory.Exists(attachments))
                Directory.Delete(attachments, true);   // mail-triage's queue handler keeps them beside the email for the job
        }
        if (File.Exists(Progress(work)))
            File.Delete(Progress(work));
        return 0;
    }

    private static void Prepare(PrepareStep step, Func<string, object?> value, JobsConfig settings, CliHost host, Action<string> log, string job)
    {
        if (step.Copy is not null)
        {
            var from = settings.Resolve(Jobs.Fill(step.Copy, value, single: true)!);
            var to = settings.Resolve(Jobs.Fill(step.To!, value, single: true)!);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, to, true);
            return;
        }
        var argv = step.Run!.Select(a => Jobs.Fill(a, value, single: true)!).Select(a => a.Contains('/') || a.Contains('\\') ? settings.Resolve(a) : a).ToList();
        var (exit, output) = host.RunStep(argv, settings.Dir);
        if (exit != 0)
            throw new InvalidDataException($"prepare step {Path.GetFileName(argv[0])} exited {exit}: {PyStr.Head(output, 300)}");
        log($"job {job}: prepared ({Path.GetFileName(argv.Count > 1 ? argv[^1] : argv[0])})");
    }

    private static List<(string Session, string Reply)> LoadProgress(string path, string input)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is JsonObject p && p["input"]?.ToString() == input
                && p["turns"] is JsonArray turns)
                return turns.OfType<JsonObject>().Select(t => (t["session"]?.ToString() ?? "", t["reply"]?.ToString() ?? "")).ToList();
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
        }
        return [];
    }

    private static void SaveProgress(string path, string input, List<(string Session, string Reply)> done)
    {
        var turns = new JsonArray();
        foreach (var (session, reply) in done)
            turns.Add((JsonNode)new JsonObject { ["session"] = session, ["reply"] = reply });
        File.WriteAllText(path, new JsonObject { ["input"] = input, ["turns"] = turns }.ToJsonString(), new UTF8Encoding(false));
    }

    private static int Failed(JobsConfig settings, JobKind job, Func<string, object?> value, Dictionary<string, object?> values, string why,
        Action<string> log, CliHost host)
    {
        values["error"] = why;
        log($"job {job.Name}: failed: {why}");
        host.Err.WriteLine($"couldn't run the job: {why}");
        if (job.Failed is { } failed && settings.ReviewFile is { } file)
            new QueueFile(file, settings.ReviewLock).Add(failed.Source, failed.Type, Fields(failed, value));
        return 1;
    }

    private static JsonObject Fields(ReviewTemplate review, Func<string, object?> value)
    {
        var fields = new JsonObject();
        foreach (var (key, template) in review.Fields)
            if (Jobs.Fill(template, value, single: !template.Contains('\n')) is { } filled)   // a "?" field with nothing in it is left out
                fields[key] = filled;
        return fields;
    }
}
