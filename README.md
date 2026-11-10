# claude-headless

Run Claude Code headless (`claude -p`) from background tools, and survive the usage limit.

On a subscription seat, everything you run shares one usage limit. A batch of background jobs
can hit it in minutes; the first time it happened here, 25 of 32 jobs failed one after another,
each with its own failure notification. This library turns the limit into a queue instead:

```python
from claude_headless import LimitReached, queue, run

try:
    result = run(["-p", "--output-format", "json", "--model", "opus"], prompt, cwd, timeout=1800)
    text = result["result"]
except LimitReached as limit:
    if queue.retry([sys.executable, "-m", "my_tool", "draft", path], cwd, limit.reset):
        notify("Claude usage limit reached", f"Carries on after {limit.reset:%H:%M}")
```

## Install

```
pip install git+https://github.com/gareth-jones-zen/win-background git+https://github.com/gareth-jones-zen/claude-headless
claude-headless autostart on      # run the queue every 15 minutes and at logon (Windows)
```

Needs Claude Code installed and signed in (`claude` on the PATH), and Python 3.11+.
[win-background](https://github.com/gareth-jones-zen/win-background) does the scheduled task,
the runner lock and the log.

## How it works, and why

- **One queue per seat**, in `%LOCALAPPDATA%\claude-headless\` (or `CLAUDE_HEADLESS_HOME`), whichever tool queued the job, because they all wait on the same reset. A job is a command line and the folder to run it in, so any tool can queue one.
- **The limit is read from whichever shape it comes in**: text on stderr ("You've hit your session limit · resets 7pm (Europe/London)"), a JSON result with `is_error`, or an API 429. The reset time is read from the message; if it can't be, an hour from now.
- **Once a limit is known, later runs don't start** until it has passed (plus five minutes), so a batch queues straight away instead of failing job by job.
- **One notification per limit:** `queue.retry()` returns True only the first time a limit is seen.
- **Jobs run one at a time** after the reset, in order. `queue.add(..., front=True)` puts a job with a deadline first. The same job is never queued twice.
- **Something has to run the queue.** A tool that runs all day can call `queue.run()` (it returns at once if another runner holds the queue). Otherwise `claude-headless autostart on` runs it every 15 minutes and at logon. Without either, queued jobs wait for the next time you run `claude-headless run-queue`.
- **An auto-update briefly removes npm's `claude` shim**, which once killed a run mid-batch, so the executable is waited for, and each run turns the auto-updater off for itself.

```
claude-headless status       # the limit, and what's queued
claude-headless run-queue    # run what's queued now, if the limit has passed
```

## Tests

```
python -m unittest discover -s tests
```
