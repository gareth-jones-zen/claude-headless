using System.Text.Json.Nodes;
using ClaudeHeadless;

namespace ClaudeHeadlessTool.Tests;

/// <summary>claude-headless job: a temporary settings folder, a fake Claude, a queue of its own.</summary>
public sealed class JobTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ch-job-tests-" + Guid.NewGuid().ToString("N"));
    private string reply = "POST:\nFrom 13/11:\n- The new SASE pages are live – go and look\nLEFT OUT:\n- Social stats\n- The awards dinner\nCHECK:\n- none\n";
    private readonly List<string> prompts = [];

    private const string Settings = """
        [review]
        file = "queue/pending.json"
        lock = "ch-job-tests"

        [[job]]
        name = "marketing"
        prompt = "prompt.md"
        context = { rules = "picks.md" }
        values = { what = "the ExCo Marketing update", title = "Marketing chat" }
        date = { field = "subject", pattern = '\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b', otherwise = "received" }
        output = "out/{date:yyyy-MM-dd}.md"
        work = "work"
        sections = ["POST", "LEFT OUT", "CHECK?"]
        body = "POST"
        must_match = ['\AFrom ', '(?m)^- ']
        replace = { "–" = "-", "—" = "-" }
        frontmatter = [
          'source: {input.from} "{input.subject}", received {input.received:dd MMM yyyy}',
          "chat: {value.title}",
          "status: draft",
          "left-out: {LEFT OUT|nothing}",
          "check: {CHECK}?",
        ]
        marker = "[DRAFT]"
        review = { source = "team-chat", type = "draft", title = "{value.title}: post from {date:dd/MM}", chat = "marketing", file = "{output}" }
        failed = { source = "team-chat", type = "failed", title = "{value.title}: couldn't draft {date:dd/MM}", reason = "{error}", saved = "{input.path}" }
        """;

    public JobTests()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.toml"), Settings.Replace("ch-job-tests", "ch-" + Guid.NewGuid().ToString("N")[..8]));
        File.WriteAllText(Path.Combine(dir, "prompt.md"), "Rules: {context.rules}\nWhat: {value.what}\nReceived: {input.received:ddd dd MMM yyyy}\n{input.body}\nLiteral {{braces}}\n");
        File.WriteAllText(Path.Combine(dir, "picks.md"), "Pick what the team needs {not a placeholder}.");
        WriteInput("FW: ExCo Marketing update - 13/11");
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

    private void WriteInput(string subject) => File.WriteAllText(Input, new JsonObject
    {
        ["from"] = "john.lyons@example.com", ["subject"] = $"  {subject} ", ["received"] = "2026-11-14T08:05:00Z",
        ["body"] = "This week: SASE pages live. {curly} text.", ["key"] = "<1@example.com>",
    }.ToJsonString());

    private sealed class FakeClaude(Func<string, JsonObject> answer) : IClaudeRunner
    {
        public JsonObject Run(IReadOnlyList<string> command, string prompt, string cwd, TimeSpan timeout) => answer(prompt);
    }

    private (int Code, string Out, string Err) Run(Func<string, JsonObject>? claude = null, params string[] extra)
    {
        var output = new StringWriter { NewLine = "\n" };
        var errors = new StringWriter { NewLine = "\n" };
        var code = Cli.Run(["--config", Path.Combine(dir, "config.toml"), "job", "marketing", "--input", Input, .. extra], new CliHost
        {
            Out = output, Err = errors, Env = _ => null, Queue = new JobQueue(Path.Combine(dir, "headless")),
            Claude = new FakeClaude(claude ?? (p => { prompts.Add(p); return new JsonObject { ["result"] = reply }; })),
            Self = () => ["claude-headless.exe", "job", "marketing", "--input", Input],
        });
        return (code, output.ToString(), errors.ToString());
    }

    private JsonArray Queue() => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "queue", "pending.json")))!["items"]!.AsArray();

    [Fact]
    public void A_job_drafts_the_output_raises_it_and_deletes_its_input()
    {
        var (code, output, _) = Run();
        Assert.Equal(0, code);
        Assert.Contains("out/2026-11-13.md", output.Replace('\\', '/'));
        Assert.Equal(
            "---\n" +
            "source: john.lyons@example.com \"FW: ExCo Marketing update - 13/11\", received 14 Nov 2026\n" +
            "chat: Marketing chat\n" +
            "status: draft\n" +
            "left-out: Social stats; The awards dinner\n" +
            "---\n" +
            "[DRAFT]\n" +
            "From 13/11:\n" +
            "- The new SASE pages are live - go and look\n",   // the dash is his hyphen; an empty CHECK leaves its line out
            File.ReadAllText(Path.Combine(dir, "out", "2026-11-13.md")));
        Assert.Equal("Rules: Pick what the team needs {not a placeholder}.\nWhat: the ExCo Marketing update\nReceived: Sat 14 Nov 2026\n" +
                     "This week: SASE pages live. {curly} text.\nLiteral {braces}\n", Assert.Single(prompts));
        var item = Assert.Single(Queue())!;
        Assert.Equal(("team-chat", "draft", "Marketing chat: post from 13/11", "marketing"),
            ((string?)item["source"], (string?)item["type"], (string?)item["title"], (string?)item["chat"]));
        Assert.EndsWith("out/2026-11-13.md", ((string?)item["file"])!.Replace('\\', '/'));
        Assert.False(File.Exists(Input));
    }

    [Fact]
    public void An_output_already_there_is_left_alone_and_out_writes_elsewhere_without_raising_it()
    {
        Directory.CreateDirectory(Path.Combine(dir, "out"));
        File.WriteAllText(Path.Combine(dir, "out", "2026-11-13.md"), "posted");
        Assert.Contains("exists already", Run().Out);
        Assert.Empty(prompts);
        var elsewhere = Path.Combine(dir, "try.md");
        Assert.Equal(0, Run(null, "--out", elsewhere).Code);
        Assert.StartsWith("---\nsource:", File.ReadAllText(elsewhere));
        Assert.True(File.Exists(Input));   // kept
        Assert.False(File.Exists(Path.Combine(dir, "queue", "pending.json")));
    }

    [Fact]
    public void A_reply_of_the_wrong_shape_is_a_failed_item_and_the_input_is_kept()
    {
        reply = "Here's a post for you!\n- something";
        var (code, _, err) = Run();
        Assert.Equal(1, code);
        Assert.Contains("the reply has no POST: / LEFT OUT: sections", err);
        var item = Assert.Single(Queue())!;
        Assert.Equal(("failed", "Marketing chat: couldn't draft 13/11"), ((string?)item["type"], (string?)item["title"]));
        Assert.Equal(Input, (string?)item["saved"]);
        Assert.True(File.Exists(Input));

        reply = "POST:\nHello team\nLEFT OUT:\n- x";
        Assert.Contains("doesn't match", Run().Err);
    }

    [Fact]
    public void A_usage_limit_puts_the_job_back_on_the_queue()
    {
        var reset = DateTimeOffset.Now.AddHours(2);
        var (code, output, _) = Run(_ => throw new LimitReachedException(reset, "limit"));
        Assert.Equal(0, code);
        Assert.Contains("usage limit, queued again", output);
        var queued = Assert.Single(new JobQueue(Path.Combine(dir, "headless")).Pending());
        Assert.Equal(["claude-headless.exe", "job", "marketing", "--input", Input], queued.Argv);
        Assert.True(File.Exists(Input));
    }

    [Theory]
    [InlineData("FW: ExCo Marketing update - 13/11", "2026-11-13")]
    [InlineData("ExCo Marketing update", "2026-11-14")]        // no date: when it arrived
    [InlineData("Update 31/02", "2026-11-14")]                 // not a date
    [InlineData("Update 28/12", "2025-12-28")]                 // more than a week ahead: last year's
    public void The_date_comes_from_the_subject(string subject, string expected)
    {
        var rule = new DateRule("subject", new System.Text.RegularExpressions.Regex(@"\b(\d{1,2})/(\d{1,2})(?:/(\d{2,4}))?\b"), "received");
        var input = new JsonObject { ["subject"] = subject, ["received"] = "2026-11-14T08:05:00Z" };
        Assert.Equal(expected, Jobs.DateOf(rule, input).ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void Templates_fill_format_default_and_drop_optional_lines()
    {
        object? Value(string name) => name switch
        {
            "a" => "x", "empty" => "", "list" => new List<string> { "p", "q" }, "when" => "2026-11-14T08:05:00Z", _ => null,
        };
        Assert.Equal("x and p; q on 14/11", Jobs.Fill("{a} and {list} on {when:dd/MM}", Value));
        Assert.Equal("left-out: nothing", Jobs.Fill("left-out: {empty|nothing}", Value));
        Assert.Null(Jobs.Fill("check: {empty}?", Value));
        Assert.Equal("{literal}", Jobs.Fill("{{literal}}", Value));
    }

    [Fact]
    public void Status_and_the_queue_commands_still_work()
    {
        var output = new StringWriter { NewLine = "\n" };
        Assert.Equal(0, Cli.Run(["status"], new CliHost { Out = output, Queue = new JobQueue(Path.Combine(dir, "headless")) }));
        Assert.Contains("0 job(s) queued.", output.ToString());
    }
}
