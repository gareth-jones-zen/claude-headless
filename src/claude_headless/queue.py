"""The seat's job queue, and the usage limit it waits on.

    %LOCALAPPDATA%\\claude-headless\\     (or CLAUDE_HEADLESS_HOME)
        limit.json    {"reset": iso, "notified": bool}   while a limit is in force
        queue.json    {"items": [{"argv": [...], "cwd": "...", "added": iso}]}
        runner.log

**One queue per seat, whichever tool queued the job.** Everything on a subscription seat shares one
limit, so jobs from different tools have to wait on the same reset, and run one at a time after it:
the first backfill on the EA started 32 drafts four at a time and used the session limit up in four
minutes (8 Oct 2026). A job is a command line and the folder to run it in, so any tool can queue one.

**One notification per limit**, not one per job: retry() says True only the first time a limit is
seen, so the caller notifies then and stays quiet for the rest of the batch.
"""

from __future__ import annotations

import datetime as dt
import json
import os
import pathlib
import subprocess

from win_background import NO_WINDOW, key, logger, single_instance

from .runner import LOCAL_TZ

GRACE = dt.timedelta(minutes=5)   # start a little after the stated reset


def default_home() -> pathlib.Path:
    home = os.environ.get("CLAUDE_HEADLESS_HOME")
    return pathlib.Path(home) if home else pathlib.Path(os.environ.get("LOCALAPPDATA", pathlib.Path.home())) / "claude-headless"


class Queue:
    def __init__(self, home: pathlib.Path | None = None):
        self.home = pathlib.Path(home) if home else default_home()

    @property
    def limit_file(self) -> pathlib.Path:
        return self.home / "limit.json"

    @property
    def queue_file(self) -> pathlib.Path:
        return self.home / "queue.json"

    def _read(self, path: pathlib.Path, default):
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return default

    def _write(self, path: pathlib.Path, data) -> None:
        self.home.mkdir(parents=True, exist_ok=True)
        temp = path.with_name(f"{path.name}.{os.getpid()}.tmp")
        temp.write_text(json.dumps(data, indent=2), encoding="utf-8")
        temp.replace(path)

    # ---- the limit ------------------------------------------------------------------------------

    def limit_until(self, now: dt.datetime | None = None) -> dt.datetime | None:
        """The reset time while a limit is in force (plus a few minutes' grace); None once it's passed."""
        data = self._read(self.limit_file, {})
        try:
            reset = dt.datetime.fromisoformat(data["reset"])
        except (KeyError, ValueError, TypeError):
            return None
        return reset if reset + GRACE > (now or dt.datetime.now(LOCAL_TZ)) else None

    def remember(self, reset: dt.datetime) -> dt.datetime:
        """Record a limit. A limit already known keeps its notified flag."""
        known = self.limit_until()
        notified = bool(known) and self._read(self.limit_file, {}).get("notified", False)
        self._write(self.limit_file, {"reset": max(reset, known or reset).isoformat(), "notified": notified})
        return reset

    # ---- jobs -----------------------------------------------------------------------------------

    def add(self, argv: list, cwd: str | os.PathLike, front: bool = False) -> bool:
        """Queue a job. The same job twice is queued once. front=True for one with a deadline. True if added."""
        data = self._read(self.queue_file, {"items": []})
        argv = [str(a) for a in argv]
        if any(item["argv"] == argv for item in data["items"]):
            return False
        item = {"argv": argv, "cwd": str(cwd), "added": dt.datetime.now(LOCAL_TZ).isoformat(timespec="seconds")}
        data["items"].insert(0, item) if front else data["items"].append(item)
        self._write(self.queue_file, data)
        return True

    def retry(self, argv: list, cwd: str | os.PathLike, reset: dt.datetime) -> bool:
        """A job that met the limit: queue it for after the reset. True if this limit is news, so the
        caller notifies once."""
        self.add(argv, cwd)
        known = self.limit_until()
        data = self._read(self.limit_file, {})
        news = not (known and data.get("notified"))
        self._write(self.limit_file, {"reset": max(reset, known or reset).isoformat(), "notified": True})
        return news

    def pending(self) -> list:
        return self._read(self.queue_file, {"items": []})["items"]

    def take(self) -> dict | None:
        """The next job, removed from the queue, if the limit has passed. None otherwise."""
        if self.limit_until():
            return None
        data = self._read(self.queue_file, {"items": []})
        if not data["items"]:
            return None
        item = data["items"].pop(0)
        self._write(self.queue_file, data)
        return item

    def run(self, log=None, max_jobs: int = 50, timeout: int = 3600) -> int:
        """Run queued jobs one at a time until the queue is empty or a limit is back. Only one runner
        works the queue at once; a second returns straight away. Returns how many jobs ran."""
        log = log or logger(self.home / "runner.log")
        lock = single_instance(f"claude-headless-runner-{key(self.home)}", self.home)
        if lock is None:
            return 0
        ran = 0
        try:
            while ran < max_jobs:
                item = self.take()
                if item is None:
                    break
                ran += 1
                log(f"job: {' '.join(item['argv'])[:200]} ({len(self.pending())} more queued)")
                try:
                    done = subprocess.run(item["argv"], cwd=item["cwd"], capture_output=True, text=True,
                                          encoding="utf-8", errors="replace", timeout=timeout, creationflags=NO_WINDOW)
                    if done.returncode:
                        log(f"  exited {done.returncode}: {(done.stderr or done.stdout).strip()[-300:]}")
                except Exception as error:
                    log(f"  failed: {type(error).__name__}: {error}")
        finally:
            lock.release()
        return ran


queue = Queue()
