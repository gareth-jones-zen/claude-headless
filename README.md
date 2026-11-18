# claude-headless

Run Claude Code headless (`claude -p`) from background tools, and survive the usage limit.

**Two forms, one queue.** The **.NET exe** (`claude-headless.exe`, since 0.2.0: Windows x64, Native AOT, no runtime)
has the queue commands and **jobs defined in settings**. The **Python package** below (`src/claude_headless`) is the
original, for Python scripts that run Claude themselves; both use the same queue, so either runner runs either's jobs.

## The exe

```
claude-headless status            the usage limit, if one is in force, and what's queued
claude-headless run-queue         run queued jobs one at a time, until done or the limit is back
claude-headless autostart on      run the queue every 15 minutes and at logon (off, status)
claude-headless --config FILE job KIND --input FILE [--out FILE]
```

Install: unzip `claude-headless-<version>-win-x64.zip` from
[Releases](https://github.com/gareth-jones-zen/claude-headless/releases) to a folder that will stay put, such as
`%LOCALAPPDATA%\Programs\claude-headless`. The queue commands are background-tools-lib's ClaudeHeadless.

### Jobs: input, prompt, output

A `[[job]]` is "take this input, give it to Claude with this prompt, check the reply, put the output here", all in
settings, so a new kind of job is a new table, not new code. Something (a mail-triage queue handler, say) puts
`claude-headless --config FILE job KIND --input saved.json` on the queue; the runner runs it.

- **The input** is a JSON object (an email, as mail-triage saves one): its fields are `{input.FIELD}` in templates.
- **The prompt** is a template file, with `{input.*}`, `{value.NAME}` (text from the settings), `{context.NAME}` (a
  file's text, such as your rules) and `{date}`. Values are put in as they are, braces and all; `{{` and `}}` are
  literal braces in the template.
- **Claude** runs with the settings' arguments (`claude`) in an empty folder (`work`), so it reads nothing it
  isn't given. Every run sets `CLAUDE_HEADLESS=1`, so your Claude Code hooks can tell it from your own sessions.
- **The reply is checked**: the `sections` it must have ("NAME:" lines; "NAME?" for optional), which one is the
  output's `body`, patterns the body must match (`must_match`), and `replace`ments applied to it. Other sections
  become lists of items (`{LEFT OUT}`, joined with "; "), "none" and the like left out.
- **The output** goes to `output` (a path template), with `frontmatter` lines (a line ending `?` is left out when
  anything in it is empty) and a `marker` line before the body. It's committed with `[git]` (`commit`,
  `commit_body`), a `review` item is added to the `[review]` queue, and the input is deleted.
- **If the output is already there**, it does nothing. **At the usage limit** the job puts itself back on the queue
  for after the reset. **A reply of the wrong shape**, or Claude failing, adds the `failed` item and exits 1, keeping
  the input for a rerun. **`--out FILE`** writes there instead: no commit, no item, the input kept.

```toml
base = "base-config.toml"       # optional: shared settings; [git] and [review] can come from it

[git]                           # repo, push, trailers
[review]                        # file, lock: background-tools-lib's ReviewQueue

[[job]]
name = "marketing"
prompt = "team-chat/post-prompt.md"
context = { rules = "team-chat/marketing-picks.md" }
values = { what = "the ExCo Marketing update", title = "Marketing updates chat" }
date = { field = "subject", pattern = '\b(\d{1,2})/(\d{1,2})\b', otherwise = "received" }   # day/month in the subject
output = "../comms/team-chat/marketing/{date:yyyy-MM-dd}.md"
work = "%LOCALAPPDATA%/my-tools/marketing-work"
claude = ["-p", "--output-format", "json", "--model", "opus", "--tools", "Read", "--restricted", "--strict-mcp-config"]
sections = ["POST", "LEFT OUT", "CHECK?"]
body = "POST"
must_match = ['\AFrom ', '(?m)^- ']
replace = { "—" = "-" }
frontmatter = ['source: {input.from} "{input.subject}"', "status: draft", "left-out: {LEFT OUT|nothing}", "check: {CHECK}?"]
marker = "[DRAFT]"
commit = "Draft post for the marketing chat from {date:dd/MM} [DRAFT]"
review = { source = "team-chat", type = "draft", title = "Marketing chat: post from {date:dd/MM}", file = "{output}" }
failed = { source = "team-chat", type = "failed", title = "Marketing chat: couldn't draft {date:dd/MM}", reason = "{error}" }
```

### Conversations: several turns, files to read, a table to keep (0.3.0)

A job can also be a conversation with files, such as a monthly report reviewed in two messages:

- **`[[job.turn]]`s** in place of `prompt`: each turn's `prompt` (a file) or `text`, with `replace`ments filled in
  after the template (`{ "<paste here>" = "{extract.headlines}" }`). Turn 2 on resumes turn 1's conversation, and
  each turn's reply is `{turn.N}` (its session `{turn.N.session}`). **At a usage limit, the rerun carries on from
  the turn that was cut off**, from a progress file kept beside the work folder (not in it, where Claude reads).
- **`[[job.extract]]`s** take text out of the input's `field` or a `file`: cut at `upto`, `tidy` the spacing, keep
  what's `after` and `before` two patterns, or a `pattern`'s first group (no match is a failure). `{extract.NAME}`.
- **`[[job.prepare]]`s** run before turn 1, in the settings' folder: `run` a command (a non-zero exit fails the
  job, with what it said) or `copy` a file `to` somewhere, usually into `{work}`. **`fresh_work = true`** empties
  the work folder first, so Claude finds only what the steps put there.
- **`body_template`** is a file the output's body is filled from (`{turn.1}`, `{extract.*}`), instead of a section.
- **`table`** keeps a CSV from a reply's fenced block (`file`, `turn`, `block`): **rows it doesn't have are added;
  rows it has are never changed.** Where the reply's copy differs, `{table.changes}` says how, column by column
  (`2026-08: churn 1.2 -> 1.3`), for a person to decide; `{table.added}` is the new rows.
- **`commit_also`** commits other files the job made (those that exist), **`notify`** shows a Windows notification
  (`title`, `text`), and **`--force`** runs a job whose output is already there. `{today}`, `{work}` and
  `{input.path}` are there for any template.
- **Put a job's own keys before its `[[job.…]]` tables**: in TOML, keys after a table header belong to that table.

```toml
[[job]]
name = "report"
output = "../reports/{date:yyyy-MM}.md"
work = "%LOCALAPPDATA%/my-tools/report/{date:yyyy-MM}"
fresh_work = true
claude = ["-p", "--output-format", "json", "--model", "claude-opus-5-5", "--tools", "Read", "--restricted", "--strict-mcp-config"]
timeout_minutes = 60
body_template = "report/review.md"
table = { file = "../reports/series.csv", turn = 1, block = '(?s)```(?:csv)?\n(month,.*?)```' }
frontmatter = ["report: {date:yyyy-MM}", "session: {turn.1.session}"]
commit = "Review the {date:yyyy-MM} report"
review = { source = "report", type = "review", title = "Review {date:yyyy-MM}", new_rows = "{table.added}?", changes = "{table.changes}?" }

[[job.extract]]
name = "headlines"
field = "body"
tidy = true
after = '(?i)main points[^\n:]*:'

[[job.prepare]]
run = ["powershell", "-NoProfile", "-File", "report/deck-to-pdf.ps1", "-InputJson", "{input.path}", "-Pdf", "{work}/deck.pdf"]

[[job.turn]]
prompt = "report/first.md"
[[job.turn]]
text = "Now compare with the headlines:\n\n{extract.headlines}"
```

## The Python package

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
