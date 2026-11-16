"""Small projections from the existing samplers; no hardware or extra collector."""
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import subprocess


def read_json(path):
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    temporary.replace(path)


def project_live(snapshot, output):
    heartbeats = (read_json(output / "stream" / name) for name in ("stream-heartbeat.json", "stream-heartbeat-a.json", "stream-heartbeat-b.json"))
    heartbeat = max(heartbeats, key=lambda value: value.get("utc", ""), default={})
    guard = read_json(output / "hs2-startup-window-guard.json")
    actions = guard.get("RecentActions", [])
    moves = [r for r in actions if r.get("Action") == "Move" and r.get("Guard") == "hs2"]
    sending = heartbeat.get("send_attempted") is True and heartbeat.get("frame_transport") in {"full_200", "diff_204"}
    running = guard.get("Status") == "running"
    count = guard.get("HS2TotalMoves")
    if count is None and guard.get("TotalMoves") == sum(r.get("Action") == "Move" for r in actions):
        count = len(moves)  # Old state is exact only while the complete action history still fits.
    trust = snapshot.get("trust", {})
    metrics = {"sent": heartbeat.get("sent") if sending else None,
               "failed": heartbeat.get("failed") if sending else None,
               "updated": heartbeat.get("utc") if sending and heartbeat.get("status") == "ok" else None,
               "missing": trust.get("missing_count"), "stale": trust.get("stale_count"),
               "fallback": trust.get("fallback_count"), "missing_fields": trust.get("missing_field_count"),
               "self_score": trust.get("score"),
               "fps_status": {"active": "在更新", "stale": "已过期"}.get(snapshot.get("fps", {}).get("status"), snapshot.get("fps", {}).get("status")),
               "weather_status": {"active": "在更新", "live": "在更新", "stale": "已过期"}.get(snapshot.get("weather", {}).get("status"), snapshot.get("weather", {}).get("status")),
               "moves": count if running else None,
               "last_move": (guard.get("HS2LastMoveUtc") or max((r["ObservedAtUtc"] for r in moves), default=None)) if running else None,
               "wallpaper": guard.get("WallpaperLastRestartUtc") if guard.get("WallpaperRestartResult") == "restart-verified" else None,
               "wallpaper_restarts": guard.get("WallpaperRestartCount"),
               "recent_moves": [{"at": r["ObservedAtUtc"], "text": "曲面屏挪窗", "state_text": "已执行"} for r in moves] if running and "RecentActions" in guard else None}
    metrics["readings"] = {"text": "缺 %s 类／过期 %s 类" % (metrics["missing"], metrics["stale"]),
                           "missing": metrics["missing"], "stale": metrics["stale"]} if metrics["missing"] is not None and metrics["stale"] is not None else None
    preview = output / "stream/stream-last.png"
    preview_at = datetime.fromtimestamp(preview.stat().st_mtime, timezone.utc).isoformat() if preview.is_file() else None
    metrics["preview"] = {"text": "有一张预览" if preview_at else "没有预览文件", "count": int(preview_at is not None), "generated_at": preview_at}
    return {"v": 1, "at": snapshot.get("health", {}).get("generated_at"),
            "stream_at": heartbeat.get("utc"), "guard_at": guard.get("ObservedAtUtc"), "preview_at": preview_at,
            "scope": "主机写出不等于实体屏收到；计数限当前进程；旧窗口状态混有 VDD 且历史截断时不猜 HS2 总数。",
            "frame_timing": {key: heartbeat.get(key) for key in ("fetch_ms", "render_ms", "send_ms", "elapsed_ms", "sleep_ms", "period_ms")},
            "metrics": metrics, "artifacts": [{"at": preview_at, "kind": "image", "title": "机箱屏此刻的画面", "status": "completed", "public": "owner", "open": {"source": "panel.preview"}}] if preview_at else []}


def publish_live(snapshot, output=None):
    try:
        output = output or Path(__file__).resolve().parent / "out"
        write_json(output / "live.snapshot.json", project_live(snapshot, output))
    except (OSError, ValueError, KeyError, TypeError):
        pass  # Optional publication must not interrupt the screen's existing sampling.


def publish_static(root):
    files = subprocess.check_output(["git", "-C", str(root), "ls-files", "-z"]).decode("utf-8").split("\0")
    docs, code, digest = [], [], hashlib.sha256()
    words = lambda text: len(re.findall(r"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff\U00020000-\U0002fa1f\U00030000-\U000323af]|[A-Za-z0-9_]+", text))
    for name in sorted(filter(None, files)):
        path = root / name
        group = docs if path.suffix == ".md" else code if path.suffix in {".cmd", ".cs", ".ps1", ".py", ".vbs", ".xaml"} else None
        if group is None:
            continue
        text = path.read_text(encoding="utf-8-sig")
        digest.update(name.encode("utf-8") + b"\0" + text.encode("utf-8"))
        group.append(text)
    path = root / "docs/live.snapshot.json"
    previous = read_json(path)
    if previous.get("fingerprint") == digest.hexdigest(): return
    at = datetime.now(timezone.utc).isoformat()
    size = {"at": at, "docs_chars": sum(map(words, docs)), "code_lines": sum(len(t.splitlines()) for t in code),
            "code_chars": sum(len(re.sub(r"\s", "", t)) for t in code)}
    metrics = {"docs_files": len(docs), "code_files": len(code), "docs_chars_raw": sum(len(re.sub(r"\s", "", t)) for t in docs),
               "code_words": sum(map(words, code)), "hs2_note": "曲面屏卡片与刷新时刻由浮层快照提供；未安装新浮层时留空，不以进程状态推测。"}
    write_json(path, {"v": 1, "at": at, "scope": "按需重算 Git 跟踪的自有文档与源码，含测试；排除配置、依赖、生成物和厂商文件。字数按汉字与连续英文数字下划线串计。",
                      "fingerprint": digest.hexdigest(), "metrics": metrics, "size": size})


if __name__ == "__main__":
    publish_static(Path(__file__).resolve().parents[2])
