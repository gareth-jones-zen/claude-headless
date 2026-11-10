"""claude-headless: the seat's queue of Claude Code jobs waiting out a usage limit.

    claude-headless status          the limit, if one is in force, and what's queued
    claude-headless run-queue       run queued jobs one at a time, until done or the limit is back
    claude-headless autostart on    run the queue every 15 minutes (Task Scheduler); off, status
"""

from __future__ import annotations

import argparse
import datetime as dt
import sys

from win_background import Task, python_command

from . import __version__
from .queue import queue

TASK = Task("claude-headless run-queue", "Runs Claude Code jobs that waited out the usage limit (claude-headless run-queue).")


def main(argv: list | None = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if stream and hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    parser = argparse.ArgumentParser(prog="claude-headless", description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", action="version", version=__version__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("status", help="the limit and the queue")
    sub.add_parser("run-queue", help="run queued jobs, one at a time")
    p = sub.add_parser("autostart", help="run the queue every 15 minutes")
    p.add_argument("action", choices=["on", "off", "status"])
    p.add_argument("--every", type=int, default=15, help="minutes between runs (default 15)")
    args = parser.parse_args(argv)

    if args.command == "status":
        reset = queue.limit_until()
        print(f"Usage limit in force until {reset:%a %H:%M}." if reset else "No usage limit in force.")
        items = queue.pending()
        print(f"{len(items)} job(s) queued" + (":" if items else "."))
        for item in items:
            print(f"  {item['added'][:16]}  {' '.join(item['argv'])[:150]}")
        return 0
    if args.command == "run-queue":
        ran = queue.run()
        print(f"{ran} job(s) run; {len(queue.pending())} still queued.")
        return 0
    if args.action == "on":
        line = TASK.enable(*python_command("claude_headless", "run-queue"), at_logon=True,
                           every=dt.timedelta(minutes=args.every), keep_running=False,
                           time_limit=dt.timedelta(hours=4))
        print(f"Autostart is on: '{TASK.name}' runs every {args.every} minutes and at logon.\n  {line}")
    elif args.action == "off":
        print("Autostart is off." if TASK.disable() else "Autostart was already off.")
    else:
        info = TASK.status()
        print("Autostart is off." if not info else
              "\n".join([f"Autostart is on ('{TASK.name}')."] + [f"  {k.replace('_', ' '):<12} {info.get(k, '')}"
                                                                 for k in ("state", "last_run", "last_result", "next_run")]))
    return 0
