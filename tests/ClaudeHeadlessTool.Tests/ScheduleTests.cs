using WinBackground;

namespace ClaudeHeadlessTool.Tests;

/// <summary>0.5.0: the queue task built from the settings' [schedule] (background-tools-lib's shared keys), the old flags
/// as aliases, and drift shown by autostart status.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ch-schedule-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTask task = new();

    public ScheduleTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    private sealed class FakeTask : ITask
    {
        public TaskSchedule? Registered;
        public string? Description;
        public string Name => "claude-headless run-queue";
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir) => Enable(exe, args, workingDir, TaskSchedule.Watcher);
        public string Enable(string exe, IReadOnlyList<string> args, string? workingDir, TaskSchedule schedule)
        {
            Registered = schedule;
            Description = schedule.Fingerprint is { } f ? ScheduleDrift.WithFingerprint("Runs the queue.", f) : "Runs the queue.";
            return $"{exe} {string.Join(' ', args)}";
        }
        public bool Disable() => Registered is not null;
        public string State = "Ready";
        public int Starts, Stops;
        public bool Stop()
        {
            Stops++;
            return true;
        }
        public bool Start()
        {
            Starts++;
            return Registered is not null;
        }
        public IReadOnlyDictionary<string, string>? Status() => Registered is null ? null
            : new Dictionary<string, string> { ["state"] = State, ["description"] = Description ?? "" };
    }

    private string Settings(string schedule)
    {
        var path = Path.Combine(dir, "claude-headless.toml");
        File.WriteAllText(path, schedule);
        return path;
    }

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = Cli.Run(args, new CliHost { Out = output, Err = error, Task = () => task, BackgroundExe = () => "bg.exe" });
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void No_schedule_keeps_every_15_minutes_logon_and_a_minute_after_unlock()
    {
        var (exit, output, _) = Run("--config", Settings(""), "autostart", "on");
        Assert.Equal(0, exit);
        Assert.Equal(TimeSpan.FromMinutes(15), task.Registered!.Every);
        Assert.True(task.Registered.AtLogon);
        Assert.Equal(TimeSpan.FromMinutes(1), task.Registered.AfterUnlock);
        Assert.Equal(TimeSpan.FromHours(4), task.Registered.TimeLimit);
        Assert.Contains("runs every 15 minutes, at logon and 1 minute(s) after each unlock", output);
    }

    [Fact]
    public void Schedule_table_sets_the_triggers()
    {
        var (exit, _, _) = Run("--config", Settings("[schedule]\nevery_minutes = 30\nafter_unlock_minutes = 0\n"), "autostart", "on");
        Assert.Equal(0, exit);
        Assert.Equal(TimeSpan.FromMinutes(30), task.Registered!.Every);
        Assert.Null(task.Registered.AfterUnlock);
        Assert.Equal("v1 job every=30 daily=- logon=yes unlock=0", task.Registered.Fingerprint);
    }

    [Fact]
    public void Old_flag_alone_still_works_but_not_with_the_same_key()
    {
        Assert.Equal(0, Run("--config", Settings(""), "autostart", "--every", "20", "on").Exit);
        Assert.Equal(TimeSpan.FromMinutes(20), task.Registered!.Every);
        var (exit, _, err) = Run("--config", Settings("[schedule]\nevery_minutes = 30\n"), "autostart", "--every", "20", "on");
        Assert.Equal(2, exit);
        Assert.Contains("both set", err);
    }

    [Fact]
    public void Run_gating_keys_and_unknown_keys_are_refused()
    {
        Assert.Contains("isn't used by claude-headless", Run("--config", Settings("[schedule]\nhours = \"08:00-18:00\"\n"), "autostart", "on").Err);
        Assert.Equal(2, Run("--config", Settings("[schedule]\nevery = 5\n"), "autostart", "on").Exit);
    }

    [Fact]
    public void Status_says_when_the_settings_changed_since_registration()
    {
        Run("--config", Settings(""), "autostart", "on");
        Assert.DoesNotContain("settings changed", Run("--config", Settings(""), "autostart", "status").Out);
        Assert.Contains("settings changed since this task was registered: run `claude-headless autostart on`",
            Run("--config", Settings("[schedule]\nafter_unlock_minutes = 0\n"), "autostart", "status").Out);
    }

    [Fact]
    public void Restart_starts_the_queue_task_but_never_stops_a_run_in_progress()
    {
        Assert.Contains("nothing to restart", Run("autostart", "restart").Out);
        Run("--config", Settings(""), "autostart", "on");
        var (exit, output, _) = Run("autostart", "restart");
        Assert.Equal(0, exit);
        Assert.Contains("Started 'claude-headless run-queue' now", output);
        Assert.Equal(1, task.Starts);

        task.State = "Running";   // a job under way: left to finish with the version it started with
        (exit, output, _) = Run("autostart", "restart");
        Assert.Equal(0, exit);
        Assert.Contains("is running the queue now", output);
        Assert.Equal((1, 0), (task.Starts, task.Stops));
    }
}
