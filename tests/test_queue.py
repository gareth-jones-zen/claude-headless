"""Run with: python -m unittest discover -s tests"""

import datetime as dt
import pathlib
import sys
import tempfile
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "src"))

from claude_headless import Queue  # noqa: E402
from claude_headless.runner import LOCAL_TZ, reset_time  # noqa: E402


class Limits(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.queue = Queue(pathlib.Path(self.temp.name))

    def tearDown(self):
        self.temp.cleanup()

    def test_reset_times_are_read_from_the_message(self):
        now = dt.datetime(2026, 11, 10, 15, 0, tzinfo=LOCAL_TZ)
        self.assertEqual(reset_time("You've hit your session limit · resets 7pm (Europe/London)", now).hour, 19)
        self.assertEqual(reset_time("resets 7:30pm", now).minute, 30)
        self.assertEqual(reset_time("resets 9am", now).day, 11)          # tomorrow morning
        self.assertEqual(reset_time("resets Nov 12, 7pm", now).day, 12)
        self.assertEqual(reset_time("no time here", now), now + dt.timedelta(hours=1))

    def test_one_notification_per_limit(self):
        reset = dt.datetime.now(LOCAL_TZ) + dt.timedelta(hours=2)
        self.queue.remember(reset)                     # the runner saw it
        self.assertTrue(self.queue.retry(["a"], ".", reset))
        self.assertFalse(self.queue.retry(["b"], ".", reset))
        self.assertEqual([i["argv"] for i in self.queue.pending()], [["a"], ["b"]])

    def test_jobs_wait_for_the_reset_and_a_passed_limit_is_ignored(self):
        self.queue.retry(["a"], ".", dt.datetime.now(LOCAL_TZ) + dt.timedelta(hours=1))
        self.assertIsNone(self.queue.take())
        self.queue.remember(dt.datetime.now(LOCAL_TZ) - dt.timedelta(hours=1))
        self.assertIsNotNone(self.queue.limit_until())   # remember never shortens a known limit
        self.queue._write(self.queue.limit_file, {"reset": (dt.datetime.now(LOCAL_TZ) - dt.timedelta(hours=1)).isoformat()})
        self.assertEqual(self.queue.take()["argv"], ["a"])

    def test_same_job_once_and_deadlines_first(self):
        self.assertTrue(self.queue.add(["a"], "."))
        self.assertFalse(self.queue.add(["a"], "."))
        self.queue.add(["urgent"], ".", front=True)
        self.assertEqual([i["argv"] for i in self.queue.pending()], [["urgent"], ["a"]])

    def test_run_works_through_the_queue(self):
        marker = pathlib.Path(self.temp.name) / "ran.txt"
        for n in range(3):
            self.queue.add([sys.executable, "-c", f"open(r'{marker}', 'a').write('{n}')"], self.temp.name)
        self.assertEqual(self.queue.run(log=lambda _: None), 3)
        self.assertEqual(marker.read_text(), "012")
        self.assertEqual(self.queue.pending(), [])


if __name__ == "__main__":
    unittest.main()
