using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaudeHeadless;
using ReviewQueue;

namespace ClaudeHeadlessTool.Tests;

/// <summary>0.6.0: jobs that work in the folder itself, as the examples define them. finalise-summary (Claude edits the
/// draft, the ledger and the project page; the INBOX items are added by md-inbox; what changed is committed, nothing else)
/// and recommend (read only; the recommendations written onto review-queue items by review-queue update). An invented
/// folder (a git repository), a fake Claude that edits as the real one would, and fake md-inbox and review-queue.</summary>
public sealed class InPlaceJobTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ch-inplace-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string lockName = "ch-" + Guid.NewGuid().ToString("N")[..8];
    private readonly List<(IReadOnlyList<string> Command, string Prompt, string Cwd)> calls = [];
    private readonly List<IReadOnlyList<string>> steps = [];

    private const string Summary = "meetings/catch-ups/ann-lee/summaries/2026-11-24.md";

    private const string Draft = """
        ---
        title: "Catch-up: Ann Lee"
        date: 2026-11-24
        attendees: [Alex Brown, Ann Lee]
        topics: [partner portal, tag list]
        project: partner-portal
        transcript: ../2026-11-24.md
        status: draft
        ---
        [DRAFT]

        ## For review

        - [AB] 1. Is "the portal" the partner portal or the customer portal?
        - [AB] Waiting for? Ann Lee to send the tag list.
        - [AB] Dictated instructions? "Claude, please add an action for me to book the budget review" (12:40)
        - [Noted] "Fern" read as Fernhill.

        ## Purpose

        Monthly catch-up on the portal.

        ## Decisions

        - The portal launch moves to January.
        """;

    private const string Finalised = """
        ---
        title: "Catch-up: Ann Lee"
        date: 2026-11-24
        attendees: [Alex Brown, Ann Lee]
        topics: [partner portal, tag list]
        project: partner-portal
        transcript: ../2026-11-24.md
        ---

        ## Purpose

        Monthly catch-up on the partner portal.

        ## Decisions

        - The partner portal launch moves to January.

        ## Transcript notes

        - "Fern" read as Fernhill.
        """;

    private const string Reply = """
        CHANGED:
        - meetings/catch-ups/ann-lee/summaries/2026-11-24.md: the portal is the partner portal; For review removed
        - meetings/review-ledger.md: the portal question settled
        - projects/partner-portal.md: a Timeline row for 24 Nov
        INBOX:
        {"list": "waiting", "text": "[Waiting: Ann Lee, 2 Dec 2026] The tag list. From the 24 Nov 2026 catch-up."}
        {"list": "mine", "section": "Act", "text": "[Action] Book the budget review. Dictated in the 24 Nov 2026 catch-up: \"Claude, please add an action for me to book the budget review\"."}
        NOT APPLIED:
        - none
        """;

    public InPlaceJobTests()
    {
        Directory.CreateDirectory(Path.Combine(Folder, "config"));
        Write("meetings/catch-ups/ann-lee/2026-11-24.md", "[00:01] Ann Lee: The portal launch moves to January.\n");
        Write(Summary, Draft);
        Write("meetings/review-ledger.md", "# Review ledger\n\n## Settled\n\n## Open\n\n- Is \"the portal\" the partner portal? (2026-11-24 Ann Lee)\n");
        Write("projects/partner-portal.md", "---\nupdated: 2026-11-10\n---\n# Partner portal\n\n## Timeline\n\n| Date | What |\n|---|---|\n");
        Write("projects/other.md", "# Other\n");
        Write("inbox/waiting.md", "# Waiting\n\n## Waiting\n");
        Write("inbox/mine.md", "# Mine\n\n## Actions\n");
        Write("notes/scratch.md", "scratch\n");
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
        Git("add", "-A");
        Git("commit", "-q", "-m", "The fixture");
        Write("projects/other.md", "# Other\n\nSomeone's work in progress, not the job's.\n");   // uncommitted before the job
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);   // git's read-only objects
            Directory.Delete(root, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Folder => Path.Combine(root, "folder");
    private string Local => Path.Combine(root, "local");
    private string Input => Path.Combine(root, "requests", "finalise-1.json");
    private string Receipt => Path.Combine(Local, "claude-headless", "finalise-summary", "finalise-1.md");
    private string ReviewFile => Path.Combine(Local, "pending.json");

    private void Write(string path, string text)
    {
        var full = Path.Combine(Folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.Replace("\r\n", "\n"));
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(Folder, path)).Replace("\r\n", "\n");

    private string Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = Folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var git = Process.Start(info)!;
        var output = git.StandardOutput.ReadToEnd();
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"git {string.Join(" ", args)}: {error}");
        return output.Replace("\r\n", "\n");
    }

    private static string Example(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "examples", name);
            if (File.Exists(file))
                return File.ReadAllText(file).Replace("\r\n", "\n");
        }
        throw new FileNotFoundException($"examples/{name} isn't above {AppContext.BaseDirectory}");
    }

    private string Settings(string example) => $$"""
        [git]
        repo = ".."
        push = false
        trailers = ["Co-Authored-By: Test <test@example.com>"]

        [review]
        file = "%EA_LOCAL%/pending.json"
        lock = "{{lockName}}"

        """ + "\n" + Example(example);

    private string? Env(string name) => name switch
    {
        "EA_LOCAL" => Local,
        "LOCALAPPDATA" => Path.Combine(root, "appdata"),
        _ => null,
    };

    private sealed class FakeClaude(Func<IReadOnlyList<string>, string, string, JsonObject> answer) : IClaudeRunner
    {
        public JsonObject Run(IReadOnlyList<string> command, string prompt, string cwd, TimeSpan timeout) => answer(command, prompt, cwd);
    }

    /// <summary>What the real Claude would do: edit the draft, the ledger and the project page (and, to show it isn't
    /// committed, a file outside what the job may change), then reply.</summary>
    private JsonObject Finalise(IReadOnlyList<string> command, string prompt, string cwd)
    {
        calls.Add((command, prompt, cwd));
        Write(Summary, Finalised);
        Write("meetings/review-ledger.md", "# Review ledger\n\n## Settled\n\n- \"The portal\" is the partner portal.\n\n## Open\n");
        Write("projects/partner-portal.md", "---\nupdated: 2026-11-25\n---\n# Partner portal\n\n## Timeline\n\n| Date | What |\n|---|---|\n| 2026-11-24 | Catch-up with Ann Lee: launch moves to January |\n");
        Write("notes/scratch.md", "touched\n");
        return new JsonObject { ["result"] = Reply, ["session_id"] = "s-1" };
    }

    /// <summary>md-inbox add, faked: the item appended to its list, as md-inbox would (uncommitted, with --no-commit).</summary>
    private (int, string) Inbox(IReadOnlyList<string> argv, string cwd)
    {
        steps.Add(argv);
        var list = argv[argv.ToList().IndexOf("add") + 1];
        var file = Path.Combine(Folder, "inbox", list + ".md");
        File.AppendAllText(file, "\n- **2026-11-25:** **" + argv[^2] + "**\n");
        return (0, $"Added to {list}.");
    }

    private (int Code, string Out, string Err) Run(string kind, Func<IReadOnlyList<string>, string, string, JsonObject> claude,
        Func<IReadOnlyList<string>, string, (int, string)> step, string? input = null)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        input ??= Input;
        var code = Cli.Run(["--config", Path.Combine(Folder, "config", "claude-headless.toml"), "job", kind, "--input", input], new CliHost
        {
            Out = output, Err = errors, Env = Env, Queue = new JobQueue(Path.Combine(root, "headless")),
            Claude = new FakeClaude(claude), RunStep = step,
            Today = () => new DateOnly(2026, 11, 25),
            Self = () => ["claude-headless.exe", "job", kind, "--input", input],
        });
        return (code, output.ToString(), errors.ToString());
    }

    private void Request(JsonObject request)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Input)!);
        File.WriteAllText(Input, request.ToJsonString());
    }

    private void FinaliseSetup()
    {
        File.WriteAllText(Path.Combine(Folder, "config", "claude-headless.toml"), Settings("finalise-summary.toml"));
        Request(new JsonObject
        {
            ["summary"] = Summary,
            ["answers"] = new JsonArray(new JsonObject { ["question"] = "Is \"the portal\" the partner portal or the customer portal?", ["answer"] = "The partner portal." }),
            ["waiting_fors"] = new JsonArray("Ann Lee to send the tag list."),
            ["dictation"] = new JsonArray("Claude, please add an action for me to book the budget review"),
        });
    }

    private JsonArray Queue() => File.Exists(ReviewFile) ? JsonNode.Parse(File.ReadAllText(ReviewFile))!["items"]!.AsArray() : [];

    [Fact]
    public void Finalise_summary_commits_what_it_changed_and_nothing_else()
    {
        FinaliseSetup();
        var head = Git("rev-parse", "HEAD").Trim();
        var (code, output, errors) = Run("finalise-summary", Finalise, Inbox);
        Assert.True(code == 0, errors + output);

        // Claude ran in the folder, could read and edit only the summaries, the ledger and project pages, and was told
        // the draft, the answers, the ticks and today.
        var (command, prompt, cwd) = Assert.Single(calls);
        Assert.Equal(Path.GetFullPath(Folder), Path.GetFullPath(cwd));
        Assert.Equal("Read,Glob,Grep,Edit", command[command.ToList().IndexOf("--tools") + 1]);
        Assert.Equal("Read,Glob,Grep,Edit(meetings/**/summaries/**),Edit(meetings/review-ledger.md),Edit(projects/**)",
            command[command.ToList().IndexOf("--allowedTools") + 1]);
        Assert.Contains("--restricted", command);
        Assert.Contains("- The draft: " + Summary, prompt);
        Assert.Contains("\"answer\":\"The partner portal.\"", prompt);
        Assert.Contains("[\"Ann Lee to send the tag list.\"]", prompt);
        Assert.Contains("Today: Wednesday 25 November 2026", prompt);
        Assert.Contains("questions in a draft meeting summary", prompt);
        Assert.Contains("[AB] questions", prompt);
        Assert.Contains("{\"list\": \"waiting\", \"text\":", prompt);   // the example's braces, {{ and }} in the settings

        // Each INBOX line through md-inbox, uncommitted; an empty section left out.
        Assert.Equal(2, steps.Count);
        Assert.EndsWith(Path.Combine("appdata", "Programs", "md-inbox", "md-inbox.exe"), steps[0][0]);
        Assert.Equal(["--config", "md-inbox.toml", "add", "waiting", "[Waiting: Ann Lee, 2 Dec 2026] The tag list. From the 24 Nov 2026 catch-up.", "--no-commit"], steps[0].Skip(1));
        Assert.Equal(["--config", "md-inbox.toml", "add", "mine", "--section=Act",
            "[Action] Book the budget review. Dictated in the 24 Nov 2026 catch-up: \"Claude, please add an action for me to book the budget review\".", "--no-commit"], steps[1].Skip(1));

        // One commit: the summary, the ledger, the project page and the two lists; not the work in progress that was
        // there before, nor the file outside what the job may change.
        Assert.Equal(head, Git("rev-parse", "HEAD~1").Trim());
        Assert.Equal(["inbox/mine.md", "inbox/waiting.md", "meetings/catch-ups/ann-lee/summaries/2026-11-24.md", "meetings/review-ledger.md", "projects/partner-portal.md"],
            Git("show", "--name-only", "--format=", "HEAD").Trim().Split('\n').Order(StringComparer.Ordinal));
        var message = Git("log", "-1", "--format=%B");
        Assert.StartsWith($"Finalise the summary {Summary} with Alex's answers\n\nWritten automatically by claude-headless job finalise-summary. Inbox items added: [Waiting: Ann Lee", message);
        Assert.Contains("Co-Authored-By: Test <test@example.com>", message);
        Assert.Equal(["?? config/", "M notes/scratch.md", "M projects/other.md"], Git("status", "--porcelain").Trim().Split('\n').Select(l => l.Trim()).Order(StringComparer.Ordinal));
        Assert.Equal(Finalised.Replace("\r\n", "\n"), Read(Summary));

        // The receipt, outside the folder, for the page to read: JSON values, so any YAML or JSON reader takes them.
        var receipt = File.ReadAllText(Receipt);
        var commit = Git("rev-parse", "--short", "HEAD").Trim();
        Assert.StartsWith("---\nsummary: \"meetings/catch-ups/ann-lee/summaries/2026-11-24.md\"\nfinalised: \"2026-11-25\"\n" +
                          $"commit: \"{commit}\"\nchanged: [\"inbox/mine.md\",\"inbox/waiting.md\",", receipt);
        Assert.Contains("\ninbox-not-added: []\nnot-applied: []\nnot-committed: [\"notes/scratch.md\"]\ncommit-error: \"\"\n---\n- meetings/catch-ups", receipt);

        // Everything applied: no review item. The input and the snapshot are gone; running it again does nothing.
        Assert.Empty(Queue());
        Assert.False(File.Exists(Input));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "headless", "snapshots")));
        FinaliseSetup();
        Assert.Contains("exists already", Run("finalise-summary", Finalise, Inbox).Out);
        Assert.Single(calls);
    }

    [Fact]
    public void Finalise_summary_leaves_a_review_item_when_something_wasnt_applied()
    {
        FinaliseSetup();
        var (code, _, _) = Run("finalise-summary", (command, prompt, cwd) =>
        {
            var answer = Finalise(command, prompt, cwd);
            answer["result"] = Reply.Replace("NOT APPLIED:\n- none", "NOT APPLIED:\n- No project page for partner-portal-2: not created");
            return answer;
        }, (argv, cwd) => argv.Contains("--section=Act") ? (2, "--section 'Act' matches no section. Sections: Actions, Someday.") : Inbox(argv, cwd));
        Assert.Equal(0, code);
        var item = Assert.Single(Queue())!.AsObject();
        Assert.Equal(("finalise-summary", "needs-you"), ((string?)item["source"], (string?)item["type"]));
        Assert.Equal($"Finalised, with something left for you: {Summary}", (string?)item["title"]);
        Assert.Equal("No project page for partner-portal-2: not created", (string?)item["not_applied"]);
        Assert.StartsWith("[Action] Book the budget review.", (string?)item["inbox_not_added"]);
        Assert.Contains("(md-inbox exited 2: --section 'Act' matches no section.", (string?)item["inbox_not_added"]);
        Assert.False(item.ContainsKey("commit_error"));   // a "?" field with nothing in it is left out
        Assert.Equal(Git("rev-parse", "--short", "HEAD").Trim(), (string?)item["commit"]);
        Assert.DoesNotContain("inbox/mine.md", Git("show", "--name-only", "--format=", "HEAD"));   // not added, so not changed
    }

    [Fact]
    public void Finalise_summary_skips_what_isnt_a_draft_and_changes_nothing()
    {
        FinaliseSetup();
        var head = Git("rev-parse", "HEAD").Trim();
        var (code, _, _) = Run("finalise-summary", (_, _, _) => new JsonObject { ["result"] = "SKIP: it has no status: draft and no [DRAFT]: finalised already" }, Inbox);
        Assert.Equal(0, code);
        Assert.Equal(head, Git("rev-parse", "HEAD").Trim());
        Assert.Empty(steps);
        var item = Assert.Single(Queue())!;
        Assert.Equal(("skipped", "it has no status: draft and no [DRAFT]: finalised already"), ((string?)item["type"], (string?)item["reason"]));
        Assert.False(File.Exists(Receipt));
        Assert.False(File.Exists(Input));
    }

    [Fact]
    public void A_usage_limit_half_way_through_still_commits_the_first_attempts_edits_on_the_rerun()
    {
        FinaliseSetup();
        var reset = DateTimeOffset.Now.AddHours(1);
        var (code, output, _) = Run("finalise-summary", (_, _, _) =>
        {
            Write(Summary, Finalised);   // Claude got this far, then met the limit
            throw new LimitReachedException(reset, "limit");
        }, Inbox);
        Assert.Equal(0, code);
        Assert.Contains("usage limit, queued again", output);
        Assert.Single(new JobQueue(Path.Combine(root, "headless")).Pending());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "headless", "snapshots")));   // kept for the rerun
        Assert.True(File.Exists(Input));

        // The rerun: the summary is already as Claude left it, so this attempt changes only the ledger. Both are the job's.
        (code, _, _) = Run("finalise-summary", (command, prompt, cwd) =>
        {
            calls.Add((command, prompt, cwd));
            Write("meetings/review-ledger.md", "# Review ledger\n\n## Settled\n\n- \"The portal\" is the partner portal.\n\n## Open\n");
            return new JsonObject { ["result"] = "CHANGED:\n- meetings/review-ledger.md: settled\nINBOX:\nnone\nNOT APPLIED:\nnone" };
        }, Inbox);
        Assert.Equal(0, code);
        Assert.Equal(["meetings/catch-ups/ann-lee/summaries/2026-11-24.md", "meetings/review-ledger.md"],
            Git("show", "--name-only", "--format=", "HEAD").Trim().Split('\n').Order(StringComparer.Ordinal));
        Assert.Empty(steps);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "headless", "snapshots")));
    }

    [Fact]
    public void Recommend_writes_recommendations_onto_items_without_one_and_touches_nothing_else()
    {
        File.WriteAllText(Path.Combine(Folder, "config", "claude-headless.toml"), Settings("recommend.toml"));
        var queue = new QueueFile(ReviewFile, lockName);
        var bare = queue.Add("ev", "question", new JsonObject { ["title"] = "Wed 25 Nov: work plan says office, no Travel block" });
        var has = queue.Add("claims", "claim", new JsonObject { ["title"] = "A claim", ["recommendation"] = "Approve" });
        var hard = queue.Add("transcripts", "transcript", new JsonObject { ["title"] = "A transcript" });
        var items = new JsonArray(queue.Items().Select(i => { i["guide"] = "Raise it, then mark it done."; return (JsonNode)i; }).ToArray());
        Request(new JsonObject { ["items"] = items });
        var head = Git("rev-parse", "HEAD").Trim();

        var (code, output, errors) = Run("recommend", (command, prompt, cwd) =>
        {
            calls.Add((command, prompt, cwd));
            return new JsonObject
            {
                ["result"] = "RECOMMENDATIONS:\n```json\n" +
                             $"{{\"id\": \"{bare}\", \"source\": \"ev\", \"recommendation\": \"Add a Travel block for Wednesday\", \"reasons\": [\"The work plan says office\", \"No Travel block on 25 Nov\"]}}\n" +
                             $"{{\"id\": \"{has}\", \"source\": \"claims\", \"recommendation\": \"Reject it\", \"reasons\": [\"made up\"]}}\n" +
                             "not json at all\n```\n" +
                             $"CANNOT:\n{{\"id\": \"{hard}\", \"source\": \"transcripts\", \"why\": \"It turns on who was in the room.\"}}\n",
            };
        }, (argv, cwd) =>
        {
            // review-queue update, faked with the queue's own Update: the JSON arguments as the real one would parse them.
            steps.Add(argv);
            var at = argv.ToList().IndexOf("update");
            var match = JsonNode.Parse(argv[argv.ToList().IndexOf("--match") + 1])!.AsObject();
            var set = JsonNode.Parse(argv[argv.ToList().IndexOf("--set") + 1])!.AsObject();
            return (0, new QueueFile(ReviewFile, lockName).Update(argv[at + 1], match, set).ToString());
        });
        Assert.True(code == 0, errors + output);

        var (command, prompt, cwd) = Assert.Single(calls);
        Assert.Equal(Path.GetFullPath(Folder), Path.GetFullPath(cwd));
        Assert.Equal("Read,Glob,Grep", command[command.ToList().IndexOf("--tools") + 1]);
        Assert.DoesNotContain(command, a => a.Contains("Edit", StringComparison.Ordinal) || a.Contains("Bash", StringComparison.Ordinal));
        Assert.Contains($"\"id\":\"{bare}\"", prompt);
        Assert.Contains("\"guide\":\"Raise it, then mark it done.\"", prompt);

        var after = new QueueFile(ReviewFile, lockName).Items().ToDictionary(i => (string)i["id"]!);
        Assert.Equal(3, after.Count);   // none added, none done: only fields written
        Assert.Equal("Add a Travel block for Wednesday", (string?)after[bare]["recommendation"]);
        Assert.Equal(["The work plan says office", "No Travel block on 25 Nov"], after[bare]["reasons"]!.AsArray().Select(r => (string)r!));
        Assert.Equal("claude-headless job recommend, 2026-11-25", (string?)after[bare]["recommended_by"]);
        Assert.Equal("Approve", (string?)after[has]["recommendation"]);   // it had one: left alone
        Assert.False(after[has].AsObject().ContainsKey("reasons"));
        Assert.Equal("It turns on who was in the room.", (string?)after[hard]["no_recommendation"]);
        Assert.False(after[hard].AsObject().ContainsKey("recommendation"));

        var receipt = File.ReadAllText(Path.Combine(Local, "claude-headless", "recommend", "finalise-1.md"));
        Assert.StartsWith($"---\nrecommended: [\"{bare}\"]\nnot-written: [\"{has} (review-queue said: 0)\",\"not json at all (not a JSON object)\"]\ncannot-recommend: [\"{hard}\"]\ncannot-not-written: []\n", receipt);
        Assert.Equal(head, Git("rev-parse", "HEAD").Trim());   // nothing committed
        Assert.Equal(["?? config/", "M projects/other.md"], Git("status", "--porcelain").Trim().Split('\n').Select(l => l.Trim()).Order(StringComparer.Ordinal));
        Assert.Equal(3, after.Count);
    }

    [Fact]
    public void Queue_puts_a_job_on_the_queue_for_the_runner_and_now_starts_it()
    {
        FinaliseSetup();
        var output = new StringWriter { NewLine = "\n" };
        var started = new List<string>();
        var host = new CliHost
        {
            Out = output, Err = output, Env = Env, Queue = new JobQueue(Path.Combine(root, "headless")),
            ScheduledExe = () => @"C:\tools\claude-headless-background.exe", BackgroundExe = () => @"C:\tools\claude-headless-background.exe",
            StartQueue = started.Add,
        };
        var config = Path.Combine(Folder, "config", "claude-headless.toml");
        Assert.Equal(0, Cli.Run(["--config", config, "queue", "finalise-summary", "--input", Input, "--now"], host));
        Assert.Equal(0, Cli.Run(["--config", config, "queue", "finalise-summary", "--input", Input], host));
        var job = Assert.Single(new JobQueue(Path.Combine(root, "headless")).Pending());
        Assert.Equal([@"C:\tools\claude-headless-background.exe", "--config", config, "job", "finalise-summary", "--input", Input], job.Argv);
        Assert.Equal(Path.Combine(Folder, "config"), job.Cwd);
        Assert.Equal([@"C:\tools\claude-headless-background.exe"], started);
        Assert.Contains("is queued already", output.ToString());
        Assert.Equal(2, Cli.Run(["--config", config, "queue", "nope", "--input", Input], host));
        Assert.Equal(2, Cli.Run(["--config", config, "queue", "recommend", "--input", Path.Combine(root, "missing.json")], host));
    }

    [Theory]
    [InlineData("meetings/**/summaries/*.md", "meetings/catch-ups/ann-lee/summaries/2026-11-24.md", true)]
    [InlineData("meetings/**/summaries/*.md", "meetings/summaries/2026-11-24.md", true)]
    [InlineData("meetings/**/summaries/*.md", "meetings/catch-ups/ann-lee/2026-11-24.md", false)]
    [InlineData("meetings/**/summaries/*.md", "meetings/a/summaries/deeper/x.md", false)]
    [InlineData("projects/*.md", "Projects/Partner-Portal.md", true)]
    [InlineData("projects/*.md", "projects/archive/old.md", false)]
    [InlineData("inbox/*.md", "inbox/closed/2026-11.md", false)]
    [InlineData("meetings/review-ledger.md", "meetings/review-ledger.md", true)]
    [InlineData("meetings/review-ledger.md", "meetings/review-ledgerXmd", false)]
    [InlineData("**", "anything/at/all.md", true)]
    public void Globs_match_repository_paths(string glob, string path, bool matches) => Assert.Equal(matches, Jobs.Glob(glob).IsMatch(path));

    [Fact]
    public void Json_format_and_items_give_commands_valid_json()
    {
        var item = JsonNode.Parse("{\"id\": \"a1\", \"why\": \"He said \\\"no\\\"\", \"reasons\": [\"one\", \"two\"], \"n\": 3}")!.AsObject();
        object? Value(string name) => name.StartsWith("item.", StringComparison.Ordinal) ? Jobs.ItemField(item, name[5..]) : null;
        Assert.Equal("{\"id\": \"a1\", \"why\": \"He said \\\"no\\\"\", \"reasons\": [\"one\",\"two\"], \"n\": 3, \"x\": null}",
            Jobs.Fill("{{\"id\": {item.id:json}, \"why\": {item.why:json}, \"reasons\": {item.reasons:json}, \"n\": {item.n:json}, \"x\": {item.x:json}}}", Value));
        Assert.Equal("one; two", Jobs.Fill("{item.reasons}", Value));
        Assert.Null(Jobs.Fill("--section={item.section}?", Value));
        Assert.Equal("\"2026-11-25\"", Jobs.Json(new DateOnly(2026, 11, 25)));
        Assert.Equal("[\"she'll send it, \\\"next week\\\"\",\"café\"]", Jobs.Json(new List<string> { "she'll send it, \"next week\"", "café" }));
    }

    [Theory]
    [InlineData("commit_changed = [\"a/*.md\"]", "commit_changed needs commit")]
    [InlineData("commit_changed = [\"a/*.md\"]\ncommit = \"x\"\nfresh_work = true", "fresh_work would empty the folder")]
    [InlineData("sections = [\"A\"]\n[[job.each]]\nname = \"e\"\nsection = \"B\"\nrun = [\"x\"]", "section 'B' isn't one of its sections")]
    [InlineData("sections = [\"A\"]\nbody = \"A\"\n[[job.each]]\nname = \"e\"\nsection = \"A\"\nrun = [\"x\"]", "section 'A' is the body")]
    [InlineData("sections = [\"A\"]\n[[job.each]]\nname = \"e\"\nsection = \"A\"\nrun = [\"x\"]\nwhen = \"y\"", "unknown setting 'when'")]
    public void Bad_settings_for_the_new_keys_are_refused(string keys, string message)
    {
        var file = Path.Combine(Folder, "config", "bad.toml");
        File.WriteAllText(file, $"[[job]]\nname = \"j\"\noutput = \"o.md\"\nwork = \"w\"\nprompt = \"p.md\"\n{keys}\n");
        var error = Assert.Throws<UsageException>(() => JobsConfig.Load(file, Env));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void Fresh_work_never_empties_a_git_repository()
    {
        File.WriteAllText(Path.Combine(Folder, "config", "claude-headless.toml"),
            "[review]\nfile = \"%EA_LOCAL%/pending.json\"\n\n[[job]]\nname = \"wipe\"\noutput = \"%EA_LOCAL%/o.md\"\nwork = \"..\"\nfresh_work = true\n" +
            "failed = { source = \"wipe\", type = \"failed\", title = \"x\", error = \"{error}\" }\n[[job.turn]]\ntext = \"hi\"\n");
        Request(new JsonObject());
        var (code, _, errors) = Run("wipe", (_, _, _) => new JsonObject { ["result"] = "hello" }, Inbox);
        Assert.Equal(1, code);
        Assert.Contains("a git repository: not done", errors);
        Assert.True(File.Exists(Path.Combine(Folder, Summary)));
    }
}
