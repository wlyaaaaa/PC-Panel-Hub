from __future__ import annotations

import argparse
import asyncio
from collections import deque
import csv
import ctypes
from ctypes import wintypes
import datetime as dt
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import importlib
import json
import math
import os
import platform
import re
import shutil
import socket
import subprocess
import threading
import time
from typing import Any, Callable
from urllib.parse import urlencode, urlsplit
import urllib.request
import warnings
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError


DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 18765
SCHEMA_VERSION = 1
CPU_INITIAL_SAMPLE_WAIT_SECONDS = 0.05
GPU_CACHE_TTL_SECONDS = 1.0
GPU_FAILURE_CACHE_TTL_SECONDS = 3.0
GPU_LAST_GOOD_MAX_AGE_SECONDS = 30.0
NVIDIA_SMI_TIMEOUT_SECONDS = 0.8
WEATHER_CACHE_TTL_SECONDS = 600.0
WEATHER_FAILURE_CACHE_TTL_SECONDS = 30.0
DEFAULT_LHM_SENSOR_URLS = (
    "http://127.0.0.1:18085/data.json",
    "http://127.0.0.1:8085/data.json",
)
LHM_SENSOR_CACHE_TTL_SECONDS = 1.0
LHM_SENSOR_FAILURE_CACHE_TTL_SECONDS = 2.0
LHM_LAST_GOOD_MAX_AGE_SECONDS = 20.0
PHYSICAL_DISK_TOPOLOGY_TTL_SECONDS = 60.0
PHYSICAL_DISK_TOPOLOGY_FAILURE_TTL_SECONDS = 10.0
SMALL_REMOVABLE_DISK_BYTES = 32_000_000_000
NETWORK_LATENCY_TTL_SECONDS = 2.0
NETWORK_PING_TARGET = "223.5.5.5"
TIMEAUDIT_CACHE_TTL_SECONDS = 2.0
TIMEAUDIT_CONNECT_TIMEOUT_SECONDS = 1.0
TIMEAUDIT_QUERY_TIMEOUT_SECONDS = 1.0
FPS_SAMPLE_FRESH_SECONDS = 10.0
TOP_PROCESSES_CACHE_TTL_SECONDS = 5.0
TOP_PROCESSES_HELPER_MAX_AGE_SECONDS = 10.0
DEFAULT_DISPLAY_TIMEZONE = "Asia/Shanghai"
TIMEAUDIT_DSN = os.environ.get("TIMEAUDIT_DSN") or None
CONFIG_PATH = os.path.join(os.path.dirname(__file__), "config.json")
WEATHER_SHIM_BASE_URL = "http://127.0.0.1:18080"
TOP_PROCESSES_CACHE_PATH = os.environ.get(
    "TURZX_TOP_PROCESSES_CACHE",
    os.path.join(os.path.dirname(__file__), "out", "top-processes.json"),
)
DATA_TRUST_LOG_PATH = os.environ.get(
    "TURZX_DATA_TRUST_LOG",
    os.path.join(os.path.dirname(__file__), "out", "data-trust.jsonl"),
)
DATA_TRUST_LOG_INTERVAL_SECONDS = 5.0
DATA_TRUST_LOG_MAX_BYTES = 2 * 1024 * 1024
DATA_TRUST_LOG_BACKUP_COUNT = 2
_sequence = 0
_gpu_cache_value: dict[str, Any] | None = None
_gpu_cache_expires_at = 0.0
_gpu_last_good_value: dict[str, Any] | None = None
_gpu_last_good_at = 0.0
_lhm_cache_value: dict[str, Any] | None = None
_lhm_cache_expires_at = 0.0
_lhm_cache_lock = threading.Lock()
_lhm_refreshing = False
_lhm_last_good_url: str | None = None
_lhm_last_success_at = 0.0
_lhm_last_refresh_failed = False
_physical_disk_topology_cache_value: list[dict[str, Any]] | None = None
_physical_disk_topology_cache_expires_at = 0.0
_physical_disk_topology_cache_lock = threading.Lock()
_physical_disk_topology_refreshing = False
_timeaudit_cache_value: dict[str, Any] | None = None
_timeaudit_cache_expires_at = 0.0
_timeaudit_cache_lock = threading.Lock()
_timeaudit_refreshing = False
_weather_cache_value: dict[str, Any] | None = None
_weather_cache_expires_at = 0.0
_weather_cache_key: str | None = None
_weather_cache_lock = threading.Lock()
_weather_refreshing = False
_weather_refresh_thread: threading.Thread | None = None
_config_cache: dict[str, Any] | None = None
_config_cache_mtime: float | None = None
_top_processes_cache_value: list[dict[str, Any]] | None = None
_top_processes_cache_expires_at = 0.0
_top_processes_cache_limit = 0
_top_processes_cache_lock = threading.Lock()
_top_processes_refreshing = False
_data_trust_log_lock = threading.Lock()
_data_trust_last_logged_at = 0.0
_data_trust_last_log_key: str | None = None
_cpu_history: deque[float] = deque(maxlen=120)
_gpu_history: deque[float] = deque(maxlen=120)

WEATHER_LOCATION_ALIASES = {
    "北京": "116.4074,39.9042",
    "beijing": "116.4074,39.9042",
    "田家庵": "101220405",
    "tianjiaan": "101220405",
}

DRIVE_TYPE_NAMES = {
    0: "unknown",
    1: "no_root_dir",
    2: "removable",
    3: "fixed",
    4: "network",
    5: "cdrom",
    6: "ramdisk",
}


def display_time_iso_from_utc(
    value: dt.datetime,
    timezone_name: str | None = None,
) -> str:
    if value.tzinfo is None:
        value = value.replace(tzinfo=dt.timezone.utc)
    return (
        value.astimezone(display_tzinfo(timezone_name))
        .replace(microsecond=0)
        .isoformat()
    )


def display_tzinfo(timezone_name: str | None = None) -> dt.tzinfo:
    name = timezone_name or configured_timezone_name()
    try:
        return ZoneInfo(name)
    except (ZoneInfoNotFoundError, ValueError):
        return dt.timezone(dt.timedelta(hours=8), name="CST")


def configured_timezone_name() -> str:
    config = _load_config()
    time_config = config.get("time") if isinstance(config.get("time"), dict) else {}
    value = time_config.get("timezone")
    if isinstance(value, str) and value.strip():
        return value.strip()
    return DEFAULT_DISPLAY_TIMEZONE


def utc_now_iso() -> str:
    return (
        dt.datetime.now(dt.timezone.utc)
        .replace(microsecond=0)
        .isoformat()
        .replace("+00:00", "Z")
    )


def next_sequence() -> int:
    global _sequence
    _sequence += 1
    return _sequence


def empty_snapshot() -> dict[str, Any]:
    return {
        "schema_version": SCHEMA_VERSION,
        "timestamp_unix_ms": 0,
        "sequence": 0,
        "time": None,
        "weather": {
            "city": None,
            "summary": None,
            "condition": None,
            "temperature_celsius": None,
            "temperature_c": None,
            "temperature_text": None,
            "aqi": None,
            "humidity_percent": None,
            "wind_text": None,
            "source": "fallback",
            "status": "unavailable",
            "updated_at": None,
        },
        "alert": {
            "level": "ok",
            "message": None,
            "items": [],
        },
        "foreground_app": {
            "title": None,
            "process_id": None,
            "process_name": None,
            "exe_path": None,
            "source": "fallback",
        },
        "cpu": {
            "model": None,
            "usage_percent": None,
            "temperature_celsius": None,
            "power_watts": None,
            "clock_ghz": None,
            "clock_mhz": None,
            "core_voltage": None,
            "load_history_percent": [],
            "logical_count": os.cpu_count(),
            "status": "idle",
            "source": "fallback",
        },
        "gpu": {
            "model": None,
            "usage_percent": None,
            "temperature_c": None,
            "temperature_celsius": None,
            "name": None,
            "power_watts": None,
            "core_voltage": None,
            "core_clock_mhz": None,
            "core_clock_ghz": None,
            "memory_clock_mhz": None,
            "memory_clock_ghz": None,
            "mem_clock_mhz": None,
            "vram_used_gb": None,
            "vram_total_gb": None,
            "load_history_percent": [],
            "status": "idle",
            "source": "fallback",
        },
        "fps": {
            "current": None,
            "average": None,
            "low_1_percent": None,
            "frame_time_ms": None,
            "status": "idle",
            "source": "fallback",
            "sample_age_seconds": None,
            "detail": None,
        },
        "memory": {
            "used_percent": None,
            "used_gb": None,
            "available_gb": None,
            "total_gb": None,
            "vram_usage_percent": None,
            "vram_used_gb": None,
            "vram_total_gb": None,
            "motherboard_temperature_celsius": None,
            "module_temperatures_celsius": [],
            "source": "fallback",
        },
        "disks": [],
        "physical_disks": [],
        "network": {
            "rx_bytes_per_sec": None,
            "tx_bytes_per_sec": None,
            "download_bytes_per_second": None,
            "upload_bytes_per_second": None,
            "ping_ms": None,
            "jitter_ms": None,
            "packet_loss_percent": None,
            "dpc_percent": None,
            "latency_status": "connecting",
            "addresses": [],
            "source": "stdlib",
        },
        "top_processes": [],
        "health": {
            "status": "ok",
            "detail": None,
            "dpc_latency_us": None,
            "hard_page_faults_per_second": None,
            "refresh_interval_seconds": None,
            "generated_at": None,
            "errors": [],
        },
        "trust": {
            "score": 0,
            "level": "unknown",
            "summary": None,
            "worst_component": None,
            "missing_count": 0,
            "fallback_count": 0,
            "stale_count": 0,
            "log_path": "out/data-trust.jsonl",
            "items": [],
        },
    }


def build_snapshot() -> dict[str, Any]:
    snapshot = empty_snapshot()
    now_dt = dt.datetime.now(dt.timezone.utc)
    now = (
        now_dt.replace(microsecond=0)
        .isoformat()
        .replace("+00:00", "Z")
    )
    snapshot["timestamp_unix_ms"] = int(now_dt.timestamp() * 1000)
    snapshot["sequence"] = next_sequence()
    snapshot["time"] = display_time_iso_from_utc(now_dt)
    snapshot["health"]["generated_at"] = now

    errors: list[dict[str, str]] = []
    collectors: list[tuple[str, Callable[[], Any]]] = [
        ("weather", read_weather_snapshot),
        ("foreground_app", read_foreground_app),
        ("cpu", read_cpu_snapshot),
        ("gpu", read_gpu_snapshot),
        ("fps", read_fps_snapshot),
        ("memory", read_memory_snapshot),
        ("disks", enumerate_disks),
        ("physical_disks", read_physical_disks),
        ("network", read_network_snapshot),
        ("top_processes", read_top_processes),
    ]

    for key, collector in collectors:
        try:
            snapshot[key] = collector()
        except Exception as exc:  # Keep the HTTP schema stable even on partial failure.
            errors.append(
                {
                    "component": key,
                    "error": f"{type(exc).__name__}: {exc}",
                }
            )

    _merge_gpu_vram_into_memory(snapshot)
    _merge_lhm_sensors(snapshot)
    snapshot["health"]["refresh_interval_seconds"] = _configured_refresh_interval_seconds()
    snapshot["health"]["dpc_latency_us"] = None
    network = snapshot.get("network")
    dpc_percent = _DPC_TIME_SAMPLER.sample()
    if isinstance(network, dict) and dpc_percent is not None:
        network["dpc_percent"] = dpc_percent
        network["source"] = _append_source(network.get("source"), "pdh")

    fps = snapshot.get("fps")
    fps_status = fps.get("status") if isinstance(fps, dict) else None
    if fps_status in {"stale", "error"}:
        errors.append(
            {
                "component": "fps",
                "error": str(fps.get("detail") or fps_status),
            }
        )
    snapshot["health"]["errors"] = errors
    snapshot["health"]["status"] = "ok" if not errors else "degraded"
    snapshot["health"]["detail"] = "模块正常运转中" if not errors else f"采集异常 {len(errors)} 项"
    snapshot["alert"] = {
        "level": "ok" if not errors else "warn",
        "message": "系统正常" if not errors else f"采集异常 {len(errors)} 项",
        "items": [str(item.get("component")) for item in errors],
    }
    snapshot["trust"] = build_trust_snapshot(snapshot)
    _maybe_write_data_trust_log(snapshot)
    return snapshot


def build_trust_snapshot(snapshot: dict[str, Any]) -> dict[str, Any]:
    items = [
        _trust_cpu(snapshot.get("cpu")),
        _trust_gpu(snapshot.get("gpu")),
        _trust_fps(snapshot.get("fps")),
        _trust_weather(snapshot.get("weather")),
        _trust_memory(snapshot.get("memory")),
        _trust_disks(snapshot.get("physical_disks")),
        _trust_network(snapshot.get("network")),
        _trust_apps(snapshot.get("top_processes")),
        _trust_health(snapshot.get("health")),
    ]
    weights = {
        "cpu": 16,
        "gpu": 16,
        "fps": 10,
        "weather": 12,
        "memory": 10,
        "disks": 10,
        "network": 12,
        "apps": 10,
        "health": 4,
    }
    total_weight = sum(weights.get(str(item.get("component")), 1) for item in items)
    weighted = sum(_trust_item_score(item) * weights.get(str(item.get("component")), 1) for item in items)
    score = int(round(weighted / total_weight)) if total_weight else 0
    missing_count = sum(1 for item in items if int(item.get("missing_count") or 0) > 0)
    missing_field_count = sum(int(item.get("missing_count") or 0) for item in items)
    fallback_count = sum(1 for item in items if item.get("fallback"))
    stale_count = sum(1 for item in items if item.get("status") == "stale")
    worst = min(items, key=lambda item: (_trust_item_score(item), str(item.get("component")))) if items else None
    level = "ok"
    if score < 35:
        level = "bad"
    elif score < 90 or missing_count > 0 or fallback_count > 0 or stale_count > 0:
        level = "warn"
    return {
        "score": score,
        "level": level,
        "summary": f"可信度 {score}/100",
        "worst_component": None if worst is None else worst.get("component"),
        "worst_label": None if worst is None else worst.get("label"),
        "missing_count": missing_count,
        "missing_field_count": missing_field_count,
        "fallback_count": fallback_count,
        "stale_count": stale_count,
        "log_path": "out/data-trust.jsonl",
        "items": items,
    }


def _trust_cpu(cpu: Any) -> dict[str, Any]:
    data = cpu if isinstance(cpu, dict) else {}
    score = 100
    missing: list[str] = []
    if _parse_float(data.get("usage_percent")) is None:
        missing.append("usage")
        score = min(score, 25)
    for key in ("temperature_celsius", "power_watts", "clock_mhz", "core_voltage"):
        if _parse_float(data.get(key)) is None:
            missing.append(key)
            score -= 5
    source = _empty_to_none(data.get("source")) or "fallback"
    stale = _is_stale_source(source) or data.get("status") == "stale"
    fallback = _is_fallback_source(source)
    if fallback:
        score = min(score, 75)
    if stale:
        score = min(score, 85)
    item = _trust_item("cpu", "CPU", score, source, missing, fallback, _detail_from_missing("CPU", missing))
    if stale:
        item["status"] = "stale"
        item["detail"] = "CPU 探针短暂失败，使用最近可信值"
    return item


def _trust_gpu(gpu: Any) -> dict[str, Any]:
    data = gpu if isinstance(gpu, dict) else {}
    score = 100
    missing: list[str] = []
    if _parse_float(data.get("usage_percent")) is None:
        missing.append("usage")
        score = min(score, 25)
    for key in ("temperature_celsius", "power_watts", "core_clock_mhz", "memory_clock_mhz", "core_voltage"):
        if _parse_float(data.get(key)) is None:
            missing.append(key)
            score -= 4
    source = _empty_to_none(data.get("source")) or "fallback"
    stale = _is_stale_source(source) or data.get("status") == "stale"
    fallback = _is_fallback_source(source)
    if fallback:
        score = min(score, 75)
    if stale:
        score = min(score, 85)
    item = _trust_item("gpu", "GPU", score, source, missing, fallback, _detail_from_missing("GPU", missing))
    if stale:
        item["status"] = "stale"
        item["detail"] = "GPU 探针短暂失败，使用最近可信值"
    return item


def _trust_fps(fps: Any) -> dict[str, Any]:
    data = fps if isinstance(fps, dict) else {}
    source = _empty_to_none(data.get("source")) or "fallback"
    status = (_empty_to_none(data.get("status")) or "error").lower()
    missing: list[str] = []
    fallback = False

    if status == "active":
        score = 100
        if _parse_float(data.get("current")) is None:
            missing.append("current")
            score = 45
        if _parse_float(data.get("frame_time_ms")) is None:
            missing.append("frame_time")
            score = min(score, 80)
        detail = "游戏帧捕获正常"
    elif status == "idle":
        score = 100
        detail = "PresentMon 正常，当前没有游戏帧"
    elif status == "disabled":
        score = 100
        detail = "FPS 采集未启用"
    elif status == "connecting":
        score = 90
        detail = "正在连接 PresentMon 数据源"
    elif status == "stale":
        score = 55
        detail = str(data.get("detail") or "帧数据已过期")
    else:
        score = 25
        missing.append("collector")
        detail = str(data.get("detail") or "帧采集异常")

    item = _trust_item("fps", "FPS", score, source, missing, fallback, detail)
    if status in {"connecting", "stale", "error", "disabled"}:
        item["status"] = status
    return item


def _trust_weather(weather: Any) -> dict[str, Any]:
    data = weather if isinstance(weather, dict) else {}
    source = _empty_to_none(data.get("source")) or "fallback"
    missing: list[str] = []
    score = 100
    if _empty_to_none(data.get("city")) is None:
        missing.append("city")
        score -= 10
    if _parse_float(data.get("temperature_celsius") if data.get("temperature_celsius") is not None else data.get("temperature_c")) is None:
        missing.append("temperature")
        score = min(score, 45)
    if _empty_to_none(data.get("condition")) is None and _empty_to_none(data.get("summary")) is None:
        missing.append("condition")
        score -= 10
    fallback = _is_fallback_source(source)
    if fallback:
        score = min(score, 65)
    return _trust_item("weather", "天气", score, source, missing, fallback, _detail_from_missing("天气", missing))


def _trust_memory(memory: Any) -> dict[str, Any]:
    data = memory if isinstance(memory, dict) else {}
    source = _empty_to_none(data.get("source")) or "fallback"
    missing: list[str] = []
    score = 100
    if _parse_float(data.get("ram_usage_percent") if data.get("ram_usage_percent") is not None else data.get("used_percent")) is None:
        missing.append("ram")
        score = min(score, 45)
    if _parse_float(data.get("vram_usage_percent")) is None:
        score -= 8
    fallback = _is_fallback_source(source)
    if fallback and missing:
        score = min(score, 70)
    return _trust_item("memory", "内存", score, source, missing, fallback and bool(missing), _detail_from_missing("内存", missing))


def _trust_disks(disks: Any) -> dict[str, Any]:
    disk_list = disks if isinstance(disks, list) else []
    missing: list[str] = []
    score = 100
    if not disk_list:
        missing.append("drives")
        score = 45
    else:
        bad_rows = 0
        for disk in disk_list:
            if (
                not isinstance(disk, dict)
                or (
                    _empty_to_none(disk.get("device_id")) is None
                    and _empty_to_none(disk.get("drive")) is None
                )
                or _parse_float(
                    disk.get("used_percent")
                    if disk.get("used_percent") is not None
                    else disk.get("usage_percent")
                )
                is None
            ):
                bad_rows += 1
        if bad_rows:
            missing.append(f"{bad_rows}rows")
            score = max(60, 100 - bad_rows * 10)
    sources = {
        _empty_to_none(disk.get("source"))
        for disk in disk_list
        if isinstance(disk, dict) and _empty_to_none(disk.get("source")) is not None
    }
    source = "+".join(sorted(sources)) if sources else "win32_cim"
    stale = any(_is_stale_source(disk_source) for disk_source in sources)
    if stale:
        score = min(score, 85)
    item = _trust_item("disks", "磁盘", score, source, missing, False, _detail_from_missing("磁盘", missing))
    if stale:
        item["status"] = "stale"
        item["detail"] = "磁盘温度探针短暂失败，使用最近可信值"
    return item


def _trust_network(network: Any) -> dict[str, Any]:
    data = network if isinstance(network, dict) else {}
    source = _empty_to_none(data.get("source")) or "stdlib"
    missing: list[str] = []
    score = 100
    if _parse_float(data.get("download_bytes_per_second") if data.get("download_bytes_per_second") is not None else data.get("rx_bytes_per_sec")) is None:
        missing.append("download")
        score = min(score, 50)
    if _parse_float(data.get("upload_bytes_per_second") if data.get("upload_bytes_per_second") is not None else data.get("tx_bytes_per_sec")) is None:
        missing.append("upload")
        score = min(score, 50)
    if _parse_float(data.get("ping_ms")) is None:
        missing.append("ping")
        score = min(score, 82)
    if _parse_float(data.get("jitter_ms")) is None:
        score -= 4
    fallback = _is_fallback_source(source)
    return _trust_item("network", "网络", score, source, missing, fallback, _detail_from_missing("网络", missing))


def _trust_apps(processes: Any) -> dict[str, Any]:
    process_list = processes if isinstance(processes, list) else []
    missing: list[str] = []
    score = 100
    if not process_list:
        missing.append("processes")
        score = 45
    else:
        with_cpu = [item for item in process_list if isinstance(item, dict) and _parse_float(item.get("cpu_percent")) is not None]
        if not with_cpu:
            missing.append("cpu_delta")
            score = 72
    return _trust_item("apps", "应用", score, "top_processes_helper", missing, False, _detail_from_missing("应用", missing))


def _trust_health(health: Any) -> dict[str, Any]:
    data = health if isinstance(health, dict) else {}
    errors = data.get("errors") if isinstance(data.get("errors"), list) else []
    missing = [str(error.get("component") if isinstance(error, dict) else "error") for error in errors]
    score = 100 if not errors else max(50, 100 - len(errors) * 25)
    return _trust_item("health", "采集", score, "metrics_agent", missing, False, "模块正常" if not errors else f"采集异常 {len(errors)} 项")


def _trust_item(
    component: str,
    label: str,
    score: float,
    source: str,
    missing: list[str],
    fallback: bool,
    detail: str,
) -> dict[str, Any]:
    normalized_score = max(0, min(100, int(round(score))))
    status = "ok"
    if normalized_score < 50:
        status = "missing"
    elif normalized_score < 90 or missing or fallback:
        status = "warn"
    return {
        "component": component,
        "label": label,
        "score": normalized_score,
        "status": status,
        "source": source,
        "missing": missing,
        "missing_count": len(missing),
        "fallback": bool(fallback),
        "detail": detail,
    }


def _trust_item_score(item: dict[str, Any]) -> int:
    try:
        return int(item.get("score") or 0)
    except (TypeError, ValueError):
        return 0


def _is_fallback_source(source: Any) -> bool:
    text = (_empty_to_none(source) or "").lower()
    return not text or text == "fallback" or text.endswith("+fallback")


def _is_stale_source(source: Any) -> bool:
    text = (_empty_to_none(source) or "").lower()
    return any(
        token == "stale" or token.endswith("_stale")
        for token in text.split("+")
    )


def _detail_from_missing(label: str, missing: list[str]) -> str:
    if not missing:
        return f"{label} 数据完整"
    return f"{label} 缺 " + "/".join(missing[:3])


def write_data_trust_log(
    log_path: str | os.PathLike[str],
    trust: dict[str, Any],
    timestamp_unix_ms: int | None = None,
    *,
    max_bytes: int = DATA_TRUST_LOG_MAX_BYTES,
    backup_count: int = DATA_TRUST_LOG_BACKUP_COUNT,
) -> None:
    target = os.fspath(log_path)
    parent = os.path.dirname(target)
    if parent:
        os.makedirs(parent, exist_ok=True)
    payload = {
        "timestamp_unix_ms": int(timestamp_unix_ms if timestamp_unix_ms is not None else time.time() * 1000),
        "score": trust.get("score"),
        "level": trust.get("level"),
        "worst_component": trust.get("worst_component"),
        "worst_label": trust.get("worst_label"),
        "missing_count": trust.get("missing_count"),
        "missing_field_count": trust.get("missing_field_count"),
        "fallback_count": trust.get("fallback_count"),
        "stale_count": trust.get("stale_count"),
        "items": trust.get("items", []),
    }
    encoded = (
        json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n"
    ).encode("utf-8")
    if max_bytes > 0:
        _prune_oversized_log_backups(target, backup_count, max_bytes)
        try:
            current_size = os.path.getsize(target)
        except OSError:
            current_size = 0
        if current_size and current_size + len(encoded) > max_bytes:
            _rotate_bounded_log(target, backup_count, max_bytes)
    with open(target, "ab") as handle:
        handle.write(encoded)


def _prune_oversized_log_backups(
    target: str,
    backup_count: int,
    max_bytes: int,
) -> None:
    for index in range(1, max(0, int(backup_count)) + 1):
        backup = f"{target}.{index}"
        try:
            if os.path.getsize(backup) > max_bytes:
                os.remove(backup)
        except FileNotFoundError:
            continue


def _rotate_bounded_log(target: str, backup_count: int, max_bytes: int) -> None:
    count = max(0, int(backup_count))
    if count == 0:
        try:
            os.remove(target)
        except FileNotFoundError:
            pass
        return

    oldest = f"{target}.{count}"
    try:
        os.remove(oldest)
    except FileNotFoundError:
        pass
    for index in range(count - 1, 0, -1):
        source = f"{target}.{index}"
        destination = f"{target}.{index + 1}"
        try:
            if max_bytes > 0 and os.path.getsize(source) > max_bytes:
                os.remove(source)
            else:
                os.replace(source, destination)
        except FileNotFoundError:
            continue
    try:
        if max_bytes > 0 and os.path.getsize(target) > max_bytes:
            os.remove(target)
        else:
            os.replace(target, f"{target}.1")
    except FileNotFoundError:
        pass


def _maybe_write_data_trust_log(snapshot: dict[str, Any]) -> None:
    global _data_trust_last_logged_at, _data_trust_last_log_key

    trust = snapshot.get("trust")
    if not isinstance(trust, dict):
        return
    key = f"{trust.get('level')}:{trust.get('score')}:{trust.get('worst_component')}:{trust.get('missing_count')}:{trust.get('fallback_count')}"
    now = time.monotonic()
    with _data_trust_log_lock:
        if _data_trust_last_log_key == key and now - _data_trust_last_logged_at < DATA_TRUST_LOG_INTERVAL_SECONDS:
            return
        try:
            write_data_trust_log(
                DATA_TRUST_LOG_PATH,
                trust,
                timestamp_unix_ms=_parse_int(str(snapshot.get("timestamp_unix_ms"))) or int(time.time() * 1000),
            )
        except OSError:
            return
        _data_trust_last_logged_at = now
        _data_trust_last_log_key = key


def read_weather_snapshot() -> dict[str, Any]:
    config = _load_config()
    weather_config = config.get("weather") if isinstance(config.get("weather"), dict) else {}
    city = _empty_to_none(weather_config.get("city")) or "北京"
    location = _weather_location_for_city(city)
    cache_key = f"{city}|{location}"
    now = time.monotonic()
    with _weather_cache_lock:
        cache_matches = (
            _weather_cache_value is not None
            and _weather_cache_key == cache_key
        )
        if cache_matches and now < _weather_cache_expires_at:
            return dict(_weather_cache_value)

        _start_weather_refresh_locked(city, location, cache_key)
        if cache_matches:
            stale = dict(_weather_cache_value or {})
            if stale.get("source") == "weather_shim":
                stale["source"] = "weather_shim_stale"
            stale["status"] = "stale"
            return stale
        return _fallback_weather_snapshot(city, status="connecting")


def _start_weather_refresh_locked(
    city: str,
    location: str,
    cache_key: str,
) -> None:
    global _weather_refresh_thread, _weather_refreshing

    if _weather_refreshing:
        return
    _weather_refreshing = True
    _weather_refresh_thread = threading.Thread(
        target=_refresh_weather_cache,
        args=(city, location, cache_key),
        daemon=True,
    )
    _weather_refresh_thread.start()


def _refresh_weather_cache(city: str, location: str, cache_key: str) -> None:
    global _weather_cache_expires_at, _weather_cache_key, _weather_cache_value
    global _weather_refreshing

    query = urlencode({"location": location, "lang": "zh"})
    url = f"{WEATHER_SHIM_BASE_URL}/v7/weather/now?{query}"
    try:
        payload = _fetch_json(url, timeout=4.0)
        snapshot = _weather_from_qweather_payload(city, payload)
        succeeded = snapshot.get("source") == "weather_shim"
    except Exception:
        snapshot = _fallback_weather_snapshot(city, status="unavailable")
        succeeded = False

    completed_at = time.monotonic()
    with _weather_cache_lock:
        if succeeded or _weather_cache_value is None or _weather_cache_key != cache_key:
            _weather_cache_value = dict(snapshot)
            _weather_cache_key = cache_key
        else:
            stale = dict(_weather_cache_value)
            if stale.get("source") == "weather_shim":
                stale["source"] = "weather_shim_stale"
            stale["status"] = "stale"
            _weather_cache_value = stale
        _weather_cache_expires_at = completed_at + (
            WEATHER_CACHE_TTL_SECONDS
            if succeeded
            else WEATHER_FAILURE_CACHE_TTL_SECONDS
        )
        _weather_refreshing = False


def _reset_weather_cache_for_tests() -> None:
    global _weather_cache_expires_at, _weather_cache_key, _weather_cache_value
    global _weather_refresh_thread, _weather_refreshing

    thread = None
    with _weather_cache_lock:
        thread = _weather_refresh_thread
    if thread is not None and thread.is_alive():
        thread.join(timeout=1.0)
    with _weather_cache_lock:
        _weather_cache_value = None
        _weather_cache_key = None
        _weather_cache_expires_at = 0.0
        _weather_refreshing = False
        _weather_refresh_thread = None


def _weather_from_qweather_payload(city: str | None, payload: dict[str, Any]) -> dict[str, Any]:
    now = payload.get("now") if isinstance(payload, dict) else None
    if not isinstance(now, dict) or str(payload.get("code", "200")) != "200":
        return _fallback_weather_snapshot(city)

    temp = _round_float_or_none(now.get("temp"))
    condition = _empty_to_none(now.get("text"))
    humidity = _round_float_or_none(now.get("humidity"))
    aqi = _round_float_or_none(now.get("aqi"))
    wind_dir = _empty_to_none(now.get("windDir"))
    wind_scale = _empty_to_none(now.get("windScale"))
    wind_text = None
    if wind_dir and wind_scale:
        wind_text = f"{wind_dir} {wind_scale}级"
    elif wind_dir:
        wind_text = wind_dir

    return {
        "city": _weather_display_city(city),
        "summary": condition,
        "condition": condition,
        "temperature_celsius": temp,
        "temperature_c": temp,
        "temperature_text": f"{int(round(temp))}°C" if temp is not None else None,
        "aqi": int(round(aqi)) if aqi is not None else None,
        "humidity_percent": humidity,
        "wind_text": wind_text,
        "source": "weather_shim",
        "status": "live",
        "updated_at": _empty_to_none(payload.get("updateTime")) or _empty_to_none(now.get("obsTime")),
    }


def _fallback_weather_snapshot(
    city: str | None = None,
    *,
    status: str = "unavailable",
) -> dict[str, Any]:
    snapshot = empty_snapshot()["weather"]
    snapshot["city"] = _empty_to_none(city)
    snapshot["status"] = status
    return snapshot


def _weather_location_for_city(city: str | None) -> str:
    text = _empty_to_none(city)
    if text is None:
        return WEATHER_LOCATION_ALIASES["北京"]
    return WEATHER_LOCATION_ALIASES.get(text, WEATHER_LOCATION_ALIASES.get(text.lower(), text))


def _weather_display_city(city: str | None) -> str | None:
    text = _empty_to_none(city)
    if text == "田家庵":
        return "淮南·田家庵"
    return text


def _fetch_json(url: str, timeout: float) -> dict[str, Any]:
    with urllib.request.urlopen(url, timeout=timeout) as response:
        body = response.read().decode("utf-8")
    payload = json.loads(body)
    if not isinstance(payload, dict):
        raise ValueError("JSON payload must be an object")
    return payload


def _load_config() -> dict[str, Any]:
    global _config_cache, _config_cache_mtime

    try:
        mtime = os.path.getmtime(CONFIG_PATH)
    except OSError:
        return {}

    if _config_cache is not None and _config_cache_mtime == mtime:
        return _config_cache

    try:
        with open(CONFIG_PATH, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
    except (OSError, json.JSONDecodeError):
        return {}

    _config_cache = payload if isinstance(payload, dict) else {}
    _config_cache_mtime = mtime
    return _config_cache


def read_lhm_sensor_snapshot() -> dict[str, Any]:
    now = time.monotonic()
    with _lhm_cache_lock:
        if (
            _lhm_cache_value is not None
            and now < _lhm_cache_expires_at
            and not _lhm_last_refresh_failed
        ):
            live = dict(_lhm_cache_value)
            live["source"] = "lhm"
            live["status"] = "live"
            return live

        if now >= _lhm_cache_expires_at:
            _start_lhm_sensor_refresh_locked()

        if _lhm_cache_value is not None:
            age = now - _lhm_last_success_at if _lhm_last_success_at > 0 else float("inf")
            if age <= LHM_LAST_GOOD_MAX_AGE_SECONDS:
                stale = dict(_lhm_cache_value)
                stale["source"] = "lhm_stale"
                stale["status"] = "stale"
                return stale
            return {"source": "lhm", "status": "unavailable"}

        status = "unavailable" if _lhm_last_refresh_failed else "connecting"
        return {"source": "lhm", "status": status}


def _start_lhm_sensor_refresh_locked() -> None:
    global _lhm_refreshing

    if _lhm_refreshing:
        return
    _lhm_refreshing = True
    thread = threading.Thread(target=_refresh_lhm_sensor_cache, daemon=True)
    thread.start()


def _refresh_lhm_sensor_cache() -> None:
    global _lhm_cache_expires_at, _lhm_cache_value, _lhm_last_refresh_failed
    global _lhm_last_success_at, _lhm_refreshing

    try:
        payload, endpoint_url = _fetch_lhm_sensor_payload()
        snapshot = _lhm_sensor_snapshot_from_payload(payload)
        snapshot["endpoint_url"] = endpoint_url
    except Exception:
        snapshot = None

    with _lhm_cache_lock:
        completed_at = time.monotonic()
        if snapshot is not None:
            _lhm_cache_value = dict(snapshot)
            _lhm_last_success_at = completed_at
            _lhm_last_refresh_failed = False
            ttl = LHM_SENSOR_CACHE_TTL_SECONDS
        else:
            _lhm_last_refresh_failed = True
            ttl = LHM_SENSOR_FAILURE_CACHE_TTL_SECONDS
        _lhm_cache_expires_at = completed_at + ttl
        _lhm_refreshing = False


def _reset_lhm_cache_for_tests() -> None:
    global _lhm_cache_expires_at, _lhm_cache_value, _lhm_last_good_url
    global _lhm_last_refresh_failed, _lhm_last_success_at, _lhm_refreshing

    with _lhm_cache_lock:
        _lhm_cache_value = None
        _lhm_cache_expires_at = 0.0
        _lhm_last_good_url = None
        _lhm_last_success_at = 0.0
        _lhm_last_refresh_failed = False
        _lhm_refreshing = False


def _fetch_lhm_sensor_payload() -> tuple[dict[str, Any], str]:
    global _lhm_last_good_url

    errors: list[Exception] = []
    for url in _lhm_sensor_url_candidates():
        try:
            payload = _fetch_json(url, timeout=0.8)
        except Exception as exc:
            errors.append(exc)
            continue
        _lhm_last_good_url = url
        return payload, url
    if errors:
        raise errors[-1]
    raise OSError("no LibreHardwareMonitor endpoint configured")


def _lhm_sensor_url_candidates() -> list[str]:
    values: list[Any] = []
    for env_name in (
        "TURZX_LHM_SENSOR_URLS",
        "TURZX_LHM_SENSOR_URL",
        "LHM_SENSOR_URL",
    ):
        raw = os.environ.get(env_name)
        if raw:
            values.extend(part for part in re.split(r"[;,]", raw) if part.strip())

    config = _load_config()
    metrics = config.get("metrics") if isinstance(config.get("metrics"), dict) else {}
    lhm = config.get("lhm") if isinstance(config.get("lhm"), dict) else {}
    for value in (
        metrics.get("lhmUrls"),
        metrics.get("lhmUrl"),
        lhm.get("urls"),
        lhm.get("url"),
    ):
        if isinstance(value, list):
            values.extend(value)
        elif value is not None:
            values.append(value)
    values.extend(DEFAULT_LHM_SENSOR_URLS)

    normalized: list[str] = []
    if _lhm_last_good_url:
        normalized.append(_lhm_last_good_url)
    for value in values:
        url = _normalize_lhm_sensor_url(value)
        if url and url not in normalized:
            normalized.append(url)
    return normalized


def _normalize_lhm_sensor_url(value: Any) -> str | None:
    text = _empty_to_none(value)
    if text is None:
        return None
    if "://" not in text:
        text = f"http://{text}"
    parts = urlsplit(text)
    if not parts.netloc:
        return None
    path = parts.path.rstrip("/")
    if not path:
        path = "/data.json"
    elif not path.lower().endswith(".json"):
        path = f"{path}/data.json"
    return parts._replace(path=path, query="", fragment="").geturl()


def _lhm_sensor_snapshot_from_payload(payload: dict[str, Any]) -> dict[str, Any]:
    flat: dict[str, Any] = {}
    _flatten_lhm_payload(payload, "", flat)

    cpu_temp = None
    cpu_power = None
    cpu_vcore = None
    cpu_clock_effective = None
    gpu_voltage = None
    gpu_temp = None
    gpu_power = None
    gpu_core_clock = None
    gpu_memory_clock = None
    gpu_hotspot = None
    motherboard_temp = None
    module_temperatures: dict[int, float] = {}
    disk_values: dict[str, dict[str, Any]] = {}
    disk_temperature_candidates: dict[str, tuple[int, float]] = {}

    for key, raw in flat.items():
        lower = key.lower()
        value = _parse_float(raw)
        if value is None:
            continue

        if lower.endswith("/voltages/vcore"):
            cpu_vcore = round(value, 3)
        if lower.endswith("/powers/package") and "gpu" not in lower:
            cpu_power = round(value, 1)
        if "/clocks/" in lower and lower.endswith("/cores (average effective)"):
            cpu_clock_effective = round(value, 1)
        if "/temperatures/" in lower and "core (tctl/tdie)" in lower:
            cpu_temp = round(value, 1)
        if lower.endswith("/temperatures/system #1") and _valid_temperature(value):
            motherboard_temp = round(value, 1)
        dimm_match = re.search(r"/temperatures/dimm #(\d+)$", lower)
        if dimm_match and _valid_temperature(value):
            module_temperatures[int(dimm_match.group(1))] = round(value, 1)

        if "nvidia" in lower and lower.endswith("/voltages/gpu core voltage"):
            gpu_voltage = round(value, 3)
        elif "nvidia" in lower and lower.endswith("/powers/gpu package"):
            gpu_power = round(value, 1)
        elif "nvidia" in lower and lower.endswith("/clocks/gpu core"):
            gpu_core_clock = round(value, 1)
        elif "nvidia" in lower and lower.endswith("/clocks/gpu memory"):
            gpu_memory_clock = round(value, 1)
        elif "nvidia" in lower and lower.endswith("/temperatures/gpu core") and _valid_temperature(value):
            gpu_temp = round(value, 1)
        elif (
            "nvidia" in lower
            and "/temperatures/" in lower
            and ("hot spot" in lower or "junction" in lower)
            and _valid_temperature(value)
        ):
            if gpu_hotspot is None or "hot spot" in lower:
                gpu_hotspot = round(value, 1)

        disk_field = _lhm_disk_field(key)
        if disk_field is None:
            continue
        model, field, priority = disk_field
        values = disk_values.setdefault(model, {"model": model})
        if field == "temperature_celsius":
            if not _valid_temperature(value):
                continue
            candidate = disk_temperature_candidates.get(model)
            if candidate is None or priority < candidate[0]:
                disk_temperature_candidates[model] = (priority, round(value, 1))
        elif field in {"used_percent", "activity_percent"} and 0 <= value <= 100:
            values[field] = round(value, 1)
        elif field == "total_space_gb" and value > 0:
            values[field] = round(value, 1)

    for model, (_, temperature) in disk_temperature_candidates.items():
        disk_values.setdefault(model, {"model": model})["temperature_celsius"] = temperature

    return {
        "cpu_temperature_celsius": cpu_temp,
        "cpu_power_watts": cpu_power,
        "cpu_core_voltage": cpu_vcore,
        "cpu_clock_mhz": cpu_clock_effective,
        "gpu_core_voltage": gpu_voltage,
        "gpu_temperature_celsius": gpu_temp,
        "gpu_power_watts": gpu_power,
        "gpu_core_clock_mhz": gpu_core_clock,
        "gpu_memory_clock_mhz": gpu_memory_clock,
        "gpu_hotspot_temperature_celsius": gpu_hotspot,
        "motherboard_temperature_celsius": motherboard_temp,
        "module_temperatures_celsius": [
            module_temperatures[index] for index in sorted(module_temperatures)
        ],
        "disk_sensors": [
            disk_values[model] for model in sorted(disk_values, key=str.casefold)
        ],
        "source": "lhm",
        "status": "live",
    }


def _valid_temperature(value: float) -> bool:
    return 0.0 <= value <= 125.0


def _lhm_disk_field(path: str) -> tuple[str, str, int] | None:
    parts = [part.strip() for part in path.split("/") if part.strip()]
    if len(parts) < 3:
        return None
    category_index = next(
        (
            index
            for index, part in enumerate(parts)
            if part.casefold() in {"temperatures", "load", "data"}
        ),
        None,
    )
    if category_index is None or category_index < 1 or category_index >= len(parts) - 1:
        return None
    model = parts[category_index - 1]
    category = parts[category_index].casefold()
    leaf = parts[-1].casefold()

    if category == "temperatures":
        if leaf in {"composite", "composite temperature"}:
            return model, "temperature_celsius", 0
        if leaf == "temperature":
            return model, "temperature_celsius", 1
        if re.fullmatch(r"temperature #\d+", leaf):
            return model, "temperature_celsius", 2
    elif category == "load":
        if leaf == "used space":
            return model, "used_percent", 0
        if leaf == "total activity":
            return model, "activity_percent", 0
    elif category == "data" and leaf == "total space":
        return model, "total_space_gb", 0
    return None


def _flatten_lhm_payload(node: Any, path: str, out: dict[str, Any]) -> None:
    if not isinstance(node, dict):
        return
    text = _empty_to_none(node.get("Text")) or ""
    current = f"{path}/{text}" if text else path
    value = _empty_to_none(node.get("Value"))
    if value is not None:
        out[current] = value
    for child in node.get("Children") or []:
        _flatten_lhm_payload(child, current, out)


def _merge_lhm_sensors(snapshot: dict[str, Any]) -> None:
    sensors = read_lhm_sensor_snapshot()
    if not sensors or sensors.get("status") in {"connecting", "unavailable"}:
        return
    source_suffix = "lhm_stale" if sensors.get("status") == "stale" else "lhm"

    cpu = snapshot.get("cpu")
    if isinstance(cpu, dict):
        _set_if_not_none(cpu, "temperature_celsius", sensors.get("cpu_temperature_celsius"))
        _set_if_not_none(cpu, "power_watts", sensors.get("cpu_power_watts"))
        _set_if_not_none(cpu, "core_voltage", sensors.get("cpu_core_voltage"))
        cpu_clock_mhz = sensors.get("cpu_clock_mhz")
        _set_if_not_none(cpu, "clock_mhz", cpu_clock_mhz)
        if cpu_clock_mhz is not None:
            cpu["clock_ghz"] = _mhz_to_ghz(cpu_clock_mhz)
        if any(cpu.get(key) is not None for key in ("temperature_celsius", "power_watts", "core_voltage", "clock_mhz")):
            cpu["source"] = _append_source(cpu.get("source"), source_suffix)

    gpu = snapshot.get("gpu")
    if isinstance(gpu, dict):
        _set_if_not_none(gpu, "core_voltage", sensors.get("gpu_core_voltage"))
        gpu_temperature = sensors.get("gpu_temperature_celsius")
        _set_if_not_none(gpu, "temperature_c", gpu_temperature)
        _set_if_not_none(gpu, "temperature_celsius", gpu_temperature)
        _set_if_not_none(gpu, "power_watts", sensors.get("gpu_power_watts"))
        gpu_core_clock = sensors.get("gpu_core_clock_mhz")
        _set_if_not_none(gpu, "core_clock_mhz", gpu_core_clock)
        if gpu_core_clock is not None:
            gpu["core_clock_ghz"] = _mhz_to_ghz(gpu_core_clock)
        gpu_memory_clock = sensors.get("gpu_memory_clock_mhz")
        _set_if_not_none(gpu, "memory_clock_mhz", gpu_memory_clock)
        _set_if_not_none(gpu, "mem_clock_mhz", gpu_memory_clock)
        if gpu_memory_clock is not None:
            gpu["memory_clock_ghz"] = _mhz_to_ghz(gpu_memory_clock)
        _set_if_not_none(
            gpu,
            "hotspot_temperature_celsius",
            sensors.get("gpu_hotspot_temperature_celsius"),
        )
        if any(
            sensors.get(key) is not None
            for key in (
                "gpu_core_voltage",
                "gpu_temperature_celsius",
                "gpu_power_watts",
                "gpu_core_clock_mhz",
                "gpu_memory_clock_mhz",
            )
        ):
            gpu["source"] = _append_source(gpu.get("source"), source_suffix)

    memory = snapshot.get("memory")
    if isinstance(memory, dict):
        motherboard_temperature = sensors.get("motherboard_temperature_celsius")
        module_temperatures = sensors.get("module_temperatures_celsius")
        _set_if_not_none(
            memory,
            "motherboard_temperature_celsius",
            motherboard_temperature,
        )
        if isinstance(module_temperatures, list):
            memory["module_temperatures_celsius"] = list(module_temperatures)
        if motherboard_temperature is not None or module_temperatures:
            memory["source"] = _append_source(memory.get("source"), source_suffix)


def _set_if_not_none(target: dict[str, Any], key: str, value: Any) -> None:
    if value is not None:
        target[key] = value


def _append_source(source: Any, suffix: str) -> str:
    text = _empty_to_none(source)
    if text is None or text == "fallback":
        return suffix
    if suffix in text.split("+"):
        return text
    return f"{text}+{suffix}"


def _configured_refresh_interval_seconds() -> float | None:
    config = _load_config()
    for section_name, key in (("metrics", "pollMs"), ("screen", "dataRefreshMs")):
        section = config.get(section_name)
        if not isinstance(section, dict):
            continue
        value = _parse_float(section.get(key))
        if value is not None and value > 0:
            return round(value / 1000.0, 3)
    return None


class _PdhFmtCounterValueUnion(ctypes.Union):
    _fields_ = [
        ("long_value", ctypes.c_long),
        ("double_value", ctypes.c_double),
        ("large_value", ctypes.c_longlong),
        ("ansi_string_value", ctypes.c_char_p),
        ("wide_string_value", ctypes.c_wchar_p),
    ]


class _PdhFmtCounterValue(ctypes.Structure):
    _anonymous_ = ("value",)
    _fields_ = [
        ("status", wintypes.DWORD),
        ("value", _PdhFmtCounterValueUnion),
    ]


class WindowsDpcTimeSampler:
    PDH_FMT_DOUBLE = 0x00000200
    COUNTER_PATH = r"\Processor Information(_Total)\% DPC Time"

    def __init__(
        self,
        read_value: Callable[[], float | None] | None = None,
    ):
        self._read_value = read_value
        self._lock = threading.Lock()
        self._pdh: Any = None
        self._query = ctypes.c_void_p()
        self._counter = ctypes.c_void_p()
        self._initialized = False

    def sample(self) -> float | None:
        try:
            value = (
                self._read_value()
                if self._read_value is not None
                else self._sample_windows_counter()
            )
        except Exception:
            return None
        if value is None:
            return None
        try:
            parsed = float(value)
        except (TypeError, ValueError):
            return None
        if not math.isfinite(parsed):
            return None
        return round(max(0.0, min(100.0, parsed)), 3)

    def _sample_windows_counter(self) -> float | None:
        if os.name != "nt":
            return None

        with self._lock:
            if not self._initialized:
                if not self._initialize_windows_counter():
                    return None
                # PDH rate counters require a baseline collection.
                return None

            if self._pdh.PdhCollectQueryData(self._query) != 0:
                return None
            value_type = wintypes.DWORD()
            formatted = _PdhFmtCounterValue()
            status = self._pdh.PdhGetFormattedCounterValue(
                self._counter,
                self.PDH_FMT_DOUBLE,
                ctypes.byref(value_type),
                ctypes.byref(formatted),
            )
            if status != 0 or formatted.status != 0:
                return None
            return float(formatted.double_value)

    def _initialize_windows_counter(self) -> bool:
        try:
            pdh = ctypes.WinDLL("pdh", use_last_error=True)
            pdh.PdhOpenQueryW.argtypes = [
                wintypes.LPCWSTR,
                ctypes.c_size_t,
                ctypes.POINTER(ctypes.c_void_p),
            ]
            pdh.PdhOpenQueryW.restype = ctypes.c_long
            pdh.PdhAddEnglishCounterW.argtypes = [
                ctypes.c_void_p,
                wintypes.LPCWSTR,
                ctypes.c_size_t,
                ctypes.POINTER(ctypes.c_void_p),
            ]
            pdh.PdhAddEnglishCounterW.restype = ctypes.c_long
            pdh.PdhCollectQueryData.argtypes = [ctypes.c_void_p]
            pdh.PdhCollectQueryData.restype = ctypes.c_long
            pdh.PdhGetFormattedCounterValue.argtypes = [
                ctypes.c_void_p,
                wintypes.DWORD,
                ctypes.POINTER(wintypes.DWORD),
                ctypes.POINTER(_PdhFmtCounterValue),
            ]
            pdh.PdhGetFormattedCounterValue.restype = ctypes.c_long
            pdh.PdhCloseQuery.argtypes = [ctypes.c_void_p]
            pdh.PdhCloseQuery.restype = ctypes.c_long

            query = ctypes.c_void_p()
            counter = ctypes.c_void_p()
            if pdh.PdhOpenQueryW(None, 0, ctypes.byref(query)) != 0:
                return False
            if (
                pdh.PdhAddEnglishCounterW(
                    query,
                    self.COUNTER_PATH,
                    0,
                    ctypes.byref(counter),
                )
                != 0
            ):
                pdh.PdhCloseQuery(query)
                return False
            if pdh.PdhCollectQueryData(query) != 0:
                pdh.PdhCloseQuery(query)
                return False
        except Exception:
            return False

        self._pdh = pdh
        self._query = query
        self._counter = counter
        self._initialized = True
        return True

    def close(self) -> None:
        with self._lock:
            if self._pdh is not None and self._query:
                try:
                    self._pdh.PdhCloseQuery(self._query)
                except Exception:
                    pass
            self._pdh = None
            self._query = ctypes.c_void_p()
            self._counter = ctypes.c_void_p()
            self._initialized = False


_DPC_TIME_SAMPLER = WindowsDpcTimeSampler()


def read_cpu_snapshot() -> dict[str, Any]:
    try:
        usage_percent = _CPU_SAMPLER.sample()
        if usage_percent is None and _CPU_SAMPLER.has_baseline:
            time.sleep(CPU_INITIAL_SAMPLE_WAIT_SECONDS)
            usage_percent = _CPU_SAMPLER.sample()
    except Exception:
        usage_percent = None

    if usage_percent is None:
        psutil_snapshot = _read_cpu_snapshot_psutil()
        return psutil_snapshot or _fallback_cpu_snapshot()

    _append_history(_cpu_history, usage_percent)
    return _cpu_snapshot(
        source="win32_getsystemtimes",
        usage_percent=usage_percent,
        clock_mhz=None,
    )


def read_gpu_snapshot() -> dict[str, Any]:
    global _gpu_cache_expires_at, _gpu_cache_value, _gpu_last_good_at, _gpu_last_good_value

    now = time.monotonic()
    if _gpu_cache_value is not None and now < _gpu_cache_expires_at:
        return dict(_gpu_cache_value)

    snapshot = _read_gpu_snapshot_nvml()
    if snapshot is None:
        snapshot = _read_gpu_snapshot_nvidia_smi()
    if snapshot is None:
        if _gpu_last_good_value is not None and now - _gpu_last_good_at <= GPU_LAST_GOOD_MAX_AGE_SECONDS:
            snapshot = dict(_gpu_last_good_value)
            source = str(snapshot.get("source") or "gpu").removesuffix("+stale")
            snapshot["source"] = source + "+stale"
            snapshot["status"] = "stale"
        else:
            snapshot = _fallback_gpu_snapshot()
    else:
        _gpu_last_good_value = dict(snapshot)
        _gpu_last_good_at = now

    ttl = (
        GPU_FAILURE_CACHE_TTL_SECONDS
        if snapshot["source"] == "fallback"
        else GPU_CACHE_TTL_SECONDS
    )
    _gpu_cache_value = dict(snapshot)
    _gpu_cache_expires_at = now + ttl
    return snapshot


class CpuUsageSampler:
    def __init__(self, read_times: Callable[[], tuple[int, int, int] | None] | None = None):
        self._read_times = read_times or _read_windows_cpu_times
        self._last = self._safe_read_times()

    @property
    def has_baseline(self) -> bool:
        return self._last is not None

    def sample(self) -> float | None:
        current = self._safe_read_times()
        if current is None:
            return None

        previous = self._last
        self._last = current
        if previous is None:
            return None

        idle_delta = current[0] - previous[0]
        kernel_delta = current[1] - previous[1]
        user_delta = current[2] - previous[2]
        total_delta = kernel_delta + user_delta
        if total_delta <= 0 or idle_delta < 0:
            return None

        busy_delta = max(total_delta - idle_delta, 0)
        usage = min(max((busy_delta / total_delta) * 100.0, 0.0), 100.0)
        return round(usage, 1)

    def _safe_read_times(self) -> tuple[int, int, int] | None:
        try:
            return self._read_times()
        except Exception:
            return None


def _read_windows_cpu_times() -> tuple[int, int, int] | None:
    if os.name != "nt":
        return None

    class FILETIME(ctypes.Structure):
        _fields_ = [
            ("dwLowDateTime", wintypes.DWORD),
            ("dwHighDateTime", wintypes.DWORD),
        ]

    idle = FILETIME()
    kernel = FILETIME()
    user = FILETIME()
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetSystemTimes.argtypes = [
        ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME),
        ctypes.POINTER(FILETIME),
    ]
    kernel32.GetSystemTimes.restype = wintypes.BOOL

    if not kernel32.GetSystemTimes(
        ctypes.byref(idle),
        ctypes.byref(kernel),
        ctypes.byref(user),
    ):
        return None

    return (
        _filetime_to_int(idle),
        _filetime_to_int(kernel),
        _filetime_to_int(user),
    )


def _filetime_to_int(value: Any) -> int:
    return (int(value.dwHighDateTime) << 32) | int(value.dwLowDateTime)


def _fallback_cpu_snapshot() -> dict[str, Any]:
    return _cpu_snapshot(
        source="fallback",
        usage_percent=None,
        clock_mhz=None,
    )


def _read_cpu_snapshot_psutil() -> dict[str, Any] | None:
    psutil = _optional_import("psutil")
    if psutil is None:
        return None

    try:
        usage_percent = _round_float_or_none(psutil.cpu_percent(interval=None))
    except Exception:
        usage_percent = None

    if usage_percent is None:
        return None

    if usage_percent is not None:
        _append_history(_cpu_history, usage_percent)
    return _cpu_snapshot(
        source="psutil",
        usage_percent=usage_percent,
        clock_mhz=None,
    )


def _cpu_snapshot(
    *,
    source: str,
    usage_percent: Any,
    clock_mhz: Any,
) -> dict[str, Any]:
    usage_value = _round_float_or_none(usage_percent)
    clock_value = _round_float_or_none(clock_mhz)
    return {
        "model": _read_cpu_model(),
        "usage_percent": usage_value,
        "temperature_celsius": None,
        "power_watts": None,
        "clock_ghz": _mhz_to_ghz(clock_value),
        "clock_mhz": clock_value,
        "core_voltage": None,
        "load_history_percent": list(_cpu_history),
        "logical_count": os.cpu_count(),
        "status": _load_status(usage_value),
        "source": source,
    }


def _read_gpu_snapshot_nvml() -> dict[str, Any] | None:
    pynvml = _optional_import("pynvml")
    if pynvml is None:
        return None

    initialized = False
    try:
        pynvml.nvmlInit()
        initialized = True
        if pynvml.nvmlDeviceGetCount() < 1:
            return None

        handle = pynvml.nvmlDeviceGetHandleByIndex(0)
        utilization = _safe_nvml_value(
            lambda: pynvml.nvmlDeviceGetUtilizationRates(handle)
        )
        usage_percent = getattr(utilization, "gpu", None) if utilization else None
        name = _decode_text(_safe_nvml_value(lambda: pynvml.nvmlDeviceGetName(handle)))
        temperature_c = _safe_nvml_value(
            lambda: pynvml.nvmlDeviceGetTemperature(
                handle,
                pynvml.NVML_TEMPERATURE_GPU,
            )
        )
        core_clock_mhz = _safe_nvml_value(
            lambda: pynvml.nvmlDeviceGetClockInfo(
                handle,
                pynvml.NVML_CLOCK_GRAPHICS,
            )
        )
        mem_clock_mhz = _safe_nvml_value(
            lambda: pynvml.nvmlDeviceGetClockInfo(
                handle,
                pynvml.NVML_CLOCK_MEM,
            )
        )
        power_mw = _safe_nvml_value(lambda: pynvml.nvmlDeviceGetPowerUsage(handle))
        memory_info = _safe_nvml_value(lambda: pynvml.nvmlDeviceGetMemoryInfo(handle))

        return _gpu_snapshot(
            source="nvml",
            name=name,
            usage_percent=usage_percent,
            temperature_c=temperature_c,
            power_watts=_mw_to_watts(power_mw),
            core_clock_mhz=core_clock_mhz,
            memory_clock_mhz=mem_clock_mhz,
            vram_used_gb=_bytes_to_gb(int(memory_info.used)) if memory_info else None,
            vram_total_gb=_bytes_to_gb(int(memory_info.total)) if memory_info else None,
        )
    except Exception:
        return None
    finally:
        if initialized:
            _safe_nvml_value(pynvml.nvmlShutdown)


def _safe_nvml_value(read_value: Callable[[], Any]) -> Any:
    try:
        return read_value()
    except Exception:
        return None


def _read_gpu_snapshot_nvidia_smi() -> dict[str, Any] | None:
    if shutil.which("nvidia-smi") is None:
        return None

    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    try:
        result = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=name,utilization.gpu,temperature.gpu,"
                "power.draw,clocks.current.graphics,clocks.current.memory,"
                "memory.used,memory.total",
                "--format=csv,noheader,nounits",
            ],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=NVIDIA_SMI_TIMEOUT_SECONDS,
            creationflags=creationflags,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None

    if result.returncode != 0:
        return None

    return _parse_nvidia_smi_csv(result.stdout)


def _parse_nvidia_smi_csv(output: str) -> dict[str, Any] | None:
    for row in csv.reader(output.splitlines()):
        if not row or not any(cell.strip() for cell in row):
            continue
        if len(row) < 5:
            return None

        return _gpu_snapshot(
            source="nvidia-smi",
            name=_empty_to_none(row[0]),
            usage_percent=_parse_float(row[1]),
            temperature_c=_parse_float(row[2]),
            power_watts=_parse_float(row[3]) if len(row) >= 8 else None,
            core_clock_mhz=_parse_float(row[4] if len(row) >= 8 else row[3]),
            memory_clock_mhz=_parse_float(row[5] if len(row) >= 8 else row[4]),
            vram_used_gb=_mib_to_gb(_parse_float(row[6])) if len(row) >= 8 else None,
            vram_total_gb=_mib_to_gb(_parse_float(row[7])) if len(row) >= 8 else None,
        )

    return None


def _gpu_snapshot(
    *,
    source: str,
    name: str | None,
    usage_percent: Any,
    temperature_c: Any,
    power_watts: Any,
    core_clock_mhz: Any,
    memory_clock_mhz: Any,
    vram_used_gb: Any,
    vram_total_gb: Any,
) -> dict[str, Any]:
    usage_value = _round_float_or_none(usage_percent)
    _append_history(_gpu_history, usage_value)
    temp_value = _round_float_or_none(temperature_c)
    core_clock_value = _round_float_or_none(core_clock_mhz)
    memory_clock_value = _round_float_or_none(memory_clock_mhz)
    return {
        "model": name,
        "usage_percent": usage_value,
        "temperature_c": temp_value,
        "temperature_celsius": temp_value,
        "name": name,
        "power_watts": _round_float2_or_none(power_watts),
        "core_voltage": None,
        "core_clock_mhz": core_clock_value,
        "core_clock_ghz": _mhz_to_ghz(core_clock_value),
        "memory_clock_mhz": memory_clock_value,
        "memory_clock_ghz": _mhz_to_ghz(memory_clock_value),
        "mem_clock_mhz": memory_clock_value,
        "vram_used_gb": _round_float2_or_none(vram_used_gb),
        "vram_total_gb": _round_float2_or_none(vram_total_gb),
        "load_history_percent": list(_gpu_history),
        "status": _load_status(usage_value),
        "source": source,
    }


def _fallback_gpu_snapshot() -> dict[str, Any]:
    return _gpu_snapshot(
        source="fallback",
        name=None,
        usage_percent=None,
        temperature_c=None,
        power_watts=None,
        core_clock_mhz=None,
        memory_clock_mhz=None,
        vram_used_gb=None,
        vram_total_gb=None,
    )


def _optional_import(name: str) -> Any:
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", FutureWarning)
            return importlib.import_module(name)
    except Exception:
        return None


def _read_cpu_model() -> str | None:
    if os.name == "nt":
        try:
            import winreg

            with winreg.OpenKey(
                winreg.HKEY_LOCAL_MACHINE,
                r"HARDWARE\DESCRIPTION\System\CentralProcessor\0",
            ) as key:
                value, _ = winreg.QueryValueEx(key, "ProcessorNameString")
                return _empty_to_none(value)
        except Exception:
            pass

    return _empty_to_none(platform.processor())


def _merge_gpu_vram_into_memory(snapshot: dict[str, Any]) -> None:
    memory = snapshot.get("memory")
    gpu = snapshot.get("gpu")
    if not isinstance(memory, dict) or not isinstance(gpu, dict):
        return

    used = _round_float2_or_none(gpu.get("vram_used_gb"))
    total = _round_float2_or_none(gpu.get("vram_total_gb"))
    memory["vram_used_gb"] = used
    memory["vram_total_gb"] = total
    memory["vram_usage_percent"] = (
        round((used / total) * 100.0, 1)
        if used is not None and total not in (None, 0)
        else None
    )


def _append_history(history: deque[float], value: float | None) -> None:
    if value is not None:
        history.append(float(value))


def _mhz_to_ghz(value: float | None) -> float | None:
    return round(value / 1000.0, 3) if value is not None else None


def _mw_to_watts(value: Any) -> float | None:
    parsed = _parse_float(value)
    return round(parsed / 1000.0, 2) if parsed is not None else None


def _mib_to_gb(value: float | None) -> float | None:
    return round(value / 1024.0, 2) if value is not None else None


def _reset_gpu_cache_for_tests() -> None:
    global _gpu_cache_expires_at, _gpu_cache_value, _gpu_last_good_at, _gpu_last_good_value
    _gpu_cache_value = None
    _gpu_cache_expires_at = 0.0
    _gpu_last_good_value = None
    _gpu_last_good_at = 0.0


def _load_status(usage_percent: float | None) -> str:
    if usage_percent is None or usage_percent < 30.0:
        return "idle"
    if usage_percent < 85.0:
        return "active"
    return "busy"


def _decode_text(value: Any) -> str | None:
    if isinstance(value, bytes):
        value = value.decode("utf-8", errors="replace")
    return _empty_to_none(value)


def _empty_to_none(value: Any) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    if not text or text.upper() in {"N/A", "[N/A]", "[NOT SUPPORTED]"}:
        return None
    return text


def _parse_float(value: Any) -> float | None:
    text = _empty_to_none(value)
    if text is None:
        return None
    for suffix in ("%", "MHz", "C"):
        if text.endswith(suffix):
            text = text[: -len(suffix)].strip()
    match = re.search(r"[-+]?[0-9]+(?:[.,][0-9]+)?", text)
    if match:
        text = match.group(0).replace(",", ".")
    try:
        return float(text)
    except ValueError:
        return None


def _round_float_or_none(value: Any) -> float | None:
    parsed = _parse_float(value)
    return round(parsed, 1) if parsed is not None else None


def _round_float2_or_none(value: Any) -> float | None:
    parsed = _parse_float(value)
    return round(parsed, 2) if parsed is not None else None


def _positive_float_or_none(value: Any) -> float | None:
    parsed = _parse_float(value)
    return parsed if parsed is not None and parsed > 0 else None


def _round_int_or_none(value: Any) -> int | None:
    parsed = _parse_float(value)
    return int(round(parsed)) if parsed is not None else None


_CPU_SAMPLER = CpuUsageSampler()


def read_timeaudit_latest_snapshot() -> dict[str, Any]:
    if not TIMEAUDIT_DSN:
        return {
            "_status": "disabled",
            "_detail": "TIMEAUDIT_DSN 未配置",
        }

    now = time.monotonic()
    with _timeaudit_cache_lock:
        if _timeaudit_cache_value is not None and now < _timeaudit_cache_expires_at:
            return dict(_timeaudit_cache_value)

        stale = (
            dict(_timeaudit_cache_value)
            if _timeaudit_cache_value is not None
            else {
                "_status": "connecting",
                "_detail": "正在连接 TimeAudit",
            }
        )
        _start_timeaudit_refresh_locked()
        return stale


def _start_timeaudit_refresh_locked() -> None:
    global _timeaudit_refreshing

    if _timeaudit_refreshing:
        return
    _timeaudit_refreshing = True
    thread = threading.Thread(target=_refresh_timeaudit_cache, daemon=True)
    thread.start()


def _refresh_timeaudit_cache() -> None:
    global _timeaudit_cache_expires_at, _timeaudit_cache_value, _timeaudit_refreshing

    snapshot: dict[str, Any]
    try:
        snapshot = asyncio.run(_read_timeaudit_latest_snapshot_async())
    except Exception as exc:
        snapshot = {
            "_status": "error",
            "_detail": type(exc).__name__,
        }

    with _timeaudit_cache_lock:
        if (
            snapshot.get("_status") == "error"
            and isinstance(_timeaudit_cache_value, dict)
            and _timeaudit_cache_value.get("timestamp") is not None
        ):
            preserved = dict(_timeaudit_cache_value)
            preserved["_refresh_error"] = snapshot.get("_detail")
            _timeaudit_cache_value = preserved
        else:
            _timeaudit_cache_value = dict(snapshot)
        _timeaudit_cache_expires_at = time.monotonic() + TIMEAUDIT_CACHE_TTL_SECONDS
        _timeaudit_refreshing = False


def _reset_timeaudit_cache_for_tests() -> None:
    global _timeaudit_cache_expires_at, _timeaudit_cache_value, _timeaudit_refreshing

    with _timeaudit_cache_lock:
        _timeaudit_cache_value = None
        _timeaudit_cache_expires_at = 0.0
        _timeaudit_refreshing = False


async def _read_timeaudit_latest_snapshot_async() -> dict[str, Any]:
    if not TIMEAUDIT_DSN:
        return {
            "_status": "disabled",
            "_detail": "TIMEAUDIT_DSN 未配置",
        }

    asyncpg = _optional_import("asyncpg")
    if asyncpg is None:
        raise RuntimeError("asyncpg unavailable")

    conn = await asyncpg.connect(
        TIMEAUDIT_DSN,
        timeout=TIMEAUDIT_CONNECT_TIMEOUT_SECONDS,
        command_timeout=TIMEAUDIT_QUERY_TIMEOUT_SECONDS,
    )
    try:
        row = await asyncio.wait_for(
            conn.fetchrow(
                """
                SELECT timestamp,
                       current_fps,
                       average_fps,
                       one_percent_low_fps,
                       frametime_ms,
                       frametime_jitter
                FROM public.fact_system_hardware
                ORDER BY timestamp DESC
                LIMIT 1
                """
            ),
            timeout=TIMEAUDIT_QUERY_TIMEOUT_SECONDS,
        )
    finally:
        try:
            await asyncio.wait_for(
                conn.close(),
                timeout=TIMEAUDIT_QUERY_TIMEOUT_SECONDS,
            )
        except Exception:
            pass

    if not row:
        return {
            "_status": "idle",
            "_detail": "TimeAudit 尚无硬件样本",
        }
    result = dict(row)
    result["_status"] = "ok"
    return result


def read_fps_snapshot() -> dict[str, Any]:
    timeaudit = read_timeaudit_latest_snapshot()
    cache_status = (_empty_to_none(timeaudit.get("_status")) or "error").lower()
    source = "disabled" if cache_status == "disabled" else "timeaudit_postgres"
    base = {
        "current": None,
        "average": None,
        "low_1_percent": None,
        "frame_time_ms": None,
        "status": cache_status,
        "source": source,
        "sample_age_seconds": None,
        "detail": _empty_to_none(timeaudit.get("_detail")),
    }
    if cache_status in {"disabled", "connecting", "error"}:
        return base
    if cache_status == "idle" and timeaudit.get("timestamp") is None:
        return base

    sample_age = _sample_age_seconds(timeaudit.get("timestamp"))
    base["sample_age_seconds"] = sample_age
    if sample_age is None:
        base["status"] = "error"
        base["detail"] = "TimeAudit 样本缺少有效时间戳"
        return base

    current = _positive_float_or_none(timeaudit.get("current_fps"))
    average = _positive_float_or_none(timeaudit.get("average_fps"))
    low_1_percent = _positive_float_or_none(timeaudit.get("one_percent_low_fps"))
    frame_time = _positive_float_or_none(timeaudit.get("frametime_ms"))
    base.update(
        {
            "current": current,
            "average": average,
            "low_1_percent": low_1_percent,
            "frame_time_ms": frame_time,
        }
    )
    if sample_age > FPS_SAMPLE_FRESH_SECONDS:
        base["status"] = "stale"
        base["detail"] = f"最近帧样本已过期（{sample_age:.1f}s）"
    elif current is not None:
        base["status"] = "active"
        base["detail"] = "PresentMon 帧采集正常"
    else:
        base["status"] = "idle"
        base["detail"] = "PresentMon 正常，等待游戏启动"
    return base


def _sample_age_seconds(
    value: Any,
    now: dt.datetime | None = None,
) -> float | None:
    timestamp: dt.datetime
    if isinstance(value, dt.datetime):
        timestamp = value
    elif isinstance(value, str):
        text = value.strip()
        if not text:
            return None
        if text.endswith("Z"):
            text = text[:-1] + "+00:00"
        try:
            timestamp = dt.datetime.fromisoformat(text)
        except ValueError:
            return None
    else:
        return None

    if timestamp.tzinfo is None:
        timestamp = timestamp.replace(tzinfo=dt.timezone.utc)
    current = now or dt.datetime.now(dt.timezone.utc)
    if current.tzinfo is None:
        current = current.replace(tzinfo=dt.timezone.utc)
    age = (current.astimezone(dt.timezone.utc) - timestamp.astimezone(dt.timezone.utc)).total_seconds()
    return round(max(0.0, age), 1)


def read_memory_snapshot() -> dict[str, Any]:
    if os.name != "nt":
        return _fallback_memory_snapshot()

    class MEMORYSTATUSEX(ctypes.Structure):
        _fields_ = [
            ("dwLength", wintypes.DWORD),
            ("dwMemoryLoad", wintypes.DWORD),
            ("ullTotalPhys", ctypes.c_ulonglong),
            ("ullAvailPhys", ctypes.c_ulonglong),
            ("ullTotalPageFile", ctypes.c_ulonglong),
            ("ullAvailPageFile", ctypes.c_ulonglong),
            ("ullTotalVirtual", ctypes.c_ulonglong),
            ("ullAvailVirtual", ctypes.c_ulonglong),
            ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
        ]

    status = MEMORYSTATUSEX()
    status.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GlobalMemoryStatusEx.argtypes = [ctypes.POINTER(MEMORYSTATUSEX)]
    kernel32.GlobalMemoryStatusEx.restype = wintypes.BOOL
    if not kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
        return _fallback_memory_snapshot()

    total = int(status.ullTotalPhys)
    available = int(status.ullAvailPhys)
    used = max(total - available, 0)
    return {
        "used_percent": round(float(status.dwMemoryLoad), 1),
        "used_gb": _bytes_to_gb(used),
        "available_gb": _bytes_to_gb(available),
        "total_gb": _bytes_to_gb(total),
        "source": "win32",
    }


def _fallback_memory_snapshot() -> dict[str, Any]:
    return {
        "used_percent": None,
        "used_gb": None,
        "available_gb": None,
        "total_gb": None,
        "source": "fallback",
    }


def read_foreground_app() -> dict[str, Any]:
    fallback = {
        "title": None,
        "process_id": None,
        "process_name": None,
        "exe_path": None,
        "source": "fallback",
    }
    if os.name != "nt":
        return fallback

    user32 = ctypes.WinDLL("user32", use_last_error=True)
    user32.GetForegroundWindow.restype = wintypes.HWND
    user32.GetWindowTextLengthW.argtypes = [wintypes.HWND]
    user32.GetWindowTextLengthW.restype = ctypes.c_int
    user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
    user32.GetWindowTextW.restype = ctypes.c_int
    user32.GetWindowThreadProcessId.argtypes = [
        wintypes.HWND,
        ctypes.POINTER(wintypes.DWORD),
    ]
    user32.GetWindowThreadProcessId.restype = wintypes.DWORD

    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return fallback

    length = user32.GetWindowTextLengthW(hwnd)
    title_buffer = ctypes.create_unicode_buffer(length + 1)
    user32.GetWindowTextW(hwnd, title_buffer, length + 1)

    pid = wintypes.DWORD(0)
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))

    exe_path = _process_image_path(pid.value) if pid.value else None
    return {
        "title": title_buffer.value or None,
        "process_id": int(pid.value) if pid.value else None,
        "process_name": os.path.basename(exe_path) if exe_path else None,
        "exe_path": exe_path,
        "source": "win32",
    }


def _process_image_path(pid: int) -> str | None:
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    kernel32.OpenProcess.restype = wintypes.HANDLE
    kernel32.QueryFullProcessImageNameW.argtypes = [
        wintypes.HANDLE,
        wintypes.DWORD,
        wintypes.LPWSTR,
        ctypes.POINTER(wintypes.DWORD),
    ]
    kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL

    process_query_limited_information = 0x1000
    handle = kernel32.OpenProcess(process_query_limited_information, False, pid)
    if not handle:
        return None

    try:
        size = wintypes.DWORD(32768)
        path_buffer = ctypes.create_unicode_buffer(size.value)
        ok = kernel32.QueryFullProcessImageNameW(
            handle,
            0,
            path_buffer,
            ctypes.byref(size),
        )
        return path_buffer.value if ok else None
    finally:
        kernel32.CloseHandle(handle)


def enumerate_disks() -> list[dict[str, Any]]:
    if os.name != "nt":
        return [_fallback_disk_info()]

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.GetLogicalDrives.restype = wintypes.DWORD
    kernel32.GetDriveTypeW.argtypes = [wintypes.LPCWSTR]
    kernel32.GetDriveTypeW.restype = wintypes.UINT
    kernel32.GetDiskFreeSpaceExW.argtypes = [
        wintypes.LPCWSTR,
        ctypes.POINTER(ctypes.c_ulonglong),
        ctypes.POINTER(ctypes.c_ulonglong),
        ctypes.POINTER(ctypes.c_ulonglong),
    ]
    kernel32.GetDiskFreeSpaceExW.restype = wintypes.BOOL
    kernel32.GetVolumeInformationW.argtypes = [
        wintypes.LPCWSTR,
        wintypes.LPWSTR,
        wintypes.DWORD,
        ctypes.POINTER(wintypes.DWORD),
        ctypes.POINTER(wintypes.DWORD),
        ctypes.POINTER(wintypes.DWORD),
        wintypes.LPWSTR,
        wintypes.DWORD,
    ]
    kernel32.GetVolumeInformationW.restype = wintypes.BOOL

    mask = kernel32.GetLogicalDrives()
    disks: list[dict[str, Any]] = []

    for index in range(26):
        if not mask & (1 << index):
            continue
        root = f"{chr(ord('A') + index)}:\\"
        disks.append(_windows_disk_info(kernel32, root))

    return disks or [_fallback_disk_info()]


def _windows_disk_info(kernel32: Any, root: str) -> dict[str, Any]:
    drive_type_code = int(kernel32.GetDriveTypeW(root))
    label = _windows_volume_label(kernel32, root)

    free_to_caller = ctypes.c_ulonglong(0)
    total_bytes = ctypes.c_ulonglong(0)
    total_free_bytes = ctypes.c_ulonglong(0)
    ok = kernel32.GetDiskFreeSpaceExW(
        root,
        ctypes.byref(free_to_caller),
        ctypes.byref(total_bytes),
        ctypes.byref(total_free_bytes),
    )

    total = int(total_bytes.value) if ok else 0
    free = int(total_free_bytes.value) if ok else 0
    used_percent = round(((total - free) / total) * 100, 1) if total else None

    return {
        "drive": root,
        "label": label,
        "used_percent": used_percent,
        "free_gb": _bytes_to_gb(free) if total else None,
        "total_gb": _bytes_to_gb(total) if total else None,
        "drive_type": DRIVE_TYPE_NAMES.get(drive_type_code, "unknown"),
    }


def _windows_volume_label(kernel32: Any, root: str) -> str | None:
    label_buffer = ctypes.create_unicode_buffer(261)
    filesystem_buffer = ctypes.create_unicode_buffer(261)
    serial_number = wintypes.DWORD(0)
    max_component_length = wintypes.DWORD(0)
    filesystem_flags = wintypes.DWORD(0)

    ok = kernel32.GetVolumeInformationW(
        root,
        label_buffer,
        len(label_buffer),
        ctypes.byref(serial_number),
        ctypes.byref(max_component_length),
        ctypes.byref(filesystem_flags),
        filesystem_buffer,
        len(filesystem_buffer),
    )
    return label_buffer.value or None if ok else None


def _fallback_disk_info() -> dict[str, Any]:
    usage = shutil.disk_usage(os.path.abspath(os.sep))
    used = usage.total - usage.free
    return {
        "drive": os.path.abspath(os.sep),
        "label": None,
        "used_percent": round((used / usage.total) * 100, 1) if usage.total else None,
        "free_gb": _bytes_to_gb(usage.free),
        "total_gb": _bytes_to_gb(usage.total),
        "drive_type": "fixed",
    }


def read_physical_disk_topology() -> list[dict[str, Any]]:
    now = time.monotonic()
    with _physical_disk_topology_cache_lock:
        if (
            _physical_disk_topology_cache_value is not None
            and now < _physical_disk_topology_cache_expires_at
        ):
            return [dict(item) for item in _physical_disk_topology_cache_value]

        stale = (
            [dict(item) for item in _physical_disk_topology_cache_value]
            if _physical_disk_topology_cache_value is not None
            else []
        )
        _start_physical_disk_topology_refresh_locked()
        return stale


def _start_physical_disk_topology_refresh_locked() -> None:
    global _physical_disk_topology_refreshing

    if _physical_disk_topology_refreshing:
        return
    _physical_disk_topology_refreshing = True
    thread = threading.Thread(
        target=_refresh_physical_disk_topology_cache,
        daemon=True,
    )
    thread.start()


def _refresh_physical_disk_topology_cache() -> None:
    global _physical_disk_topology_cache_expires_at
    global _physical_disk_topology_cache_value, _physical_disk_topology_refreshing

    try:
        raw = _read_physical_disk_topology_windows()
        topology = _filter_physical_disk_topology(raw)
        succeeded = True
    except Exception:
        topology = []
        succeeded = False

    with _physical_disk_topology_cache_lock:
        if succeeded:
            _physical_disk_topology_cache_value = [
                dict(item) for item in topology
            ]
        ttl = (
            PHYSICAL_DISK_TOPOLOGY_TTL_SECONDS
            if succeeded
            else PHYSICAL_DISK_TOPOLOGY_FAILURE_TTL_SECONDS
        )
        _physical_disk_topology_cache_expires_at = time.monotonic() + ttl
        _physical_disk_topology_refreshing = False


def _reset_physical_disk_cache_for_tests() -> None:
    global _physical_disk_topology_cache_expires_at
    global _physical_disk_topology_cache_value, _physical_disk_topology_refreshing

    with _physical_disk_topology_cache_lock:
        _physical_disk_topology_cache_value = None
        _physical_disk_topology_cache_expires_at = 0.0
        _physical_disk_topology_refreshing = False
    sampler = globals().get("_PHYSICAL_DISK_RATE_SAMPLER")
    if sampler is not None and hasattr(sampler, "reset"):
        sampler.reset()


def _read_physical_disk_topology_windows() -> list[dict[str, Any]]:
    if os.name != "nt":
        return []
    powershell = shutil.which("powershell.exe") or shutil.which("pwsh.exe")
    if powershell is None:
        raise OSError("PowerShell is unavailable")

    script = r"""
$ErrorActionPreference = 'Stop'
$OutputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$physicalById = @{}
try {
    Get-PhysicalDisk -ErrorAction Stop | ForEach-Object {
        $physicalById["$($_.DeviceId)"] = $_
    }
} catch {}
$result = @(
    Get-CimInstance Win32_DiskDrive -ErrorAction Stop | ForEach-Object {
        $disk = $_
        $volumes = @()
        Get-CimAssociatedInstance -InputObject $disk -Association Win32_DiskDriveToDiskPartition -ErrorAction SilentlyContinue |
            ForEach-Object {
                Get-CimAssociatedInstance -InputObject $_ -Association Win32_LogicalDiskToPartition -ErrorAction SilentlyContinue |
                    ForEach-Object {
                        $volumes += [pscustomobject]@{
                            drive = "$($_.DeviceID)\"
                            label = "$($_.VolumeName)"
                            capacity_bytes = [uint64]$_.Size
                            free_bytes = [uint64]$_.FreeSpace
                            drive_type = [int]$_.DriveType
                        }
                    }
            }
        $physical = $physicalById["$($disk.Index)"]
        [pscustomobject]@{
            device_id = "$($disk.Index)"
            model = "$($disk.Model)"
            bus_type = if ($null -ne $physical) { "$($physical.BusType)" } else { "$($disk.InterfaceType)" }
            interface_type = "$($disk.InterfaceType)"
            media_type = if ($null -ne $physical) { "$($physical.MediaType)" } else { "$($disk.MediaType)" }
            capacity_bytes = [uint64]$disk.Size
            pnp_device_id = "$($disk.PNPDeviceID)"
            volumes = @($volumes)
        }
    }
)
ConvertTo-Json -InputObject @($result) -Depth 6 -Compress
"""
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    result = subprocess.run(
        [powershell, "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=10.0,
        creationflags=creationflags,
        check=False,
    )
    if result.returncode != 0:
        raise OSError(_empty_to_none(result.stderr) or "physical disk query failed")
    text = result.stdout.lstrip("\ufeff").strip()
    if not text:
        return []
    payload = json.loads(text)
    if isinstance(payload, dict):
        payload = [payload]
    if not isinstance(payload, list):
        raise ValueError("physical disk query did not return a list")
    return [item for item in payload if isinstance(item, dict)]


def _filter_physical_disk_topology(
    raw_disks: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    filtered: list[dict[str, Any]] = []
    for raw in raw_disks:
        if not isinstance(raw, dict):
            continue
        capacity_bytes = _parse_counter_int(raw.get("capacity_bytes")) or 0
        raw_volumes = raw.get("volumes")
        volumes = raw_volumes if isinstance(raw_volumes, list) else []
        if _physical_disk_is_excluded(raw, volumes, capacity_bytes):
            continue

        normalized_volumes: list[dict[str, Any]] = []
        for volume in volumes:
            if not isinstance(volume, dict):
                continue
            drive = _normalize_volume_drive(volume.get("drive"))
            volume_capacity = _parse_counter_int(volume.get("capacity_bytes"))
            free_bytes = _parse_counter_int(volume.get("free_bytes"))
            normalized_volumes.append(
                {
                    "drive": drive,
                    "label": _empty_to_none(volume.get("label")),
                    "capacity_bytes": volume_capacity,
                    "free_bytes": free_bytes,
                }
            )

        measured_capacity = sum(
            int(volume["capacity_bytes"])
            for volume in normalized_volumes
            if volume["capacity_bytes"] is not None
        )
        measured_free = sum(
            int(volume["free_bytes"])
            for volume in normalized_volumes
            if volume["free_bytes"] is not None
        )
        used_percent = (
            round(((measured_capacity - measured_free) / measured_capacity) * 100, 1)
            if measured_capacity > 0 and measured_free <= measured_capacity
            else None
        )
        volume_drives = sorted(
            {
                str(volume["drive"])
                for volume in normalized_volumes
                if volume["drive"] is not None
            }
        )
        filtered.append(
            {
                "device_id": _empty_to_none(raw.get("device_id")) or str(len(filtered)),
                "model": _empty_to_none(raw.get("model")),
                "bus_type": _empty_to_none(raw.get("bus_type")),
                "media_type": _empty_to_none(raw.get("media_type")),
                "volume_drives": volume_drives,
                "capacity_gb": _bytes_to_gb(capacity_bytes) if capacity_bytes > 0 else None,
                "used_percent": used_percent,
                "free_gb": _bytes_to_gb(measured_free) if measured_capacity > 0 else None,
            }
        )
    return sorted(filtered, key=lambda item: _device_id_sort_key(item.get("device_id")))


def _physical_disk_is_excluded(
    raw: dict[str, Any],
    volumes: list[Any],
    capacity_bytes: int,
) -> bool:
    labels = [
        (_empty_to_none(volume.get("label")) or "").casefold()
        for volume in volumes
        if isinstance(volume, dict)
    ]
    if "recover" in labels:
        return True

    identity = " ".join(
        _empty_to_none(raw.get(key)) or ""
        for key in (
            "model",
            "bus_type",
            "interface_type",
            "media_type",
            "pnp_device_id",
        )
    ).casefold()
    virtual_markers = (
        "ramdisk",
        "ram disk",
        "romex",
        "virtual disk",
        "file backed virtual",
    )
    if any(marker in identity for marker in virtual_markers) or "devdrive" in labels:
        return True

    removable = (
        "usb" in identity
        or any(
            _parse_counter_int(volume.get("drive_type")) == 2
            or (_empty_to_none(volume.get("drive_type")) or "").casefold() == "removable"
            for volume in volumes
            if isinstance(volume, dict)
        )
    )
    return removable and 0 < capacity_bytes < SMALL_REMOVABLE_DISK_BYTES


def _normalize_volume_drive(value: Any) -> str | None:
    text = _empty_to_none(value)
    if text is None:
        return None
    if re.fullmatch(r"[A-Za-z]:", text):
        text += "\\"
    return text[0].upper() + text[1:] if re.match(r"^[A-Za-z]:", text) else text


def _device_id_sort_key(value: Any) -> tuple[int, str]:
    text = _empty_to_none(value) or ""
    parsed = _parse_counter_int(text)
    return (parsed if parsed is not None else 2**31 - 1, text)


class PhysicalDiskRateSampler:
    def __init__(
        self,
        read_counters: Callable[[], Any | None] | None = None,
        now: Callable[[], float] | None = None,
    ):
        self._read_counters = read_counters or _read_disk_io_counters_psutil
        self._now = now or time.monotonic
        self._last: dict[str, tuple[float, int, int, int]] = {}

    def reset(self) -> None:
        self._last = {}

    def sample(
        self,
        topology: list[dict[str, Any]],
    ) -> dict[str, dict[str, Any]]:
        try:
            counters = self._read_counters()
        except Exception:
            counters = None
        timestamp = self._now()
        counter_map = counters if isinstance(counters, dict) else {}
        results: dict[str, dict[str, Any]] = {}
        next_last: dict[str, tuple[float, int, int, int]] = {}

        for disk in topology:
            device_id = _empty_to_none(disk.get("device_id"))
            if device_id is None:
                continue
            counter = _physical_disk_counter(counter_map, device_id)
            if counter is None:
                results[device_id] = {
                    "read_bytes_per_second": None,
                    "write_bytes_per_second": None,
                    "activity_percent": None,
                    "source": "fallback",
                    "status": "unavailable",
                }
                continue

            read_bytes = _parse_counter_int(getattr(counter, "read_bytes", None))
            write_bytes = _parse_counter_int(getattr(counter, "write_bytes", None))
            busy_time = _physical_disk_busy_time_ms(counter)
            if read_bytes is None or write_bytes is None or busy_time is None:
                results[device_id] = {
                    "read_bytes_per_second": None,
                    "write_bytes_per_second": None,
                    "activity_percent": None,
                    "source": "fallback",
                    "status": "unavailable",
                }
                continue

            current = (timestamp, read_bytes, write_bytes, busy_time)
            next_last[device_id] = current
            previous = self._last.get(device_id)
            read_rate = None
            write_rate = None
            activity = None
            status = "warming"
            if previous is not None:
                elapsed = timestamp - previous[0]
                read_delta = read_bytes - previous[1]
                write_delta = write_bytes - previous[2]
                busy_delta = busy_time - previous[3]
                if elapsed > 0 and min(read_delta, write_delta, busy_delta) >= 0:
                    read_rate = round(read_delta / elapsed, 1)
                    write_rate = round(write_delta / elapsed, 1)
                    activity = round(
                        min(100.0, max(0.0, busy_delta / (elapsed * 10.0))),
                        1,
                    )
                    status = (
                        "active"
                        if read_rate > 0 or write_rate > 0 or activity > 0
                        else "idle"
                    )
            results[device_id] = {
                "read_bytes_per_second": read_rate,
                "write_bytes_per_second": write_rate,
                "activity_percent": activity,
                "source": "psutil",
                "status": status,
            }

        self._last = next_last
        return results


def _read_disk_io_counters_psutil() -> dict[str, Any] | None:
    psutil = _optional_import("psutil")
    if psutil is None:
        return None
    try:
        return psutil.disk_io_counters(perdisk=True)
    except Exception:
        return None


def _physical_disk_counter(counters: dict[str, Any], device_id: str) -> Any | None:
    expected = f"physicaldrive{device_id}".casefold()
    for name, counter in counters.items():
        normalized = str(name).replace("\\", "").casefold()
        if normalized == expected:
            return counter
    return None


def _physical_disk_busy_time_ms(counter: Any) -> int | None:
    busy_time = _parse_counter_int(getattr(counter, "busy_time", None))
    if busy_time is not None:
        return busy_time
    read_time = _parse_counter_int(getattr(counter, "read_time", None))
    write_time = _parse_counter_int(getattr(counter, "write_time", None))
    if read_time is None or write_time is None:
        return None
    return read_time + write_time


_PHYSICAL_DISK_RATE_SAMPLER = PhysicalDiskRateSampler()


def read_physical_disks() -> list[dict[str, Any]]:
    topology = read_physical_disk_topology()
    if not topology:
        return []
    rates = _PHYSICAL_DISK_RATE_SAMPLER.sample(topology)
    lhm = read_lhm_sensor_snapshot()
    sensor_matches = _match_lhm_disk_sensors(
        topology,
        lhm.get("disk_sensors") if isinstance(lhm.get("disk_sensors"), list) else [],
    )
    lhm_source = (
        "lhm_stale" if lhm.get("status") == "stale" else "lhm"
    )

    result: list[dict[str, Any]] = []
    for disk in topology:
        device_id = str(disk.get("device_id"))
        rate = rates.get(device_id, {})
        sensor = sensor_matches.get(device_id, {})
        activity = (
            sensor.get("activity_percent")
            if sensor.get("activity_percent") is not None
            else rate.get("activity_percent")
        )
        status = _empty_to_none(rate.get("status")) or "unavailable"
        if sensor.get("activity_percent") is not None:
            status = "active" if float(sensor["activity_percent"]) > 0.1 else "idle"
        source = "win32_cim"
        if rate.get("source") == "psutil":
            source = _append_source(source, "psutil")
        if sensor:
            source = _append_source(source, lhm_source)
        result.append(
            {
                "device_id": device_id,
                "model": disk.get("model"),
                "bus_type": disk.get("bus_type"),
                "media_type": disk.get("media_type"),
                "volume_drives": list(disk.get("volume_drives") or []),
                "capacity_gb": disk.get("capacity_gb"),
                "used_percent": disk.get("used_percent"),
                "free_gb": disk.get("free_gb"),
                "read_bytes_per_second": rate.get("read_bytes_per_second"),
                "write_bytes_per_second": rate.get("write_bytes_per_second"),
                "activity_percent": activity,
                "temperature_celsius": sensor.get("temperature_celsius"),
                "source": source,
                "status": status,
            }
        )
    return sorted(result, key=_physical_disk_display_sort_key)


def _physical_disk_display_sort_key(disk: dict[str, Any]) -> tuple[int, tuple[int, str]]:
    drives = {
        str(drive).upper().rstrip("\\") + ":"
        if re.fullmatch(r"[A-Za-z]", str(drive).strip())
        else str(drive).upper().rstrip("\\")
        for drive in disk.get("volume_drives") or []
    }
    bus_type = (_empty_to_none(disk.get("bus_type")) or "").casefold()
    media_type = (_empty_to_none(disk.get("media_type")) or "").casefold()
    if "C:" in drives:
        priority = 0
    elif "usb" in bus_type or "removable" in media_type:
        priority = 3
    elif "nvme" in bus_type:
        priority = 1
    elif "hdd" in media_type or "sata" in bus_type:
        priority = 2
    else:
        priority = 4
    return (priority, _device_id_sort_key(disk.get("device_id")))


def _match_lhm_disk_sensors(
    topology: list[dict[str, Any]],
    sensors: list[dict[str, Any]],
) -> dict[str, dict[str, Any]]:
    matches: dict[str, dict[str, Any]] = {}
    used: set[int] = set()

    for disk in topology:
        device_id = str(disk.get("device_id"))
        model = _normalize_disk_model(disk.get("model"))
        for index, sensor in enumerate(sensors):
            if index in used or not isinstance(sensor, dict):
                continue
            sensor_model = _normalize_disk_model(sensor.get("model"))
            if model and sensor_model and (
                model == sensor_model
                or (len(model) >= 6 and model in sensor_model)
                or (len(sensor_model) >= 6 and sensor_model in model)
            ):
                matches[device_id] = sensor
                used.add(index)
                break

    for disk in topology:
        device_id = str(disk.get("device_id"))
        if device_id in matches:
            continue
        capacity = _parse_float(disk.get("capacity_gb"))
        if capacity is None or capacity <= 0:
            continue
        candidates: list[tuple[float, int, dict[str, Any]]] = []
        for index, sensor in enumerate(sensors):
            if index in used or not isinstance(sensor, dict):
                continue
            sensor_capacity = _parse_float(sensor.get("total_space_gb"))
            if sensor_capacity is None or sensor_capacity <= 0:
                continue
            difference = abs(sensor_capacity - capacity) / max(sensor_capacity, capacity)
            if difference <= 0.12:
                candidates.append((difference, index, sensor))
        if len(candidates) == 1:
            _, index, sensor = candidates[0]
            matches[device_id] = sensor
            used.add(index)
    return matches


def _normalize_disk_model(value: Any) -> str:
    text = (_empty_to_none(value) or "").casefold()
    return re.sub(r"[^a-z0-9\u4e00-\u9fff]+", "", text)


class NetworkRateSampler:
    def __init__(
        self,
        read_counters: Callable[[], Any | None] | None = None,
        now: Callable[[], float] | None = None,
        latency_sampler: Any | None = None,
    ):
        self._read_counters = read_counters or _read_network_counters_psutil
        self._now = now or time.monotonic
        self._latency_sampler = latency_sampler or NetworkLatencySampler()
        self._last: tuple[float, int, int] | None = None

    def sample(self) -> dict[str, Any]:
        current = self._safe_read_counters()
        timestamp = self._now()
        rx_rate = None
        tx_rate = None
        latency = self._latency_sampler.sample()

        if current is not None:
            rx_total = _parse_counter_int(getattr(current, "bytes_recv", None))
            tx_total = _parse_counter_int(getattr(current, "bytes_sent", None))
            if rx_total is not None and tx_total is not None:
                if self._last is not None:
                    previous_time, previous_rx, previous_tx = self._last
                    elapsed = timestamp - previous_time
                    rx_delta = rx_total - previous_rx
                    tx_delta = tx_total - previous_tx
                    if elapsed > 0 and rx_delta >= 0 and tx_delta >= 0:
                        rx_rate = round(rx_delta / elapsed, 1)
                        tx_rate = round(tx_delta / elapsed, 1)
                self._last = (timestamp, rx_total, tx_total)

        source_parts = []
        if current is not None:
            source_parts.append("psutil")
        if latency.get("source") == "ping":
            source_parts.append("ping")

        return {
            "rx_bytes_per_sec": rx_rate,
            "tx_bytes_per_sec": tx_rate,
            "download_bytes_per_second": rx_rate,
            "upload_bytes_per_second": tx_rate,
            "ping_ms": latency.get("ping_ms"),
            "jitter_ms": latency.get("jitter_ms"),
            "packet_loss_percent": latency.get("packet_loss_percent"),
            "latency_status": latency.get("status"),
            "addresses": _local_addresses(),
            "source": "+".join(source_parts) if source_parts else "fallback",
        }

    def _safe_read_counters(self) -> Any | None:
        try:
            return self._read_counters()
        except Exception:
            return None


def _read_network_counters_psutil() -> Any | None:
    psutil = _optional_import("psutil")
    if psutil is None:
        return None
    try:
        return psutil.net_io_counters()
    except Exception:
        return None


def _parse_counter_int(value: Any) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


class NetworkLatencySampler:
    def __init__(
        self,
        read_ping_ms: Callable[[], float | None] | None = None,
        now: Callable[[], float] | None = None,
        ttl_seconds: float = NETWORK_LATENCY_TTL_SECONDS,
    ):
        self._read_ping_ms = read_ping_ms or _read_ping_ms
        self._now = now or time.monotonic
        self._ttl_seconds = ttl_seconds
        self._last_result: dict[str, Any] | None = None
        self._last_expires_at = 0.0
        self._history: deque[float] = deque(maxlen=15)
        self._outcome_history: deque[bool] = deque(maxlen=15)
        self._lock = threading.Lock()
        self._refreshing = False
        self._refresh_thread: threading.Thread | None = None

    def sample(self) -> dict[str, Any]:
        now = self._now()
        with self._lock:
            if self._last_result is not None and now < self._last_expires_at:
                return dict(self._last_result)

            if not self._refreshing:
                self._start_refresh_locked()

            if self._last_result is None:
                return {
                    "ping_ms": None,
                    "jitter_ms": None,
                    "packet_loss_percent": None,
                    "source": "ping",
                    "status": "connecting",
                }
            stale = dict(self._last_result)
            stale["status"] = "stale"
            return stale

    def _start_refresh_locked(self) -> None:
        self._refreshing = True
        self._refresh_thread = threading.Thread(target=self._refresh, daemon=True)
        self._refresh_thread.start()

    def _refresh(self) -> None:
        try:
            ping_ms = _round_float_or_none(self._read_ping_ms())
        except Exception:
            ping_ms = None
        completed_at = self._now()
        with self._lock:
            succeeded = ping_ms is not None
            self._outcome_history.append(succeeded)
            if ping_ms is not None:
                self._history.append(ping_ms)
            failures = sum(1 for outcome in self._outcome_history if not outcome)
            packet_loss = round(
                failures / len(self._outcome_history) * 100.0,
                1,
            )
            self._last_result = {
                "ping_ms": ping_ms,
                "jitter_ms": (
                    _network_jitter(self._history)
                    if self._history
                    else None
                ),
                "packet_loss_percent": packet_loss,
                "source": "ping",
                "status": "live" if succeeded else "unavailable",
            }
            self._last_expires_at = completed_at + self._ttl_seconds
            self._refreshing = False


def _network_jitter(history: deque[float]) -> float:
    values = list(history)
    if len(values) < 2:
        return 0.0
    diffs = [abs(values[index] - values[index - 1]) for index in range(1, len(values))]
    return round(sum(diffs) / len(diffs), 1)


def _read_ping_ms() -> float | None:
    target = _network_ping_target()
    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    try:
        result = subprocess.run(
            ["ping", "-n", "1", "-w", "1000", target],
            capture_output=True,
            text=True,
            encoding="mbcs" if os.name == "nt" else "utf-8",
            errors="replace",
            timeout=1.5,
            creationflags=creationflags,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        return None
    if result.returncode != 0:
        return None
    return _parse_ping_ms(result.stdout)


def _parse_ping_ms(output: str) -> float | None:
    match = re.search(r"(?:time|时间)\s*[=<]\s*([0-9]+(?:\.[0-9]+)?)\s*ms", output, re.IGNORECASE)
    if match:
        return _round_float_or_none(match.group(1))
    match = re.search(r"(?:Average|平均)\s*=\s*([0-9]+(?:\.[0-9]+)?)\s*ms", output, re.IGNORECASE)
    if match:
        return _round_float_or_none(match.group(1))
    return None


def _network_ping_target() -> str:
    config = _load_config()
    network = config.get("network")
    if isinstance(network, dict):
        target = _empty_to_none(network.get("pingHost"))
        if target is not None:
            return target
    return NETWORK_PING_TARGET


_NETWORK_SAMPLER = NetworkRateSampler()


def read_network_snapshot() -> dict[str, Any]:
    return _NETWORK_SAMPLER.sample()


def _local_addresses() -> list[str]:
    addresses: set[str] = {"127.0.0.1"}

    # Avoid hostname/FQDN DNS lookups here; they can block the /snapshot endpoint
    # for several seconds on some Windows networks.
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            probe.settimeout(0.05)
            probe.connect(("8.8.8.8", 80))
            addresses.add(str(probe.getsockname()[0]))
    except OSError:
        pass

    return sorted(addresses)


class ProcessActivitySampler:
    def __init__(
        self,
        process_iter: Callable[..., Any] | None = None,
        now: Callable[[], float] | None = None,
        cpu_count: int | None = None,
    ):
        self._process_iter = process_iter
        self._now = now or time.monotonic
        self._cpu_count = max(int(cpu_count or os.cpu_count() or 1), 1)
        self._last_cpu: dict[tuple[int, float | None], tuple[float, float]] = {}

    def sample(self, limit: int = 5) -> list[dict[str, Any]]:
        process_iter = self._process_iter or _psutil_process_iter
        timestamp = self._now()
        current_cache: dict[tuple[int, float | None], tuple[float, float]] = {}
        processes: list[dict[str, Any]] = []

        try:
            iterator = process_iter(
                attrs=["pid", "name", "create_time", "cpu_times", "memory_info"]
            )
        except TypeError:
            iterator = process_iter()
        except Exception:
            return []

        for process in iterator or []:
            info = getattr(process, "info", None)
            if not isinstance(info, dict):
                continue

            pid = _parse_int(info.get("pid"))
            name = _empty_to_none(info.get("name"))
            if pid is None or name is None:
                continue
            if _is_ignored_process(pid, name):
                continue

            create_time = _round_float2_or_none(info.get("create_time"))
            cpu_seconds = _process_cpu_seconds(info.get("cpu_times"))
            memory_mb = _memory_info_mb(info.get("memory_info"))
            cpu_percent = None
            key = (pid, create_time)
            if cpu_seconds is not None:
                previous = self._last_cpu.get(key)
                if previous is not None:
                    previous_time, previous_cpu_seconds = previous
                    elapsed = timestamp - previous_time
                    delta = cpu_seconds - previous_cpu_seconds
                    if elapsed > 0 and delta >= 0:
                        cpu_percent = round(
                            min(max((delta / elapsed) * 100.0 / self._cpu_count, 0.0), 100.0),
                            1,
                        )
                current_cache[key] = (timestamp, cpu_seconds)

            processes.append(
                {
                    "name": name,
                    "description": None,
                    "pid": pid,
                    "cpu_percent": cpu_percent,
                    "gpu_percent": None,
                    "memory_mb": memory_mb,
                    "memory_gb": round(memory_mb / 1024.0, 2) if memory_mb is not None else None,
                    "source": "psutil",
                }
            )

        processes = _aggregate_processes_by_name(processes)
        self._last_cpu = current_cache
        if any((item.get("cpu_percent") or 0) > 0 for item in processes):
            sort_key = lambda item: item["cpu_percent"] if item["cpu_percent"] is not None else -1
        else:
            sort_key = lambda item: item["memory_mb"] if item["memory_mb"] is not None else -1
        return sorted(processes, key=sort_key, reverse=True)[:limit]


def _psutil_process_iter(attrs: list[str]) -> Any:
    psutil = _optional_import("psutil")
    if psutil is None:
        return []
    try:
        return psutil.process_iter(attrs=attrs)
    except Exception:
        return []


def _process_cpu_seconds(cpu_times: Any) -> float | None:
    user = _parse_float(getattr(cpu_times, "user", None))
    system = _parse_float(getattr(cpu_times, "system", None))
    if user is None and system is None:
        return None
    return float(user or 0.0) + float(system or 0.0)


def _memory_info_mb(memory_info: Any) -> float | None:
    rss = _parse_counter_int(getattr(memory_info, "rss", None))
    return round(rss / (1024.0 * 1024.0), 1) if rss is not None else None


def _aggregate_processes_by_name(processes: list[dict[str, Any]]) -> list[dict[str, Any]]:
    grouped: dict[str, dict[str, Any]] = {}
    for process in processes:
        name = _empty_to_none(process.get("name"))
        if name is None:
            continue
        key = name.lower()
        group = grouped.get(key)
        if group is None:
            group = {
                "name": name,
                "description": process.get("description"),
                "pid": process.get("pid"),
                "cpu_percent": None,
                "gpu_percent": None,
                "memory_mb": None,
                "memory_gb": None,
                "source": process.get("source"),
            }
            grouped[key] = group

        if process.get("cpu_percent") is not None:
            group["cpu_percent"] = round(
                (group["cpu_percent"] or 0.0) + float(process["cpu_percent"]),
                1,
            )
        if process.get("gpu_percent") is not None:
            group["gpu_percent"] = round(
                (group["gpu_percent"] or 0.0) + float(process["gpu_percent"]),
                1,
            )
        if process.get("memory_mb") is not None:
            group["memory_mb"] = round(
                (group["memory_mb"] or 0.0) + float(process["memory_mb"]),
                1,
            )
            group["memory_gb"] = round(group["memory_mb"] / 1024.0, 2)

    return list(grouped.values())


_PROCESS_SAMPLER = ProcessActivitySampler()


def read_top_processes(limit: int = 5) -> list[dict[str, Any]]:
    return _read_top_processes_helper_cache(limit)


def _read_top_processes_helper_cache(limit: int = 5) -> list[dict[str, Any]]:
    try:
        with open(TOP_PROCESSES_CACHE_PATH, "r", encoding="utf-8") as handle:
            payload = json.load(handle)
    except (OSError, json.JSONDecodeError):
        return []

    if not isinstance(payload, dict) or payload.get("schema_version") != 1:
        return []

    generated_at = _parse_float(payload.get("generated_at_unix_ms"))
    if generated_at is None:
        return []
    age_seconds = (time.time() * 1000 - generated_at) / 1000.0
    if age_seconds < 0 or age_seconds > TOP_PROCESSES_HELPER_MAX_AGE_SECONDS:
        return []

    processes = payload.get("processes")
    if not isinstance(processes, list):
        return []

    normalized: list[dict[str, Any]] = []
    for process in processes:
        if not isinstance(process, dict):
            continue
        name = _empty_to_none(process.get("name"))
        if name is None:
            continue
        memory_mb = _round_float_or_none(process.get("memory_mb"))
        normalized.append(
            {
                "name": name,
                "description": process.get("description"),
                "pid": _parse_int(process.get("pid")),
                "cpu_percent": _round_float_or_none(process.get("cpu_percent")),
                "gpu_percent": _round_float_or_none(process.get("gpu_percent")),
                "memory_mb": memory_mb,
                "memory_gb": _round_float2_or_none(process.get("memory_gb"))
                if process.get("memory_gb") is not None
                else (round(memory_mb / 1024.0, 2) if memory_mb is not None else None),
                "source": _empty_to_none(process.get("source")) or "top_processes_helper",
            }
        )

    return normalized[:limit]


def _start_top_processes_refresh_locked(limit: int) -> None:
    global _top_processes_refreshing

    if _top_processes_refreshing:
        return
    _top_processes_refreshing = True
    thread = threading.Thread(target=_refresh_top_processes_cache, args=(limit,), daemon=True)
    thread.start()


def _refresh_top_processes_cache(limit: int = 5) -> None:
    global _top_processes_cache_expires_at, _top_processes_cache_limit, _top_processes_cache_value, _top_processes_refreshing

    try:
        psutil_processes = _PROCESS_SAMPLER.sample(limit)
        processes = psutil_processes if psutil_processes else _read_top_processes_tasklist(limit)
    except Exception:
        processes = []

    with _top_processes_cache_lock:
        _top_processes_cache_value = [dict(process) for process in processes]
        _top_processes_cache_limit = limit
        _top_processes_cache_expires_at = time.monotonic() + TOP_PROCESSES_CACHE_TTL_SECONDS
        _top_processes_refreshing = False


def _reset_top_processes_cache_for_tests() -> None:
    global _top_processes_cache_expires_at, _top_processes_cache_limit, _top_processes_cache_value, _top_processes_refreshing

    with _top_processes_cache_lock:
        _top_processes_cache_value = None
        _top_processes_cache_expires_at = 0.0
        _top_processes_cache_limit = 0
        _top_processes_refreshing = False


def _read_top_processes_tasklist(limit: int = 5) -> list[dict[str, Any]]:
    if os.name != "nt":
        return []

    creationflags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    try:
        result = subprocess.run(
            ["tasklist", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=2,
            creationflags=creationflags,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired):
        return []

    if result.returncode != 0:
        return []

    processes: list[dict[str, Any]] = []
    for row in csv.reader(result.stdout.splitlines()):
        if len(row) < 5:
            continue
        memory_kb = _parse_memory_kb(row[4])
        pid = _parse_int(row[1])
        name = row[0]
        if pid is not None and _is_ignored_process(pid, name):
            continue
        processes.append(
            {
                "name": name,
                "pid": pid,
                "cpu_percent": None,
                "gpu_percent": None,
                "memory_mb": round(memory_kb / 1024, 1) if memory_kb is not None else None,
            }
        )

    return sorted(
        processes,
        key=lambda item: item["memory_mb"] if item["memory_mb"] is not None else -1,
        reverse=True,
    )[:limit]


def _parse_memory_kb(value: str) -> int | None:
    digits = "".join(ch for ch in value if ch.isdigit())
    return int(digits) if digits else None


def _is_ignored_process(pid: int, name: str) -> bool:
    normalized = name.strip().lower()
    return pid == 0 or normalized in {"system idle process", "idle"}


def _parse_int(value: str) -> int | None:
    try:
        return int(value)
    except ValueError:
        return None


def _bytes_to_gb(value: int) -> float:
    return round(value / (1024**3), 2)


def make_handler(snapshot_provider: Callable[[], dict[str, Any]]) -> type[BaseHTTPRequestHandler]:
    class SnapshotHandler(BaseHTTPRequestHandler):
        server_version = "TURZXMetricsAgent/1.0"

        def do_GET(self) -> None:
            if urlsplit(self.path).path != "/snapshot":
                self.send_error(404, "Not Found")
                return

            try:
                payload = snapshot_provider()
            except Exception as exc:
                payload = empty_snapshot()
                now = utc_now_iso()
                payload["time"] = now
                payload["health"] = {
                    "status": "degraded",
                    "generated_at": now,
                    "errors": [
                        {
                            "component": "snapshot",
                            "error": f"{type(exc).__name__}: {exc}",
                        }
                    ],
                }

            body = json.dumps(
                payload,
                ensure_ascii=False,
                separators=(",", ":"),
            ).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Cache-Control", "no-store")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def log_message(self, format: str, *args: Any) -> None:
            return

    return SnapshotHandler


def create_server(
    host: str = DEFAULT_HOST,
    port: int = DEFAULT_PORT,
    snapshot_provider: Callable[[], dict[str, Any]] | None = None,
) -> ThreadingHTTPServer:
    provider = snapshot_provider or build_snapshot
    return ThreadingHTTPServer((host, port), make_handler(provider))


def run_server(host: str = DEFAULT_HOST, port: int = DEFAULT_PORT) -> None:
    server = create_server(host, port)
    print(f"TURZX metrics agent listening on http://{host}:{port}/snapshot")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="TURZX side screen metrics agent")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    args = parser.parse_args(argv)

    run_server(args.host, args.port)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
