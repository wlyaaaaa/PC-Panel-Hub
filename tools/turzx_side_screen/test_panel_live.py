import tempfile
from pathlib import Path
import unittest
from panel_live import project_live, write_json
class ProjectionTests(unittest.TestCase):
    def test_no_dry_run_frames_or_mixed_window_totals(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            write_json(output / "stream/stream-heartbeat.json", {"utc": "2026-11-16T02:00:00Z", "sent": 30, "send_attempted": False})
            actions = [{"Guard": "hs2", "Action": "Move", "ObservedAtUtc": "2026-11-16T01:00:00Z"},
                       {"Guard": "vdd", "Action": "Move", "ObservedAtUtc": "2026-11-16T02:00:00Z"}]
            guard = {"Status": "running", "TotalMoves": 2, "RecentActions": actions}
            write_json(output / "hs2-startup-window-guard.json", guard)
            first = project_live({"fps": {"status": "active"}, "weather": {"status": "stale"}}, output)["metrics"]
            self.assertEqual((first["fps_status"], first["weather_status"]), ("在更新", "已过期"))
            self.assertEqual(project_live({"weather": {"status": "live"}}, output)["metrics"]["weather_status"], "在更新")
            self.assertEqual((first["sent"], first["moves"], first["last_move"]), (None, 1, actions[0]["ObservedAtUtc"]))
            write_json(output / "stream/stream-heartbeat-a.json", {"utc": "2026-11-16T02:00:01Z", "sent": 31, "send_attempted": True, "frame_transport": "diff_204", "fetch_ms": 7})
            self.assertEqual((project_live({}, output)["metrics"]["sent"], project_live({}, output)["frame_timing"]["fetch_ms"]), (31, 7))
            (output / "stream/stream-last.png").touch()
            artifact = project_live({}, output)["artifacts"][0]
            self.assertEqual((artifact["public"], artifact["open"]), ("owner", {"source": "panel.preview"}))
            write_json(output / "hs2-startup-window-guard.json", dict(guard, TotalMoves=85))
            self.assertIsNone(project_live({}, output)["metrics"]["moves"])
            self.assertEqual(len(project_live({}, output)["metrics"]["recent_moves"]), 1)
            write_json(output / "hs2-startup-window-guard.json", dict(guard, TotalMoves=85, HS2TotalMoves=61))
            self.assertEqual(project_live({}, output)["metrics"]["moves"], 61)
if __name__ == "__main__":
    unittest.main()
