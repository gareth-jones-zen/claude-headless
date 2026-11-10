"""Run Claude Code headless from background tools, and survive the usage limit.

    from claude_headless import LimitReached, queue, run

    try:
        result = run(["-p", "--output-format", "json", "--model", "opus"], prompt, cwd, timeout=1800)
    except LimitReached as limit:
        if queue.retry([sys.executable, "-m", "my_tool", "draft", path], cwd, limit.reset):
            notify(...)   # once per limit: True only the first time this limit is seen

A subscription seat has one usage limit, shared by everything run on it, so there's one queue per
seat (%LOCALAPPDATA%\\claude-headless\\, or CLAUDE_HEADLESS_HOME), whichever tool queued the job.
`claude-headless run-queue` works through it one job at a time once the limit has passed.
"""

from .queue import Queue, queue
from .runner import LimitReached, run

__version__ = "0.1.0"
__all__ = ["LimitReached", "Queue", "queue", "run"]
