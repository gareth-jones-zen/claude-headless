using System.Text.Json.Nodes;
using ClaudeHeadless;

namespace ClaudeHeadlessTool.Tests;

/// <summary>A job of several turns (0.3.0): extracts, prepare steps, an emptied work folder, a body template, a table
/// merged without changing its rows, carrying on after a usage limit. A made-up monthly report.</summary>
public sealed class ConversationJobTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ch-conv-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<(IReadOnlyList<string> Command, string Prompt, string Cwd)> calls = [];
    private readonly List<(IReadOnlyList<string> Argv, string Cwd)> steps = [];
    private readonly List<string> notes = [];

    private const string Settings = """
        [review]
        file = "queue/pending.json"
        lock = "ch-conv-tests"

        [[job]]
        name = "report"
        values = { model = "test-model" }
        output = "out/{date:yyyy-MM}.md"
        work = "work/{date:yyyy-MM}"
        fresh_work = true
        claude = ["-p", "--output-format", "json"]
        body_template = "review.md"
        table = { file = "series.csv", turn = 1, block = '(?s)```(?:csv)?\n(month,.*?)```' }
        frontmatter = ["report: {date:yyyy-MM}", "session: {turn.1.session}", "model: {value.model}"]
        commit_also = ["kept/{date:yyyy-MM}.{extract.kind}"]
        review = { source = "report", type = "review", title = "Review {date:yyyy-MM}", file = "{output}", new_rows = "{table.added}?", changes = "{table.changes}?" }
        failed = { source = "report", type = "failed", title = "Review {date:yyyy-MM} failed", error = "{error}" }
        notify = { title = "Report reviewed", text = "{date:yyyy-MM} is in" }

        [[job.extract]]
        name = "first"
        file = "pack.md"
        upto = '## LATER'
        pattern = '(?s)### Message 1\b.*?```\n(.*?)```'
        [[job.extract]]
        name = "second"
        file = "pack.md"
        upto = '## LATER'
        pattern = '(?s)### Message 2\b.*?```\n(.*?)```'
        [[job.extract]]
        name = "headlines"
        field = "body"
        tidy = true
        after = '(?i)main points[^\n:]*:'
        before = '(?i)\n\s*(thanks|regards)\b'
        [[job.extract]]
        name = "kind"
        field = "attachments"
        pattern = '(?i)"name":"[^"]*\.(pptx|pdf)"'

        [[job.prepare]]
        run = ["convert", "-in", "{input.path}", "-pdf", "{work}/deck.pdf"]
        [[job.prepare]]
        copy = "series.csv"
        to = "{work}/series.csv"

        [[job.turn]]
        text = "Read the files.\n\n{extract.first}"
        [[job.turn]]
        text = "{extract.second}"
        replace = { "<paste here>" = "{extract.headlines}" }
        """;

    private const string Pack = "# Pack\n\n### Message 1 - read\n\n```\nDo the read.\n```\n\n### Message 2 - compare\n\n```\nCompare with:\n<paste here>\n```\n\n" +
                                "## LATER\n\n### Message 1 - quarterly\n\n```\nNot this one.\n```\n";

    public ConversationJobTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings.Replace("ch-conv-tests", "ch-" + Guid.NewGuid().ToString("N")[..8]));
        File.WriteAllText(Path.Combine(dir, "pack.md"), Pack.Replace("\n", "\r\n"));
        File.WriteAllText(Path.Combine(dir, "review.md"), "# Headlines\n\n{extract.headlines}\n\n# Read\n\n{turn.1}\n\n# Assessment\n\n{turn.2}\n");
        File.WriteAllText(Path.Combine(dir, "series.csv"), "month,base\n2026-09,100\n2026-10,104\n");
        File.WriteAllText(Input, new JsonObject
        {
            ["subject"] = "Monthly Report", ["received"] = "2026-12-08T09:30:00Z",
            ["body"] = "Hi all,\n\nThe main points:\n\n- Base  up\n\n\n- Churn down\n\nThanks,\nSam",
            ["attachments"] = new JsonArray(new JsonObject { ["name"] = "Monthly Report.pptx", ["path"] = Path.Combine(dir, "deck.pptx") }),
        }.ToJsonString());
        Directory.CreateDirectory(Path.Combine(dir, "saved-attachments"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }

    private string Input => Path.Combine(dir, "saved.json");
    private string Work => Path.Combine(dir, "work", "2026-12");

    private sealed class FakeClaude(Func<IReadOnlyList<string>, string, string, JsonObject> answer) : IClaudeRunner
    {
        public JsonObject Run(IReadOnlyList<string> command, string prompt, string cwd, TimeSpan timeout) => answer(command, prompt, cwd);
    }

    private JsonObject Answer(IReadOnlyList<string> command, string prompt, string cwd)
    {
        calls.Add((command, prompt, cwd));
        return calls.Count == 1
            ? new JsonObject { ["result"] = "\nMy read.\n\n```csv\nmonth,base\n2026-09,100\n2026-10,105\n2026-11,107\n```\n", ["session_id"] = "s-1" }
            : new JsonObject { ["result"] = "My assessment.", ["session_id"] = "s-1" };
    }

    private (int Code, string Out, string Err) Run(Func<IReadOnlyList<string>, string, string, JsonObject>? claude = null, params string[] extra)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", Path.Combine(dir, "config.toml"), "job", "report", "--input", Input, .. extra], new CliHost
        {
            Out = output, Err = errors, Env = _ => null, Queue = new JobQueue(Path.Combine(dir, "headless")),
            Claude = new FakeClaude(claude ?? Answer),
            RunStep = (argv, cwd) =>
            {
                steps.Add((argv, cwd));
                File.WriteAllText(argv[^1], "pdf");
                return (0, "made the pdf");
            },
            Notify = (title, lines) => notes.Add($"{title}: {string.Join(" ", lines)}"),
            Today = () => new DateOnly(2026, 12, 8),
            Self = () => ["claude-headless.exe", "job", "report", "--input", Input],
        });
        return (code, output.ToString(), errors.ToString());
    }

    private JsonArray Queue() => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "queue", "pending.json")))!["items"]!.AsArray();

    [Fact]
    public void Two_turns_make_one_review_and_the_table_gains_only_its_new_row()
    {
        Directory.CreateDirectory(Work);
        File.WriteAllText(Path.Combine(Work, "left-over.txt"), "from another run");
        var (code, _, err) = Run();
        Assert.True(code == 0, err);

        // Message 1 sees neither the headlines nor the input; message 2 resumes it with the headlines filled in.
        Assert.Equal("Read the files.\n\nDo the read.", calls[0].Prompt);
        Assert.Equal(["-p", "--output-format", "json"], calls[0].Command);
        Assert.Equal("Compare with:\n- Base up\n\n- Churn down", calls[1].Prompt);
        Assert.Equal(["-p", "--output-format", "json", "--resume", "s-1"], calls[1].Command);
        Assert.All(calls, c => Assert.Equal(Work, c.Cwd));
        Assert.Equal(["deck.pdf", "series.csv"], Directory.GetFiles(Work).Select(Path.GetFileName).Order());   // emptied first
        Assert.Equal(["convert", "-in", Input, "-pdf", Path.Combine(Work, "deck.pdf")], steps.Single().Argv);

        Assert.Equal("---\nreport: 2026-12\nsession: s-1\nmodel: test-model\n---\n\n# Headlines\n\n- Base up\n\n- Churn down\n\n# Read\n\n" +
                     "My read.\n\n```csv\nmonth,base\n2026-09,100\n2026-10,105\n2026-11,107\n```\n\n# Assessment\n\nMy assessment.\n",
            File.ReadAllText(Path.Combine(dir, "out", "2026-12.md")));
        Assert.Equal("month,base\n2026-09,100\n2026-10,104\n2026-11,107\n", File.ReadAllText(Path.Combine(dir, "series.csv")));   // 2026-10 kept as it was

        var item = Assert.Single(Queue())!;
        Assert.Equal(("review", "2026-11,107", "2026-10: base 104 -> 105"),
            ((string?)item["type"], (string?)item["new_rows"], (string?)item["changes"]));
        Assert.Equal("Report reviewed: 2026-12 is in", Assert.Single(notes));
        Assert.False(File.Exists(Input));
        Assert.False(Directory.Exists(Path.Combine(dir, "saved-attachments")));
        Assert.False(File.Exists(Cli.Progress(Work)));
    }

    [Fact]
    public void A_usage_limit_in_turn_two_carries_on_from_turn_two()
    {
        var reset = DateTimeOffset.Now.AddHours(1);
        var (code, output, _) = Run((command, prompt, cwd) => calls.Count == 1 ? throw new LimitReachedException(reset, "limit") : Answer(command, prompt, cwd));
        Assert.Equal(0, code);
        Assert.Contains("usage limit at turn 2, queued again", output);
        Assert.Single(new JobQueue(Path.Combine(dir, "headless")).Pending());
        Assert.True(File.Exists(Cli.Progress(Work)));
        Assert.True(File.Exists(Input));

        calls.Clear();
        steps.Clear();
        Assert.Equal(0, Run((command, prompt, cwd) => { calls.Add((command, prompt, cwd)); return new JsonObject { ["result"] = "My assessment.", ["session_id"] = "s-1" }; }).Code);
        var only = Assert.Single(calls);   // turn 1 isn't run again, nor the prepare steps, and the work folder is kept
        Assert.Equal(["-p", "--output-format", "json", "--resume", "s-1"], only.Command);
        Assert.Empty(steps);
        Assert.Contains("# Read\n\nMy read.", File.ReadAllText(Path.Combine(dir, "out", "2026-12.md")));
    }

    [Fact]
    public void A_failed_prepare_step_is_a_failed_item_and_claude_is_never_asked()
    {
        var output = new StringWriter();
        var code = Cli.Run(["--config", Path.Combine(dir, "config.toml"), "job", "report", "--input", Input], new CliHost
        {
            Out = output, Err = output, Env = _ => null, Queue = new JobQueue(Path.Combine(dir, "headless")),
            Claude = new FakeClaude(Answer), RunStep = (_, _) => (1, "no .pptx attachment"), Notify = (_, _) => { },
        });
        Assert.Equal(1, code);
        Assert.Empty(calls);
        Assert.Contains("prepare step convert exited 1: no .pptx attachment", (string?)Assert.Single(Queue())!["error"]);
        Assert.True(File.Exists(Input));
    }

    [Fact]
    public void Force_runs_it_again_over_an_existing_output()
    {
        Directory.CreateDirectory(Path.Combine(dir, "out"));
        File.WriteAllText(Path.Combine(dir, "out", "2026-12.md"), "old");
        Assert.Contains("exists already", Run().Out);
        Assert.Empty(calls);
        Assert.Equal(0, Run(null, "--force").Code);
        Assert.StartsWith("---\nreport: 2026-12", File.ReadAllText(Path.Combine(dir, "out", "2026-12.md")));
    }

    [Fact]
    public void Merge_starts_a_missing_table_and_reports_a_changed_header()
    {
        var block = new System.Text.RegularExpressions.Regex(@"(?s)```\n(month,.*?)```");
        var fresh = Jobs.Merge("", "```\nmonth,base\n2026-11,1\n```", block)!.Value;
        Assert.Equal(("month,base\n2026-11,1\n", "2026-11,1"), (fresh.Text, Assert.Single(fresh.Added)));
        var changed = Jobs.Merge("month,base\r\n2026-11,1\r\n", "```\nmonth,base,new\n2026-11,1\n```", block)!.Value;
        Assert.Equal("month,base\r\n2026-11,1\r\n", changed.Text);
        Assert.Equal("header: month,base -> month,base,new", Assert.Single(changed.Changes));
        Assert.Null(Jobs.Merge("month\n", "no table here", block));
    }
}
