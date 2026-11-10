"""One headless Claude Code run: `claude -p ... --output-format json`, with the prompt on stdin.

Learned on a laptop running it unattended (October 2026):
- **The usage limit comes back in three shapes:** plain text on stderr ("You've hit your session
  limit · resets 7pm (Europe/London)"), a JSON result with is_error, or an API 429. All become
  LimitReached, with the reset time read from the message (an hour from now if it can't be read).
- **Once a limit is known, later runs don't start** until it has passed, so a batch doesn't fail
  one job after another against it.
- **An auto-update briefly removes npm's `claude` shim**, which killed a run; the executable is
  waited for, and each run turns the auto-updater off for itself.
- **No console window** under pythonw, and the output is read as UTF-8 whatever the console's code page.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import re
import shutil
import subprocess
import time
from zoneinfo import ZoneInfo

from win_background import NO_WINDOW

LOCAL_TZ = ZoneInfo(os.environ.get("CLAUDE_HEADLESS_TZ", "Europe/London"))
LIMIT_TEXT = re.compile(r"hit your .{0,20}limit|usage limit", re.I)


class LimitReached(RuntimeError):
    def __init__(self, reset: dt.datetime, message: str):
        super().__init__(message)
        self.reset = reset


def exe() -> str:
    for _ in range(24):
        found = shutil.which("claude") or shutil.which(str(pathlib.Path(os.environ.get("APPDATA", "")) / "npm" / "claude.cmd"))
        if found:
            return found
        time.sleep(5)
    raise RuntimeError("The claude CLI isn't on the PATH (is Claude Code installed?)")


def reset_time(text: str, now: dt.datetime | None = None) -> dt.datetime:
    """'… resets 7pm (Europe/London)', '… resets 7:30pm', '… resets Oct 9, 7pm' -> when. An hour if unreadable."""
    now = now or dt.datetime.now(LOCAL_TZ)
    match = re.search(r"resets\s+(?:([A-Z][a-z]{2})\s+(\d{1,2}),?\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)", text, re.I)
    if not match:
        return now + dt.timedelta(hours=1)
    month, day, hour, minute, half = match.groups()
    hour = int(hour) % 12 + (12 if half.lower() == "pm" else 0)
    when = now.replace(hour=hour, minute=int(minute or 0), second=0, microsecond=0)
    if month:
        when = when.replace(month=dt.datetime.strptime(month, "%b").month, day=int(day))
    elif when <= now:
        when += dt.timedelta(days=1)
    return when


def run(command: list, prompt: str, cwd, timeout: int, queue=None) -> dict:
    """command is everything after the executable, e.g. ["-p", "--output-format", "json", "--model", "opus"].
    Returns the parsed JSON result; raises LimitReached, or RuntimeError for anything else."""
    from .queue import queue as default_queue
    queue = queue or default_queue
    reset = queue.limit_until()
    if reset:
        raise LimitReached(reset, f"Claude's usage limit is in force until {reset:%H:%M}")
    env = {**os.environ, "DISABLE_AUTOUPDATER": "1"}
    done = subprocess.run([exe(), *command], input=prompt, cwd=cwd, capture_output=True, text=True, env=env,
                          encoding="utf-8", timeout=timeout, creationflags=NO_WINDOW)
    try:
        result = json.loads(done.stdout)
    except json.JSONDecodeError:
        text = done.stderr or done.stdout
        if LIMIT_TEXT.search(text):
            raise LimitReached(queue.remember(reset_time(text)), text.strip()[:200])
        raise RuntimeError(f"claude exited {done.returncode}: {text[:300]}")
    if result.get("is_error") or not result.get("result"):
        text = json.dumps(result)
        if result.get("api_error_status") == 429 or LIMIT_TEXT.search(text) or "rate_limit" in text:
            raise LimitReached(queue.remember(reset_time(str(result.get("result") or text))),
                               str(result.get("result") or "rate limited")[:200])
        raise RuntimeError(f"claude reported an error: {text[:300]}")
    return result
