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

    /// <summary>The wall clock, for schedules (0.4.0).</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>The program a scheduled job is queued to run with: the windowless exe beside this one.</summary>
    public Func<string> ScheduledExe { get; init; } = () => Path.Combine(AppContext.BaseDirectory, "claude-headless-background.exe");

    /// <summary>Runs a prepare step's command in a folder: (exit code, what it said). Tests give a fake.</summary>
    public Func<IReadOnlyList<string>, string, (int Exit, string Output)> RunStep { get; init; } = (argv, cwd) =>
    {
        var done = Shell.Run(argv[0], argv.Skip(1), TimeSpan.FromMinutes(10), workingDirectory: cwd, utf8: true);
        return (done.ExitCode, (done.StandardError.Trim().Length > 0 ? done.StandardError : done.StandardOutput).Trim());
    };

    /// <summary>The command line that queued this run, for putting it back on the queue after a usage limit.</summary>
    public Func<IReadOnlyList<string>> Self { get; init; } = () => [Environment.ProcessPath ?? "claude-headless.exe", .. Environment.GetCommandLineArgs().Skip(1)];

    /// <summary>The queue task (0.5.0: autostart is handled here, from the settings' [schedule]).</summary>
    public Func<ITask> Task { get; init; } = () => new ScheduledTask(HeadlessCli.TaskName, HeadlessCli.TaskDescription);

    /// <summary>The windowless program the queue task runs.</summary>
    public Func<string> BackgroundExe { get; init; } = () => Path.Combine(AppContext.BaseDirectory, "claude-headless-background.exe");
}

/// <summary>claude-headless: the seat's queue of Claude Code jobs (status, run-queue, autostart: background-tools-lib's
/// HeadlessCli), and jobs defined in settings (job). Exit codes: 0 done; 1 failed; 2 settings or arguments.</summary>
public static class Cli
{
    public const string Usage =
        "usage: claude-headless [-h] [--version] [--config CONFIG] {status,run-queue,autostart,schedule,job} ...\n\n" +
        "claude-headless: the seat's queue of Claude Code jobs waiting out a usage limit, and jobs defined in settings.\n\n" +
        "    claude-headless status          the limit, if one is in force, and what's queued\n" +
        "    claude-headless run-queue       queue any scheduled jobs that are due, then run queued jobs one at a time,\n" +
        "                                    until done or the limit is back\n" +
        "    claude-headless --config FILE autostart on\n" +
        "                                    register the queue task from the settings' [schedule] (by default every 15\n" +
        "                                    minutes, at logon and a minute after each unlock); off; status\n" +
        "    claude-headless --config FILE schedule on|off|status\n" +
        "                                    whether run-queue starts this settings file's scheduled jobs\n" +
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
        if (args.Count > 0 && args[0] == "schedule")
            return Schedule(args.Skip(1).ToList(), config, host);
        if (args.Count > 0 && args[0] == "autostart")
            return Autostart(args.Skip(1).ToList(), config, host);
        if (args.Count == 1 && args[0] == "run-queue")
        {
            // The scheduled jobs that are due go on the queue first, so this run starts them.
            var log = Logger.Create(host.Queue.LogFile, host.LogEcho);
            try
            {
                Schedules.Check(host.Queue.Home, config is null ? null : Path.GetFullPath(config), host.Now(), host.Env, host.ScheduledExe,
                    (argv, cwd) => host.Queue.Add(argv, cwd), log);
            }
            catch (Exception error)
            {
                log($"schedule: check failed: {error.GetType().Name}: {error.Message}");
            }
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

    /// <summary>autostart on|off|status (0.5.0): the queue task, built from the settings' [schedule] (background-tools-lib's
    /// shared keys; with no --config, or no [schedule], every 15 minutes, at logon and a minute after each unlock). The
    /// old --every and --after-unlock flags still work, but not together with the same key in [schedule].</summary>
    private static int Autostart(List<string> args, string? config, CliHost host)
    {
        int? every = null, afterUnlock = null;
        string? action = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--every" && i + 1 < args.Count && int.TryParse(args[i + 1], out var minutes) && minutes >= 1)
            {
                every = minutes;
                i++;
            }
            else if (args[i].StartsWith("--every=", StringComparison.Ordinal) && int.TryParse(args[i][8..], out var m) && m >= 1)
                every = m;
            else if (args[i] == "--after-unlock" && i + 1 < args.Count && int.TryParse(args[i + 1], out var u) && u >= 0)
            {
                afterUnlock = u;
                i++;
            }
            else if (action is null && args[i] is "on" or "off" or "status")
                action = args[i];
            else
                return Bad(host, $"unrecognized arguments: {args[i]}");
        }
        if (action is null)
            return Bad(host, "the following arguments are required: action");
        ScheduleSettings schedule;
        try
        {
            schedule = JobsConfig.DefaultSchedule;
            Tomlyn.Model.TomlTable? table = null;
            if (config is not null)
            {
                var settings = JobsConfig.Load(config, host.Env);
                schedule = settings.Schedule;
                table = ReadScheduleTable(settings.Path);
            }
            if (every is { } e)
            {
                if (table?.ContainsKey("every_minutes") == true)
                    throw new UsageException("--every and [schedule] every_minutes both set: use [schedule] (--every is the old way).");
                schedule = schedule with { EveryMinutes = e };
            }
            if (afterUnlock is { } a)
            {
                if (table?.ContainsKey("after_unlock_minutes") == true)
                    throw new UsageException("--after-unlock and [schedule] after_unlock_minutes both set: use [schedule] (--after-unlock is the old way).");
                schedule = schedule with { AfterUnlockMinutes = a };
            }
            schedule.Validate(TaskKind.Job);
        }
        catch (UsageException error)
        {
            host.Err.WriteLine(error.Message);
            return 2;
        }
        catch (FormatException error)
        {
            host.Err.WriteLine(error.Message.TrimEnd());
            return 2;
        }
        var task = host.Task();
        switch (action)
        {
            case "on":
                var line = task.Enable(host.BackgroundExe(), ["run-queue"], null, schedule.ToTaskSchedule(TaskKind.Job, TimeSpan.FromHours(4)));
                host.Out.WriteLine($"Autostart is on: '{task.Name}' {Describe(schedule)}.");
                host.Out.WriteLine($"  {line}");
                return 0;
            case "off":
                host.Out.WriteLine(task.Disable() ? "Autostart is off." : "Autostart was already off.");
                return 0;
            default:
                var info = task.Status();
                if (info is null)
                {
                    host.Out.WriteLine("Autostart is off.");
                    return 0;
                }
                host.Out.WriteLine($"Autostart is on ('{task.Name}').");
                foreach (var key in new[] { "state", "last_run", "last_result", "next_run" })
                    host.Out.WriteLine($"  {key.Replace('_', ' '),-12} {info.GetValueOrDefault(key, "")}");
                if (ScheduleDrift.Advice(ScheduleDrift.Check(info, schedule.Fingerprint(TaskKind.Job)), "claude-headless") is { } advice)
                    host.Out.WriteLine($"  {advice}");
                return 0;
        }
    }

    /// <summary>The settings file's own [schedule] table (not the base's), to tell a key set there from a default.</summary>
    private static Tomlyn.Model.TomlTable? ReadScheduleTable(string path)
    {
        var text = PyStr.ReadText(path);
        if (text.StartsWith('﻿'))
            text = text[1..];
        return PyToml.Read(text, path).TryGetValue("schedule", out var s) ? s as Tomlyn.Model.TomlTable : null;
    }

    /// <summary>"runs every 15 minutes, at logon and 1 minute(s) after each unlock".</summary>
    private static string Describe(ScheduleSettings s)
    {
        var parts = new List<string>();
        if (s.EveryMinutes is > 0 and var m)
            parts.Add($"runs every {m} minutes");
        else if (s.DailyAt is { } d)
            parts.Add($"runs daily at {d}");
        if (s.AtLogon)
            parts.Add("at logon");
        if (s.AfterUnlockMinutes > 0)
            parts.Add($"{s.AfterUnlockMinutes} minute(s) after each unlock");
        return parts.Count switch
        {
            0 => "is registered",
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + " and " + parts[^1],
        };
    }

    private static int Bad(CliHost host, string message)
    {
        host.Err.WriteLine("usage: claude-headless [-h] [--version] [--config CONFIG] {status,run-queue,autostart,schedule,job} ...");
        host.Err.WriteLine($"claude-headless: error: {message}");
        return 2;
    }

    /// <summary>schedule on|off|status (0.4.0): whether run-queue starts a settings file's [[job.schedule]]s. The queue
    /// task is registered without settings, so the settings file is recorded beside the queue instead.</summary>
    private static int Schedule(List<string> args, string? config, CliHost host)
    {
        if (args.Count != 1 || args[0] is not ("on" or "off" or "status"))
            return Bad(host, "schedule needs one of on, off, status");
        var home = host.Queue.Home;
        var now = host.Now();
        try
        {
            if (args[0] == "off")
            {
                if (config is null)
                    return Bad(host, "schedule off needs --config FILE");
                var path = JobsConfig.Load(config, host.Env).Path;
                host.Out.WriteLine(Schedules.Off(home, path) ? $"Schedules are off for {path}." : $"Schedules were already off for {path}.");
                return 0;
            }
            if (args[0] == "on")
            {
                if (config is null)
                    return Bad(host, "schedule on needs --config FILE");
                var settings = JobsConfig.Load(config, host.Env);
                Schedules.On(home, settings, now);
                host.Out.WriteLine($"Schedules are on for {settings.Path}: run-queue (the queue task) starts each job when it's due.");
                ShowSchedules(settings, home, now, host);
                return 0;
            }
            var files = config is null ? Schedules.Recorded(home).ToList() : [JobsConfig.Load(config, host.Env).Path];
            if (files.Count == 0)
                host.Out.WriteLine("No settings file's schedules are on (claude-headless --config FILE schedule on).");
            foreach (var file in files)
            {
                var on = Schedules.Recorded(home).Contains(file, StringComparer.OrdinalIgnoreCase);
                host.Out.WriteLine(on ? $"Schedules are on for {file}." : $"Schedules are off for {file} (schedule on to start them).");
                try
                {
                    ShowSchedules(JobsConfig.Load(file, host.Env), home, now, host);
                }
                catch (Exception error) when (error is UsageException or IOException)
                {
                    host.Out.WriteLine($"  couldn't read it: {error.Message}");
                }
            }
            return 0;
        }
        catch (UsageException error)
        {
            host.Err.WriteLine(error.Message);
            return 2;
        }
    }

    private static void ShowSchedules(JobsConfig settings, string home, DateTime now, CliHost host)
    {
        var any = false;
        foreach (var job in settings.Kinds.Values)
            for (var n = 0; n < job.Schedules.Count; n++)
            {
                any = true;
                var spec = job.Schedules[n];
                var last = Schedules.Last(home, settings.Path, job.Name, n);
                host.Out.WriteLine($"  {job.Name}: {spec}; next {(spec.Next(now) is { } next ? next.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture) : "never")}"
                                   + (last is { } l ? $"; last started (or switched on) {l.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture)}" : ""));
            }
        if (!any)
            host.Out.WriteLine("  (no job has a [[job.schedule]])");
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

            // "Not one for me": a SKIP: section (named by `skip`) means no output, and a `skipped` item saying why.
            if (job.Skip is { } skipName && Jobs.Sections(reply, [(skipName, false)]).TryGetValue(skipName, out var skipLines))
            {
                var why = Jobs.Items(skipLines, []);
                values[skipName] = why.Count > 0 ? why : ["(no reason given)"];
                log($"job {job.Name}: skipped: {string.Join("; ", (List<string>)values[skipName]!)}");
                if (outFile is not null)
                    return 0;
                if (job.Skipped is { } skipped && settings.ReviewFile is { } skippedFile)
                    new QueueFile(skippedFile, settings.ReviewLock).Add(skipped.Source, skipped.Type, Fields(skipped, Value));
                Finish(job, inputPath, work);
                return 0;
            }

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
        catch (NothingToDoException nothing)
        {
            log($"job {job.Name}: nothing to do{(nothing.Message.Length > 0 ? $": {nothing.Message}" : "")}");
            if (outFile is null)
                Finish(job, inputPath, work);
            return 0;
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

        var keptByGit = false;
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
            keptByGit = committed.Committed;
        }
        // Not committed as written (no git, not a repo, or the commit didn't land): keep a copy outside the folder, so
        // the draft can still be compared with what was sent once it's been edited.
        if (!keptByGit && settings.Originals is { } originals)
        {
            var relative = Path.GetRelativePath(settings.OriginalsRoot, target);
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                relative = Path.GetFileName(target);
            var copy = Path.Combine(originals, relative);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(target, copy, true);
                log($"  kept the as-written copy: {copy}");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                log($"  couldn't keep the as-written copy: {error.Message}");
            }
        }
        if (job.Review is { } review && settings.ReviewFile is { } file)
            new QueueFile(file, settings.ReviewLock).Add(review.Source, review.Type, Fields(review, Value));
        if (job.Notify is { } notify)
            host.Notify(Jobs.Fill(notify.Title, Value, single: true)!, [Jobs.Fill(notify.Text, Value)!]);
        Finish(job, inputPath, work);
        return 0;
    }

    /// <summary>A job done (or skipped, or with nothing to do): the input and its attachments deleted (unless the
    /// settings keep it), and the progress file.</summary>
    private static void Finish(JobKind job, string inputPath, string work)
    {
        if (job.DeleteInput)
        {
            File.Delete(inputPath);
            var attachments = Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath) + "-attachments");
            if (Directory.Exists(attachments))
                Directory.Delete(attachments, true);   // mail-triage's queue handler keeps them beside the email for the job
        }
        if (File.Exists(Progress(work)))
            File.Delete(Progress(work));
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
        if (step.NothingToDoExit is { } nothing && exit == nothing)
            throw new NothingToDoException(PyStr.Head(output, 300));
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
