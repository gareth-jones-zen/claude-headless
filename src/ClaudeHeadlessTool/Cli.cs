using System.Globalization;
using System.Reflection;
using System.Text;
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
        "        --out FILE                  write the output there instead: no commit, no review item, input kept\n";

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
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] == "--input" && i + 1 < args.Count)
                input = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Count)
                outFile = args[++i];
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
            return RunJob(settings, job, Path.GetFullPath(input), outFile is null ? null : Path.GetFullPath(outFile), host);
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

    /// <summary>One job, start to finish. The output already there: nothing to do. A usage limit: back on the queue for
    /// after the reset. A reply that isn't the shape the settings ask for, or Claude failing: a "failed" review item
    /// (if the settings give one) and exit 1, the input kept for a rerun.</summary>
    internal static int RunJob(JobsConfig settings, JobKind job, string inputPath, string? outFile, CliHost host)
    {
        var log = Logger.Create(settings.LogFile, host.Out, stampEcho: false);
        var input = JsonNode.Parse(File.ReadAllText(inputPath, Encoding.UTF8)) as JsonObject
                    ?? throw new UsageException($"{inputPath} isn't a JSON object.");
        var date = Jobs.DateOf(job.Date, input);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["date"] = date, ["input.path"] = inputPath };
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

        var target = outFile ?? Path.GetFullPath(Path.Join(settings.Dir, Jobs.Fill(job.Output, Value, single: true)!.Replace('/', '\\')));
        var repoPath = settings.Git is { } g ? Path.GetRelativePath(g.Repo, target).Replace('\\', '/') : target;
        values["output"] = repoPath;
        if (outFile is null && File.Exists(target))
        {
            log($"job {job.Name}: {repoPath} exists already, nothing to do");
            return 0;
        }

        var prompt = Jobs.Fill(File.ReadAllText(job.Prompt, Encoding.UTF8), Value)!;
        Directory.CreateDirectory(job.Work);   // an empty folder: the reply needs nothing but the prompt
        JsonObject result;
        try
        {
            result = (host.Claude ?? new ClaudeRunner(host.Queue)).Run(job.Claude, prompt, job.Work, TimeSpan.FromMinutes(job.TimeoutMinutes));
        }
        catch (LimitReachedException limit)
        {
            host.Queue.Retry(host.Self(), Environment.CurrentDirectory, limit.Reset);
            log($"job {job.Name}: usage limit, queued again for after {limit.Reset.ToLocalTime():HH:mm}");
            return 0;
        }
        catch (Exception error) when (error is ClaudeRunException or TimeoutException)
        {
            return Failed(settings, job, Value, values, $"Claude: {error.Message}", log, host);
        }

        var reply = result["result"]?.ToString() ?? "";
        List<string> body;
        try
        {
            var sections = Jobs.Sections(reply, job.Sections);
            foreach (var (name, _) in job.Sections.Where(s => s.Name != job.Body))
                values[name] = Jobs.Items(sections.GetValueOrDefault(name), job.EmptyItems);
            body = (job.Body is null ? reply.Replace("\r\n", "\n").Split('\n').ToList() : sections[job.Body])
                .Where(l => l.Trim().Length > 0).Select(l => l.TrimEnd()).ToList();
            var text = string.Join("\n", body);
            foreach (var pattern in job.MustMatch)
                if (!pattern.IsMatch(text))
                    throw new InvalidDataException($"the reply's {job.Body ?? "text"} doesn't match {pattern}: {PyStr.Head(text, 80)}");
        }
        catch (InvalidDataException error)
        {
            return Failed(settings, job, Value, values, error.Message, log, host);
        }
        foreach (var (from, to) in job.Replace)
            body = body.Select(l => l.Replace(from, to, StringComparison.Ordinal)).ToList();

        var lines = new List<string>();
        if (job.Frontmatter.Count > 0)
        {
            lines.Add("---");
            lines.AddRange(job.Frontmatter.Select(f => Jobs.Fill(f, Value, single: true)).OfType<string>());
            lines.Add("---");
        }
        if (job.Marker is not null)
            lines.Add(job.Marker);
        lines.AddRange(body);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        log($"job {job.Name}: drafted {repoPath}");
        if (outFile is not null)
            return 0;

        if (settings.Git is { Commit: true } git && job.Commit is not null)
        {
            var subject = Jobs.Fill(job.Commit, Value, single: true)!;
            var done = host.Committer(git.Repo).Commit(Committer.Message(subject, job.CommitBody is null ? null : Jobs.Fill(job.CommitBody, Value)),
                [target], trailers: git.Trailers, push: git.Push);
            log(done.Error is { } e ? $"  commit failed: {e}" : done.Committed ? $"  committed {done.Commit}{(git.Push ? done.Pushed ? ", pushed" : $", push failed: {done.PushError}" : "")}" : "  nothing to commit");
        }
        if (job.Review is { } review && settings.ReviewFile is { } file)
            new QueueFile(file, settings.ReviewLock).Add(review.Source, review.Type, Fields(review, Value));
        if (job.DeleteInput)
            File.Delete(inputPath);
        return 0;
    }

    private static int Failed(JobsConfig settings, JobKind job, Func<string, object?> value, Dictionary<string, object?> values, string why,
        Action<string> log, CliHost host)
    {
        values["error"] = why;
        log($"job {job.Name}: couldn't draft: {why}");
        host.Err.WriteLine($"couldn't draft: {why}");
        if (job.Failed is { } failed && settings.ReviewFile is { } file)
            new QueueFile(file, settings.ReviewLock).Add(failed.Source, failed.Type, Fields(failed, value));
        return 1;
    }

    private static JsonObject Fields(ReviewTemplate review, Func<string, object?> value)
    {
        var fields = new JsonObject();
        foreach (var (key, template) in review.Fields)
            fields[key] = Jobs.Fill(template, value, single: true) ?? "";
        return fields;
    }
}
