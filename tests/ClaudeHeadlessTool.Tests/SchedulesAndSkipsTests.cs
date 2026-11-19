using System.Text.Json.Nodes;
using ClaudeHeadless;

namespace ClaudeHeadlessTool.Tests;

/// <summary>0.4.0: jobs started by a schedule, a reply that skips, a prepare step with nothing to do, {x:slug}, and the
/// as-written copy kept when an output isn't committed.</summary>
public sealed class SchedulesAndSkipsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ch-sched-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> prompts = [];
    private string reply = "POST:\n- Can you RSVP for the party by Fri 27/11 please\nCHECK:\n- none\n";
    private (int Exit, string Output) step = (0, "");

    private const string Settings = """
        originals = "originals"

        [review]
        file = "queue/pending.json"
        lock = "ch-sched-tests"

        [[job]]
        name = "general"
        prompt = "prompt.md"
        output = "../content/chat/{date:yyyy-MM-dd}-{input.subject:slug}.md"
        work = "work"
        sections = ["POST", "CHECK?"]
        body = "POST"
        skip = "SKIP"
        frontmatter = ["email: {input.key}", "check: {CHECK}?"]
        marker = "[DRAFT]"
        review = { source = "team-chat", type = "draft", title = "General: {input.subject}", file = "{output}" }
        skipped = { source = "team-chat", type = "skipped", title = "Not a reminder: {input.subject}", reason = "{SKIP}" }
        failed = { source = "team-chat", type = "failed", title = "Couldn't draft {input.subject}", reason = "{error}" }

        [[job]]
        name = "mining"
        prompt = "prompt.md"
        output = "../content/mining/{date:yyyy-MM}.md"
        work = "work-mining"
        failed = { source = "mining", type = "failed", title = "Mining failed", reason = "{error}" }

        [[job.prepare]]
        run = ["prepare", "{input.path}"]
        nothing_to_do_exit = 3

        [[job.schedule]]
        on = "first Wednesday"
        at = "07:30"
        input = { why = "monthly" }
        """;

    public SchedulesAndSkipsTests()
    {
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        File.WriteAllText(Config, Settings.Replace("ch-sched-tests", "ch-" + Guid.NewGuid().ToString("N")[..8]));
        File.WriteAllText(Path.Combine(dir, "config", "prompt.md"), "Subject: {input.subject}\n");
        File.WriteAllText(Input, new JsonObject
        {
            ["from"] = "hr@example.com", ["subject"] = "RE: FW: Christmas Party – RSVP by Fri 27/11!", ["received"] = "2026-11-19T10:00:00Z",
            ["body"] = "Managers, please remind your teams.", ["key"] = "<9@example.com>",
        }.ToJsonString());
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

    private string Config => Path.Combine(dir, "config", "config.toml");
    private string Input => Path.Combine(dir, "saved.json");
    private string Home => Path.Combine(dir, "headless");
    private string Draft => Path.Combine(dir, "content", "chat", "2026-11-19-christmas-party-rsvp-by-fri-27-11.md");

    private sealed class FakeClaude(Func<string, JsonObject> answer) : IClaudeRunner
    {
        public JsonObject Run(IReadOnlyList<string> command, string prompt, string cwd, TimeSpan timeout) => answer(prompt);
    }

    private CliHost Host(StringWriter output, DateTime? now = null) => new()
    {
        Out = output, Err = output, Env = _ => null, Queue = new JobQueue(Home),
        Claude = new FakeClaude(prompt =>
        {
            prompts.Add(prompt);
            return new JsonObject { ["result"] = reply, ["session_id"] = "s" };
        }),
        RunStep = (_, _) => step,
        Notify = (_, _) => { },
        Now = () => now ?? new DateTime(2026, 11, 19, 10, 0, 0),
        ScheduledExe = () => "claude-headless-background.exe",
    };

    private (int Code, string Out) Run(params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        return (Cli.Run(args, Host(output)), output.ToString());
    }

    private (int Code, string Out) At(DateTime now, params string[] args)
    {
        var output = new StringWriter { NewLine = "\n" };
        return (Cli.Run(args, Host(output, now)), output.ToString());
    }

    private JsonArray Items() => File.Exists(Path.Combine(dir, "config", "queue", "pending.json"))
        ? JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "config", "queue", "pending.json")))!["items"]!.AsArray()
        : [];

    [Theory]
    [InlineData("RE: FW: Christmas Party – RSVP by Fri 27/11!", "christmas-party-rsvp-by-fri-27-11")]
    [InlineData("Fwd:Re: GJ's team’s update", "gjs-teams-update")]
    [InlineData("  ", "untitled")]
    [InlineData("A very long subject line about the annual engagement survey closing date and reminders", "a-very-long-subject-line-about-the-annual")]
    public void Slug_makes_a_short_file_name_part(string subject, string slug)
    {
        Assert.Equal(slug, Jobs.Slug(subject));
        Assert.True(Jobs.Slug(subject).Length <= 50);
    }

    [Fact]
    public void A_draft_is_named_by_its_subject_slug_and_its_original_is_kept_when_not_committed()
    {
        var (code, output) = Run("--config", Config, "job", "general", "--input", Input);
        Assert.True(code == 0, output);
        Assert.Equal("---\nemail: <9@example.com>\n---\n[DRAFT]\n- Can you RSVP for the party by Fri 27/11 please\n", File.ReadAllText(Draft));
        // No [git]: the as-written copy is kept under `originals`, at its path below the folder above the settings.
        var original = Path.Combine(dir, "config", "originals", "content", "chat", "2026-11-19-christmas-party-rsvp-by-fri-27-11.md");
        Assert.Equal(File.ReadAllText(Draft), File.ReadAllText(original));
        Assert.Equal("draft", (string?)Assert.Single(Items())!["type"]);
        Assert.False(File.Exists(Input));
    }

    [Fact]
    public void Out_keeps_no_original()
    {
        var scratch = Path.Combine(dir, "scratch.md");
        Assert.Equal(0, Run("--config", Config, "job", "general", "--input", Input, "--out", scratch).Code);
        Assert.True(File.Exists(scratch));
        Assert.False(Directory.Exists(Path.Combine(dir, "config", "originals")));
    }

    [Fact]
    public void A_skip_reply_writes_nothing_and_says_why()
    {
        reply = "SKIP: it's an invitation to GJ alone, nothing for his team\n";
        var (code, output) = Run("--config", Config, "job", "general", "--input", Input);
        Assert.True(code == 0, output);
        Assert.Contains("skipped: it's an invitation to GJ alone", output);
        Assert.False(File.Exists(Draft));
        var item = Assert.Single(Items())!;
        Assert.Equal(("skipped", "it's an invitation to GJ alone, nothing for his team"), ((string?)item["type"], (string?)item["reason"]));
        Assert.False(File.Exists(Input));
    }

    [Fact]
    public void A_skip_with_out_keeps_the_input_and_adds_no_item()
    {
        reply = "SKIP:\n- not a reminder\n";
        var (code, output) = Run("--config", Config, "job", "general", "--input", Input, "--out", Path.Combine(dir, "scratch.md"));
        Assert.Equal(0, code);
        Assert.Contains("skipped: not a reminder", output);
        Assert.False(File.Exists(Path.Combine(dir, "scratch.md")));
        Assert.Empty(Items());
        Assert.True(File.Exists(Input));
    }

    [Fact]
    public void A_prepare_step_with_nothing_to_do_ends_quietly()
    {
        step = (3, "no new final week since 2026-11-23");
        var (code, output) = Run("--config", Config, "job", "mining", "--input", Input);
        Assert.True(code == 0, output);
        Assert.Contains("nothing to do: no new final week since 2026-11-23", output);
        Assert.Empty(prompts);
        Assert.Empty(Items());
        Assert.False(Directory.Exists(Path.Combine(dir, "content", "mining")));
        Assert.False(File.Exists(Input));

        File.WriteAllText(Input, "{}");
        step = (1, "broken");
        Assert.Equal(1, Run("--config", Config, "job", "mining", "--input", Input).Code);   // any other exit still fails
        Assert.Equal("failed", (string?)Assert.Single(Items())!["type"]);
    }

    [Theory]
    [InlineData("first Wednesday", "2026-12-02")]
    [InlineData("second Monday", "2026-12-14")]
    [InlineData("last Friday", "2026-11-27")]
    [InlineData("Thursday", "2026-11-26")]
    [InlineData("weekdays", "2026-11-20")]
    [InlineData("daily", "2026-11-20")]
    [InlineData("day 31", "2026-11-30")]
    public void A_schedule_falls_due_on_its_days(string on, string next)
    {
        var spec = ScheduleSpec.Parse(on, "07:30");
        Assert.Equal(DateOnly.Parse(next).ToDateTime(new TimeOnly(7, 30)), spec.Next(new DateTime(2026, 11, 19, 10, 0, 0)));
    }

    [Theory]
    [InlineData("fifth Wednesday", "07:30")]
    [InlineData("monthly", "07:30")]
    [InlineData("day 32", "07:30")]
    [InlineData("Wednesday", "half seven")]
    public void A_schedule_it_can_not_read_is_refused(string on, string at) =>
        Assert.Throws<FormatException>(() => ScheduleSpec.Parse(on, at));

    [Fact]
    public void Run_queue_starts_a_scheduled_job_once_when_due_and_never_for_before_it_was_on()
    {
        new JobQueue(Home).Remember(DateTimeOffset.Now.AddHours(3));   // a usage limit, so run-queue runs nothing and the queue can be read
        var (on, said) = At(new DateTime(2026, 11, 19, 10, 0, 0), "--config", Config, "schedule", "on");
        Assert.True(on == 0, said);
        Assert.Contains("mining: first Wednesday 07:30; next Wed 2 Dec 2026 07:30", said);

        At(new DateTime(2026, 11, 19, 10, 15, 0), "run-queue");   // 4 Nov fell due before it was on: not run
        At(new DateTime(2026, 12, 2, 7, 15, 0), "run-queue");
        Assert.Empty(new JobQueue(Home).Pending());

        At(new DateTime(2026, 12, 2, 8, 5, 0), "run-queue");   // the laptop was asleep at 07:30: caught up
        At(new DateTime(2026, 12, 2, 8, 20, 0), "run-queue");
        var job = Assert.Single(new JobQueue(Home).Pending());
        var input = Path.Combine(Home, "scheduled", "mining-20261202-0730.json");
        var settings = JobsConfig.Load(Config, _ => null);
        Assert.Equal(["claude-headless-background.exe", "--config", settings.Path, "job", "mining", "--input", input], job.Argv);
        Assert.Equal(settings.Dir, job.Cwd);
        var saved = JsonNode.Parse(File.ReadAllText(input))!;
        Assert.Equal(("monthly", "first Wednesday 07:30"), ((string?)saved["why"], (string?)saved["schedule"]));
        Assert.StartsWith("2026-12-02T07:30:00", (string?)saved["received"]);
        Assert.Equal(new DateOnly(2026, 12, 2), Jobs.DateOf(null, saved.AsObject()));

        At(new DateTime(2027, 1, 6, 7, 31, 0), "run-queue");
        Assert.Equal(2, new JobQueue(Home).Pending().Count);

        Assert.Equal(0, At(new DateTime(2027, 1, 6, 8, 0, 0), "--config", Config, "schedule", "off").Code);
        At(new DateTime(2027, 2, 3, 8, 0, 0), "run-queue");
        Assert.Equal(2, new JobQueue(Home).Pending().Count);
        Assert.Contains("No settings file's schedules are on", At(new DateTime(2027, 2, 3, 8, 0, 0), "schedule", "status").Out);
    }

    [Fact]
    public void Settings_that_misuse_the_new_keys_are_refused()
    {
        File.WriteAllText(Config, "[[job]]\nname = \"x\"\nprompt = \"p.md\"\noutput = \"o.md\"\nwork = \"w\"\n[[job.schedule]]\non = \"fortnightly\"\nat = \"07:30\"\n");
        var (code, output) = Run("--config", Config, "job", "x", "--input", Input);
        Assert.Equal(2, code);
        Assert.Contains("[[job.schedule]] on should be", output);
    }
}
