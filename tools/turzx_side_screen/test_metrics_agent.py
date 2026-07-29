import json
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent))

import metrics_agent


REQUIRED_SNAPSHOT_KEYS = {
    "schema_version",
    "timestamp_unix_ms",
    "sequence",
    "time",
    "weather",
    "alert",
    "foreground_app",
    "cpu",
    "gpu",
    "fps",
    "memory",
    "disks",
    "physical_disks",
    "network",
    "top_processes",
    "health",
    "trust",
}


class MetricsAgentTests(unittest.TestCase):
    def setUp(self):
        trust_log_patcher = patch.object(metrics_agent, "_maybe_write_data_trust_log")
        trust_log_patcher.start()
        self.addCleanup(trust_log_patcher.stop)
        if hasattr(metrics_agent, "_reset_gpu_cache_for_tests"):
            metrics_agent._reset_gpu_cache_for_tests()
        if hasattr(metrics_agent, "_reset_lhm_cache_for_tests"):
            metrics_agent._reset_lhm_cache_for_tests()
        if hasattr(metrics_agent, "_reset_physical_disk_cache_for_tests"):
            metrics_agent._reset_physical_disk_cache_for_tests()
        if hasattr(metrics_agent, "_reset_weather_cache_for_tests"):
            metrics_agent._reset_weather_cache_for_tests()

    def test_build_snapshot_has_stable_schema(self):
        fake_disk = {
            "drive": "C:\\",
            "label": "Windows",
            "used_percent": 50.0,
            "free_gb": 10.0,
            "total_gb": 20.0,
            "drive_type": "fixed",
        }

        with (
            patch.object(
                metrics_agent,
                "read_weather_snapshot",
                return_value=metrics_agent.empty_snapshot()["weather"],
            ),
            patch.object(metrics_agent, "enumerate_disks", return_value=[fake_disk]),
            patch.object(metrics_agent, "read_physical_disks", return_value=[]),
            patch.object(metrics_agent, "read_top_processes", return_value=[]),
            patch.object(
                metrics_agent,
                "read_network_snapshot",
                return_value=metrics_agent.empty_snapshot()["network"],
            ),
            patch.object(
                metrics_agent,
                "read_foreground_app",
                return_value=metrics_agent.empty_snapshot()["foreground_app"],
            ),
        ):
            snapshot = metrics_agent.build_snapshot()

        self.assertEqual(REQUIRED_SNAPSHOT_KEYS, set(snapshot.keys()))
        self.assertEqual(1, snapshot["schema_version"])
        self.assertIsInstance(snapshot["timestamp_unix_ms"], int)
        self.assertIsInstance(snapshot["sequence"], int)
        self.assertIsInstance(snapshot["disks"], list)
        self.assertIsInstance(snapshot["top_processes"], list)
        self.assertIsInstance(snapshot["health"], dict)
        self.assertIn("status", snapshot["health"])
        self.assertIsInstance(snapshot["trust"], dict)
        self.assertIn("score", snapshot["trust"])
        self.assertIn("items", snapshot["trust"])

    def test_data_trust_scores_missing_fallback_sources_lower(self):
        snapshot = metrics_agent.empty_snapshot()
        snapshot["weather"]["source"] = "fallback"
        snapshot["weather"]["temperature_celsius"] = None
        snapshot["cpu"]["source"] = "fallback"
        snapshot["cpu"]["usage_percent"] = None
        snapshot["gpu"]["source"] = "fallback"
        snapshot["gpu"]["usage_percent"] = None
        snapshot["network"]["download_bytes_per_second"] = 1024
        snapshot["network"]["upload_bytes_per_second"] = 2048
        snapshot["network"]["ping_ms"] = None
        snapshot["top_processes"] = []
        snapshot["health"]["errors"] = [{"component": "gpu", "error": "probe failed"}]

        trust = metrics_agent.build_trust_snapshot(snapshot)

        self.assertLess(trust["score"], 75)
        self.assertEqual("warn", trust["level"])
        self.assertGreaterEqual(trust["missing_count"], 3)
        self.assertIn(trust["worst_component"], {"cpu", "gpu", "weather", "apps"})

    def test_data_trust_scores_live_sources_higher(self):
        snapshot = metrics_agent.empty_snapshot()
        snapshot["weather"].update(
            {
                "source": "weather_shim",
                "city": "田家庵",
                "temperature_celsius": 31,
                "condition": "晴",
                "updated_at": "2026-07-05T08:20:00+08:00",
            }
        )
        snapshot["cpu"].update(
            {
                "source": "windows_api+lhm",
                "usage_percent": 37.0,
                "temperature_celsius": 63.0,
                "power_watts": 145.0,
                "clock_mhz": 5557.0,
                "core_voltage": 1.27,
            }
        )
        snapshot["gpu"].update(
            {
                "source": "nvml+lhm",
                "usage_percent": 18.0,
                "temperature_celsius": 54.0,
                "power_watts": 154.0,
                "core_clock_mhz": 2835.0,
                "memory_clock_mhz": 16401.0,
                "core_voltage": 1.0,
            }
        )
        snapshot["fps"].update(
            {
                "source": "presentmon",
                "status": "active",
                "current": 144.0,
                "frame_time_ms": 6.9,
            }
        )
        snapshot["memory"].update(
            {
                "source": "psutil+nvml",
                "ram_usage_percent": 34.0,
                "vram_usage_percent": 14.0,
            }
        )
        snapshot["disks"] = [{"drive": "C:\\", "used_percent": 44.0, "free_gb": 120.0, "total_gb": 512.0}]
        snapshot["physical_disks"] = [
            {
                "device_id": "1",
                "used_percent": 44.0,
                "source": "win32_cim+psutil",
            }
        ]
        snapshot["network"].update(
            {
                "source": "stdlib+ping",
                "download_bytes_per_second": 1024,
                "upload_bytes_per_second": 2048,
                "ping_ms": 18.0,
                "jitter_ms": 2.0,
                "packet_loss_percent": 0.0,
            }
        )
        snapshot["top_processes"] = [{"name": "Typora.exe", "cpu_percent": 8.0, "memory_mb": 512.0}]

        trust = metrics_agent.build_trust_snapshot(snapshot)

        self.assertGreaterEqual(trust["score"], 90)
        self.assertEqual("ok", trust["level"])
        self.assertEqual(0, trust["missing_count"])
        self.assertEqual("ok", trust["items"][0]["status"])

    def test_data_trust_marks_lhm_stale_cpu_gpu_and_disks(self):
        cpu = {
            "source": "win32_getsystemtimes+lhm_stale",
            "usage_percent": 20.0,
            "temperature_celsius": 60.0,
            "power_watts": 100.0,
            "clock_mhz": 1734.0,
            "core_voltage": 1.2,
        }
        gpu = {
            "source": "nvml+lhm_stale",
            "usage_percent": 15.0,
            "temperature_celsius": 50.0,
            "power_watts": 120.0,
            "core_clock_mhz": 2500.0,
            "memory_clock_mhz": 16000.0,
            "core_voltage": 0.95,
        }
        disks = [
            {
                "device_id": "1",
                "used_percent": 50.0,
                "source": "win32_cim+psutil+lhm_stale",
            }
        ]

        items = [
            metrics_agent._trust_cpu(cpu),
            metrics_agent._trust_gpu(gpu),
            metrics_agent._trust_disks(disks),
        ]

        self.assertEqual(["stale", "stale", "stale"], [item["status"] for item in items])
        self.assertEqual([85, 85, 85], [item["score"] for item in items])

    def test_data_trust_log_writes_jsonl_summary(self):
        trust = {
            "score": 82,
            "level": "warn",
            "worst_component": "fps",
            "missing_count": 1,
            "fallback_count": 0,
            "items": [{"component": "fps", "status": "idle", "score": 70}],
        }

        with tempfile.TemporaryDirectory() as temp_dir:
            log_path = Path(temp_dir) / "data-trust-test.jsonl"
            metrics_agent.write_data_trust_log(log_path, trust, timestamp_unix_ms=123456)

            payload = json.loads(log_path.read_text(encoding="utf-8").strip())
        self.assertEqual(123456, payload["timestamp_unix_ms"])
        self.assertEqual(82, payload["score"])
        self.assertEqual("fps", payload["worst_component"])
        self.assertEqual(1, payload["missing_count"])

    def test_data_trust_log_rotates_before_exceeding_size_cap(self):
        trust = {
            "score": 82,
            "level": "warn",
            "worst_component": "fps",
            "items": [{"component": "fps", "status": "idle", "score": 70}],
        }

        with tempfile.TemporaryDirectory() as temp_dir:
            log_path = Path(temp_dir) / "data-trust.jsonl"
            metrics_agent.write_data_trust_log(
                log_path,
                trust,
                timestamp_unix_ms=1,
                max_bytes=350,
                backup_count=2,
            )
            metrics_agent.write_data_trust_log(
                log_path,
                trust,
                timestamp_unix_ms=2,
                max_bytes=350,
                backup_count=2,
            )
            metrics_agent.write_data_trust_log(
                log_path,
                trust,
                timestamp_unix_ms=3,
                max_bytes=350,
                backup_count=2,
            )

            self.assertTrue(log_path.exists())
            self.assertTrue(Path(f"{log_path}.1").exists())
            self.assertLessEqual(log_path.stat().st_size, 350)
            self.assertLessEqual(Path(f"{log_path}.1").stat().st_size, 350)

    def test_data_trust_log_discards_preexisting_oversized_generation(self):
        trust = {
            "score": 100,
            "level": "ok",
            "items": [],
        }

        with tempfile.TemporaryDirectory() as temp_dir:
            log_path = Path(temp_dir) / "data-trust.jsonl"
            log_path.write_bytes(b"x" * 4096)
            Path(f"{log_path}.1").write_bytes(b"y" * 4096)

            metrics_agent.write_data_trust_log(
                log_path,
                trust,
                timestamp_unix_ms=1,
                max_bytes=350,
                backup_count=2,
            )

            retained = list(Path(temp_dir).glob("data-trust.jsonl*"))
            self.assertTrue(retained)
            self.assertTrue(all(path.stat().st_size <= 350 for path in retained))

    def test_display_time_uses_configured_china_timezone(self):
        value = metrics_agent.display_time_iso_from_utc(
            metrics_agent.dt.datetime(2026, 7, 4, 20, 25, 30, tzinfo=metrics_agent.dt.timezone.utc),
            "Asia/Shanghai",
        )

        self.assertEqual("2026-07-05T04:25:30+08:00", value)

    def test_enumerate_disks_returns_expected_structure(self):
        disks = metrics_agent.enumerate_disks()

        self.assertIsInstance(disks, list)
        self.assertGreaterEqual(len(disks), 1)
        for disk in disks:
            self.assertEqual(
                {
                    "drive",
                    "label",
                    "used_percent",
                    "free_gb",
                    "total_gb",
                    "drive_type",
                },
                set(disk.keys()),
            )
            self.assertRegex(disk["drive"], r"^[A-Z]:\\$")

    def test_physical_disk_topology_filters_to_four_real_disks_and_merges_c_e(self):
        tib = 1024**4
        gib = 1024**3
        raw = [
            {
                "device_id": "0",
                "model": "Hitachi HUS724040ALE641",
                "bus_type": "SATA",
                "media_type": "HDD",
                "capacity_bytes": 4 * tib,
                "volumes": [
                    {"drive": "D:\\", "label": "Data", "capacity_bytes": 4 * tib, "free_bytes": 800 * gib, "drive_type": 3}
                ],
            },
            {
                "device_id": "1",
                "model": "Predator SSD GM7000 4TB",
                "bus_type": "NVMe",
                "media_type": "SSD",
                "capacity_bytes": 4 * tib,
                "volumes": [
                    {"drive": "C:\\", "label": "Windows", "capacity_bytes": 1 * tib, "free_bytes": 100 * gib, "drive_type": 3},
                    {"drive": "E:\\", "label": "Projects", "capacity_bytes": 3 * tib, "free_bytes": 300 * gib, "drive_type": 3},
                ],
            },
            {
                "device_id": "2",
                "model": "XPG GAMMIX S50 Lite",
                "bus_type": "NVMe",
                "media_type": "SSD",
                "capacity_bytes": 2 * tib,
                "volumes": [
                    {"drive": "G:\\", "label": "Backup", "capacity_bytes": 2 * tib, "free_bytes": 1 * tib, "drive_type": 3}
                ],
            },
            {
                "device_id": "3",
                "model": "Romex RAMDISK",
                "bus_type": "RAM",
                "media_type": "RAM Disk",
                "capacity_bytes": 12 * gib,
                "volumes": [
                    {"drive": "Z:\\", "label": "Cache", "capacity_bytes": 12 * gib, "free_bytes": 4 * gib, "drive_type": 6}
                ],
            },
            {
                "device_id": "4",
                "model": "Microsoft Virtual Disk",
                "bus_type": "File Backed Virtual",
                "media_type": "Unspecified",
                "capacity_bytes": 300 * gib,
                "volumes": [
                    {"drive": "V:\\", "label": "DevDrive", "capacity_bytes": 300 * gib, "free_bytes": 100 * gib, "drive_type": 3}
                ],
            },
            {
                "device_id": "5",
                "model": "Lenovo thinkplus 1TB",
                "bus_type": "USB",
                "media_type": "SSD",
                "capacity_bytes": 1 * tib,
                "pnp_device_id": "USBSTOR\\DISK&VEN_LENOVO",
                "volumes": [
                    {"drive": "H:\\", "label": "Cold", "capacity_bytes": 1 * tib, "free_bytes": 700 * gib, "drive_type": 3}
                ],
            },
            {
                "device_id": "6",
                "model": "Kingston DataTraveler",
                "bus_type": "USB",
                "media_type": "Removable",
                "capacity_bytes": 28_800_000_000,
                "pnp_device_id": "USBSTOR\\DISK&VEN_KINGSTON",
                "volumes": [
                    {"drive": "F:\\", "label": "recover", "capacity_bytes": 28_800_000_000, "free_bytes": 10 * gib, "drive_type": 2}
                ],
            },
        ]

        disks = metrics_agent._filter_physical_disk_topology(raw)

        self.assertEqual(["0", "1", "2", "5"], [disk["device_id"] for disk in disks])
        predator = next(disk for disk in disks if disk["device_id"] == "1")
        self.assertEqual(["C:\\", "E:\\"], predator["volume_drives"])

    def test_small_usb_filter_uses_strict_32_billion_byte_boundary(self):
        raw = [
            {
                "device_id": "6",
                "model": "Small USB",
                "bus_type": "USB",
                "media_type": "Removable",
                "capacity_bytes": 31_999_999_999,
                "volumes": [],
            },
            {
                "device_id": "7",
                "model": "Boundary USB",
                "bus_type": "USB",
                "media_type": "Removable",
                "capacity_bytes": 32_000_000_000,
                "volumes": [],
            },
        ]

        disks = metrics_agent._filter_physical_disk_topology(raw)

        self.assertEqual(["7"], [disk["device_id"] for disk in disks])

    def test_physical_disk_rate_sampler_reports_differential_rates(self):
        readings = iter(
            [
                {
                    "PhysicalDrive1": SimpleNamespace(
                        read_bytes=1_000,
                        write_bytes=2_000,
                        read_time=100,
                        write_time=200,
                    )
                },
                {
                    "PhysicalDrive1": SimpleNamespace(
                        read_bytes=2_024,
                        write_bytes=3_024,
                        read_time=200,
                        write_time=250,
                    )
                },
            ]
        )
        timestamps = iter([10.0, 10.5])
        sampler = metrics_agent.PhysicalDiskRateSampler(
            read_counters=lambda: next(readings),
            now=lambda: next(timestamps),
        )
        topology = [{"device_id": "1"}]

        first = sampler.sample(topology)["1"]
        second = sampler.sample(topology)["1"]

        self.assertIsNone(first["read_bytes_per_second"])
        self.assertIsNone(first["write_bytes_per_second"])
        self.assertEqual("warming", first["status"])
        self.assertEqual(2048.0, second["read_bytes_per_second"])
        self.assertEqual(2048.0, second["write_bytes_per_second"])
        self.assertEqual(30.0, second["activity_percent"])
        self.assertEqual("active", second["status"])

    def test_read_physical_disks_combines_topology_rates_and_lhm_temperature(self):
        topology = [
            {
                "device_id": "1",
                "model": "Predator SSD GM7000 4TB",
                "bus_type": "NVMe",
                "media_type": "SSD",
                "volume_drives": ["C:\\", "E:\\"],
                "capacity_gb": 3815.0,
                "used_percent": 90.6,
                "free_gb": 358.0,
            }
        ]
        rates = {
            "1": {
                "read_bytes_per_second": 2_048.0,
                "write_bytes_per_second": 4_096.0,
                "activity_percent": 3.0,
                "source": "psutil",
                "status": "active",
            }
        }
        lhm = {
            "disk_sensors": [
                {
                    "model": "Predator SSD GM7000 4TB",
                    "temperature_celsius": 58.0,
                    "activity_percent": 4.5,
                    "total_space_gb": 4096.8,
                }
            ],
            "source": "lhm",
            "status": "live",
        }

        with (
            patch.object(metrics_agent, "read_physical_disk_topology", return_value=topology),
            patch.object(metrics_agent._PHYSICAL_DISK_RATE_SAMPLER, "sample", return_value=rates),
            patch.object(metrics_agent, "read_lhm_sensor_snapshot", return_value=lhm),
        ):
            disks = metrics_agent.read_physical_disks()

        self.assertEqual(1, len(disks))
        self.assertEqual(
            {
                "device_id",
                "model",
                "bus_type",
                "media_type",
                "volume_drives",
                "capacity_gb",
                "used_percent",
                "free_gb",
                "read_bytes_per_second",
                "write_bytes_per_second",
                "activity_percent",
                "temperature_celsius",
                "source",
                "status",
            },
            set(disks[0]),
        )
        self.assertEqual(58.0, disks[0]["temperature_celsius"])
        self.assertEqual(4.5, disks[0]["activity_percent"])
        self.assertEqual("win32_cim+psutil+lhm", disks[0]["source"])

    def test_physical_disks_are_sorted_for_system_nvme_hdd_usb_display(self):
        disks = [
            {"device_id": "0", "volume_drives": ["D:\\"], "bus_type": "SATA", "media_type": "HDD"},
            {"device_id": "1", "volume_drives": ["C:\\", "E:\\"], "bus_type": "NVMe", "media_type": "SSD"},
            {"device_id": "2", "volume_drives": ["G:\\"], "bus_type": "NVMe", "media_type": "SSD"},
            {"device_id": "5", "volume_drives": ["H:\\"], "bus_type": "USB", "media_type": "SSD"},
        ]

        ordered = sorted(disks, key=metrics_agent._physical_disk_display_sort_key)

        self.assertEqual(["1", "2", "0", "5"], [disk["device_id"] for disk in ordered])

    def test_physical_topology_cold_refresh_is_non_blocking(self):
        release = threading.Event()

        def slow_read():
            release.wait(timeout=1.0)
            return []

        with patch.object(metrics_agent, "_read_physical_disk_topology_windows", side_effect=slow_read):
            started = time.perf_counter()
            disks = metrics_agent.read_physical_disk_topology()
            elapsed = time.perf_counter() - started
            release.set()

        self.assertEqual([], disks)
        self.assertLess(elapsed, 0.1)

    def test_snapshot_handler_returns_json(self):
        expected = metrics_agent.empty_snapshot()
        expected["time"] = "2026-07-04T00:00:00Z"
        expected["disks"] = [
            {
                "drive": "C:\\",
                "label": "Windows",
                "used_percent": 50.0,
                "free_gb": 10.0,
                "total_gb": 20.0,
                "drive_type": "fixed",
            }
        ]

        server = metrics_agent.create_server(
            "127.0.0.1",
            0,
            snapshot_provider=lambda: expected,
        )
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()

        try:
            host, port = server.server_address
            with urllib.request.urlopen(
                f"http://{host}:{port}/snapshot",
                timeout=2,
            ) as response:
                body = response.read()

            payload = json.loads(body.decode("utf-8"))
            self.assertEqual(200, response.status)
            self.assertEqual("application/json", response.headers["Content-Type"])
            self.assertEqual(expected, payload)
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

    def test_local_addresses_does_not_depend_on_dns_lookup(self):
        with patch.object(
            metrics_agent.socket,
            "getaddrinfo",
            side_effect=AssertionError("DNS lookup should not be used"),
        ):
            addresses = metrics_agent._local_addresses()

        self.assertIsInstance(addresses, list)

    def test_weather_snapshot_parses_weather_shim_payload(self):
        payload = {
            "code": "200",
            "updateTime": "2026-07-05T08:20:00+08:00",
            "now": {
                "temp": "31",
                "text": "晴",
                "humidity": "38",
                "aqi": "75",
                "windDir": "北",
                "windScale": "2",
            },
        }

        weather = metrics_agent._weather_from_qweather_payload("北京", payload)

        self.assertEqual("北京", weather["city"])
        self.assertEqual(31.0, weather["temperature_celsius"])
        self.assertEqual("31°C", weather["temperature_text"])
        self.assertEqual("晴", weather["condition"])
        self.assertEqual(75, weather["aqi"])
        self.assertEqual(38.0, weather["humidity_percent"])
        self.assertEqual("北 2级", weather["wind_text"])
        self.assertEqual("weather_shim", weather["source"])

    def test_weather_cold_refresh_is_non_blocking_when_shim_is_slow(self):
        entered = threading.Event()
        release = threading.Event()

        def slow_fetch(url, timeout):
            entered.set()
            release.wait(timeout=1.0)
            return {
                "code": "200",
                "now": {"temp": "31", "text": "晴"},
            }

        with (
            patch.object(
                metrics_agent,
                "_load_config",
                return_value={"weather": {"city": "田家庵"}},
            ),
            patch.object(metrics_agent, "_fetch_json", side_effect=slow_fetch),
        ):
            started = time.perf_counter()
            weather = metrics_agent.read_weather_snapshot()
            elapsed = time.perf_counter() - started
            self.assertTrue(entered.wait(timeout=0.2))
            release.set()
            deadline = time.monotonic() + 1.0
            while metrics_agent._weather_refreshing and time.monotonic() < deadline:
                time.sleep(0.01)

        self.assertEqual("fallback", weather["source"])
        self.assertEqual("connecting", weather["status"])
        self.assertLess(elapsed, 0.1)

    def test_build_snapshot_does_not_wait_for_slow_weather(self):
        entered = threading.Event()
        release = threading.Event()

        def slow_fetch(url, timeout):
            entered.set()
            release.wait(timeout=1.0)
            return {
                "code": "200",
                "now": {"temp": "31", "text": "晴"},
            }

        with (
            patch.object(
                metrics_agent,
                "_load_config",
                return_value={"weather": {"city": "田家庵"}},
            ),
            patch.object(metrics_agent, "_fetch_json", side_effect=slow_fetch),
            patch.object(metrics_agent, "read_foreground_app", return_value=metrics_agent.empty_snapshot()["foreground_app"]),
            patch.object(metrics_agent, "read_cpu_snapshot", return_value=metrics_agent.empty_snapshot()["cpu"]),
            patch.object(metrics_agent, "read_gpu_snapshot", return_value=metrics_agent.empty_snapshot()["gpu"]),
            patch.object(metrics_agent, "read_fps_snapshot", return_value=metrics_agent.empty_snapshot()["fps"]),
            patch.object(metrics_agent, "read_memory_snapshot", return_value=metrics_agent.empty_snapshot()["memory"]),
            patch.object(metrics_agent, "enumerate_disks", return_value=[]),
            patch.object(metrics_agent, "read_physical_disks", return_value=[]),
            patch.object(metrics_agent, "read_network_snapshot", return_value=metrics_agent.empty_snapshot()["network"]),
            patch.object(metrics_agent, "read_top_processes", return_value=[]),
            patch.object(metrics_agent, "read_lhm_sensor_snapshot", return_value={}),
            patch.object(metrics_agent, "_maybe_write_data_trust_log"),
        ):
            started = time.perf_counter()
            metrics_agent.build_snapshot()
            elapsed = time.perf_counter() - started
            self.assertTrue(entered.wait(timeout=0.2))
            release.set()
            deadline = time.monotonic() + 1.0
            while metrics_agent._weather_refreshing and time.monotonic() < deadline:
                time.sleep(0.01)

        self.assertLess(elapsed, 0.2)

    def test_parse_float_accepts_libre_hardware_monitor_degree_text(self):
        self.assertEqual(66.4, metrics_agent._parse_float("66.4 ｡紊"))
        self.assertEqual(1.308, metrics_agent._parse_float("1.308 V"))

    def test_lhm_sensor_snapshot_extracts_cpu_and_gpu_physical_metrics(self):
        payload = {
            "Text": "Sensor",
            "Children": [
                {
                    "Text": "WLY",
                    "Children": [
                        {
                            "Text": "AMD Ryzen 9 9950X3D",
                            "Children": [
                                {
                                    "Text": "Clocks",
                                    "Children": [
                                        {"Text": "Cores (Average)", "Value": "5557.0 MHz"},
                                        {"Text": "Cores (Average Effective)", "Value": "1734.0 MHz"},
                                    ],
                                },
                                {"Text": "Powers", "Children": [{"Text": "Package", "Value": "144.8 W"}]},
                                {"Text": "Temperatures", "Children": [{"Text": "Core (Tctl/Tdie)", "Value": "66.4 ｡紊"}]},
                            ],
                        },
                        {
                            "Text": "Gigabyte X870E AORUS PRO ICE",
                            "Children": [
                                {"Text": "Voltages", "Children": [{"Text": "Vcore", "Value": "1.308 V"}]},
                                {
                                    "Text": "ITE IT8696E",
                                    "Children": [
                                        {
                                            "Text": "Temperatures",
                                            "Children": [{"Text": "System #1", "Value": "35.0 °C"}],
                                        }
                                    ],
                                },
                            ],
                        },
                        {
                            "Text": "Asgard - VAM5UH64C32BG-DVALWA (#1)",
                            "Children": [
                                {
                                    "Text": "Temperatures",
                                    "Children": [{"Text": "DIMM #1", "Value": "61.3 °C"}],
                                }
                            ],
                        },
                        {
                            "Text": "Asgard - VAM5UH64C32BG-DVALWA (#3)",
                            "Children": [
                                {
                                    "Text": "Temperatures",
                                    "Children": [{"Text": "DIMM #3", "Value": "62.0 °C"}],
                                }
                            ],
                        },
                        {
                            "Text": "NVIDIA GeForce RTX 5090 D",
                            "Children": [
                                {"Text": "Voltages", "Children": [{"Text": "GPU Core Voltage", "Value": "0.985 V"}]},
                                {"Text": "Powers", "Children": [{"Text": "GPU Package", "Value": "154.4 W"}]},
                                {
                                    "Text": "Clocks",
                                    "Children": [
                                        {"Text": "GPU Core", "Value": "2835.0 MHz"},
                                        {"Text": "GPU Memory", "Value": "16401.0 MHz"},
                                    ],
                                },
                                {
                                    "Text": "Temperatures",
                                    "Children": [
                                        {"Text": "GPU Core", "Value": "54.0 °C"},
                                        {"Text": "GPU Memory Junction", "Value": "70.0 °C"},
                                    ],
                                },
                            ],
                        },
                        {
                            "Text": "XPG GAMMIX S50 Lite",
                            "Children": [
                                {
                                    "Text": "Temperatures",
                                    "Children": [
                                        {"Text": "Composite Temperature", "Value": "49.0 °C"},
                                        {"Text": "Temperature #1", "Value": "-217.0 °C"},
                                    ],
                                },
                                {
                                    "Text": "Load",
                                    "Children": [
                                        {"Text": "Used Space", "Value": "40.2 %"},
                                        {"Text": "Total Activity", "Value": "46.2 %"},
                                    ],
                                },
                                {
                                    "Text": "Data",
                                    "Children": [{"Text": "Total Space", "Value": "2048.4 GB"}],
                                },
                            ],
                        },
                    ],
                }
            ],
        }

        sensors = metrics_agent._lhm_sensor_snapshot_from_payload(payload)

        self.assertEqual(66.4, sensors["cpu_temperature_celsius"])
        self.assertEqual(144.8, sensors["cpu_power_watts"])
        self.assertEqual(1.308, sensors["cpu_core_voltage"])
        self.assertEqual(5557.0, sensors["cpu_clock_mhz"])
        self.assertEqual(0.985, sensors["gpu_core_voltage"])
        self.assertEqual(154.4, sensors["gpu_power_watts"])
        self.assertEqual(54.0, sensors["gpu_temperature_celsius"])
        self.assertEqual(2835.0, sensors["gpu_core_clock_mhz"])
        self.assertEqual(16401.0, sensors["gpu_memory_clock_mhz"])
        self.assertEqual(70.0, sensors["gpu_hotspot_temperature_celsius"])
        self.assertEqual(35.0, sensors["motherboard_temperature_celsius"])
        self.assertEqual([61.3, 62.0], sensors["module_temperatures_celsius"])
        self.assertEqual(49.0, sensors["disk_sensors"][0]["temperature_celsius"])
        self.assertEqual(46.2, sensors["disk_sensors"][0]["activity_percent"])

    def test_lhm_url_candidates_use_config_and_cache_last_successful_endpoint(self):
        attempts = []

        def fake_fetch(url, timeout):
            attempts.append(url)
            if url == "http://127.0.0.1:18085/data.json":
                return {"Text": "Sensor", "Children": []}
            raise OSError("not listening")

        with (
            patch.dict(
                metrics_agent.os.environ,
                {"TURZX_LHM_SENSOR_URL": "http://127.0.0.1:18084"},
                clear=False,
            ),
            patch.object(
                metrics_agent,
                "_load_config",
                return_value={"metrics": {"lhmUrls": ["http://127.0.0.1:18085"]}},
            ),
            patch.object(metrics_agent, "_fetch_json", side_effect=fake_fetch),
        ):
            first_payload, first_url = metrics_agent._fetch_lhm_sensor_payload()
            attempts.clear()
            second_payload, second_url = metrics_agent._fetch_lhm_sensor_payload()

        self.assertEqual({"Text": "Sensor", "Children": []}, first_payload)
        self.assertEqual("http://127.0.0.1:18085/data.json", first_url)
        self.assertEqual(first_payload, second_payload)
        self.assertEqual(first_url, second_url)
        self.assertEqual(["http://127.0.0.1:18085/data.json"], attempts)

    def test_lhm_average_clock_is_reported_without_effective_clock(self):
        payload = {
            "Text": "Sensor",
            "Children": [
                {
                    "Text": "CPU",
                    "Children": [
                        {
                            "Text": "Clocks",
                            "Children": [
                                {"Text": "Cores (Average)", "Value": "5557.0 MHz"},
                            ],
                        }
                    ],
                }
            ],
        }

        sensors = metrics_agent._lhm_sensor_snapshot_from_payload(payload)

        self.assertEqual(5557.0, sensors["cpu_clock_mhz"])

    def test_lhm_effective_clock_without_average_is_not_reported(self):
        payload = {
            "Text": "Sensor",
            "Children": [
                {
                    "Text": "CPU",
                    "Children": [
                        {
                            "Text": "Clocks",
                            "Children": [
                                {"Text": "Cores (Average Effective)", "Value": "1734.0 MHz"},
                            ],
                        }
                    ],
                }
            ],
        }

        sensors = metrics_agent._lhm_sensor_snapshot_from_payload(payload)

        self.assertIsNone(sensors["cpu_clock_mhz"])

    def test_lhm_average_clock_wins_regardless_of_sensor_order(self):
        for clocks in (
            [
                {"Text": "Cores (Average)", "Value": "5557.0 MHz"},
                {"Text": "Cores (Average Effective)", "Value": "1734.0 MHz"},
            ],
            [
                {"Text": "Cores (Average Effective)", "Value": "1734.0 MHz"},
                {"Text": "Cores (Average)", "Value": "5557.0 MHz"},
            ],
        ):
            with self.subTest(clocks=clocks):
                payload = {
                    "Text": "Sensor",
                    "Children": [
                        {
                            "Text": "CPU",
                            "Children": [{"Text": "Clocks", "Children": clocks}],
                        }
                    ],
                }

                sensors = metrics_agent._lhm_sensor_snapshot_from_payload(payload)

                self.assertEqual(5557.0, sensors["cpu_clock_mhz"])

    def test_lhm_zero_average_clock_is_not_reported(self):
        payload = {
            "Text": "Sensor",
            "Children": [
                {
                    "Text": "CPU",
                    "Children": [
                        {
                            "Text": "Clocks",
                            "Children": [
                                {"Text": "Cores (Average)", "Value": "0.0 MHz"},
                            ],
                        }
                    ],
                }
            ],
        }

        sensors = metrics_agent._lhm_sensor_snapshot_from_payload(payload)

        self.assertIsNone(sensors["cpu_clock_mhz"])

    def test_build_snapshot_merges_lhm_metrics_and_refresh_interval(self):
        fake_cpu = metrics_agent.empty_snapshot()["cpu"]
        fake_cpu["clock_mhz"] = 4292.0
        fake_cpu["clock_ghz"] = 4.29
        fake_cpu["source"] = "win32_getsystemtimes+psutil"
        fake_gpu = metrics_agent._fallback_gpu_snapshot()
        fake_memory = metrics_agent.empty_snapshot()["memory"]
        lhm = {
            "cpu_temperature_celsius": 66.4,
            "cpu_power_watts": 144.8,
            "cpu_core_voltage": 1.308,
            "cpu_clock_mhz": 1734.0,
            "gpu_core_voltage": 0.985,
            "gpu_temperature_celsius": 54.0,
            "gpu_power_watts": 154.4,
            "gpu_core_clock_mhz": 2835.0,
            "gpu_memory_clock_mhz": 16401.0,
            "motherboard_temperature_celsius": 35.0,
            "module_temperatures_celsius": [61.3, 62.0],
            "source": "lhm",
            "status": "live",
        }

        with (
            patch.object(metrics_agent, "read_weather_snapshot", return_value=metrics_agent.empty_snapshot()["weather"]),
            patch.object(metrics_agent, "enumerate_disks", return_value=[]),
            patch.object(metrics_agent, "read_physical_disks", return_value=[]),
            patch.object(metrics_agent, "read_top_processes", return_value=[]),
            patch.object(metrics_agent, "read_network_snapshot", return_value=metrics_agent.empty_snapshot()["network"]),
            patch.object(metrics_agent, "read_foreground_app", return_value=metrics_agent.empty_snapshot()["foreground_app"]),
            patch.object(metrics_agent, "read_cpu_snapshot", return_value=fake_cpu),
            patch.object(metrics_agent, "read_gpu_snapshot", return_value=fake_gpu),
            patch.object(metrics_agent, "read_memory_snapshot", return_value=fake_memory),
            patch.object(metrics_agent, "read_lhm_sensor_snapshot", return_value=lhm),
            patch.object(metrics_agent, "_configured_refresh_interval_seconds", return_value=1.0),
            patch.object(metrics_agent, "_DPC_TIME_SAMPLER", SimpleNamespace(sample=lambda: 0.2)),
        ):
            snapshot = metrics_agent.build_snapshot()

        self.assertEqual(66.4, snapshot["cpu"]["temperature_celsius"])
        self.assertEqual(144.8, snapshot["cpu"]["power_watts"])
        self.assertEqual(1.308, snapshot["cpu"]["core_voltage"])
        self.assertEqual(1734.0, snapshot["cpu"]["clock_mhz"])
        self.assertEqual(1.734, snapshot["cpu"]["clock_ghz"])
        self.assertEqual("win32_getsystemtimes+psutil+lhm", snapshot["cpu"]["source"])
        self.assertEqual(0.985, snapshot["gpu"]["core_voltage"])
        self.assertEqual(54.0, snapshot["gpu"]["temperature_celsius"])
        self.assertEqual(154.4, snapshot["gpu"]["power_watts"])
        self.assertEqual(2835.0, snapshot["gpu"]["core_clock_mhz"])
        self.assertEqual(16401.0, snapshot["gpu"]["memory_clock_mhz"])
        self.assertEqual(35.0, snapshot["memory"]["motherboard_temperature_celsius"])
        self.assertEqual([61.3, 62.0], snapshot["memory"]["module_temperatures_celsius"])
        self.assertEqual(1.0, snapshot["health"]["refresh_interval_seconds"])
        self.assertIsNone(snapshot["health"]["dpc_latency_us"])
        self.assertEqual(0.2, snapshot["network"]["dpc_percent"])
        self.assertIn("pdh", snapshot["network"]["source"])

    def test_windows_dpc_sampler_reports_real_percent_contract(self):
        sampler = metrics_agent.WindowsDpcTimeSampler(read_value=lambda: 0.23456)

        self.assertEqual(0.235, sampler.sample())

    def test_windows_dpc_sampler_failure_is_nonfatal(self):
        sampler = metrics_agent.WindowsDpcTimeSampler(
            read_value=lambda: (_ for _ in ()).throw(OSError("counter unavailable"))
        )

        self.assertIsNone(sampler.sample())

    def test_build_snapshot_surfaces_fps_error_in_health_and_header(self):
        fps_error = metrics_agent.empty_snapshot()["fps"]
        fps_error.update(
            {
                "status": "error",
                "source": "timeaudit_postgres",
                "detail": "TimeoutError",
            }
        )
        with (
            patch.object(metrics_agent, "read_weather_snapshot", return_value=metrics_agent.empty_snapshot()["weather"]),
            patch.object(metrics_agent, "read_foreground_app", return_value=metrics_agent.empty_snapshot()["foreground_app"]),
            patch.object(metrics_agent, "read_cpu_snapshot", return_value=metrics_agent.empty_snapshot()["cpu"]),
            patch.object(metrics_agent, "read_gpu_snapshot", return_value=metrics_agent.empty_snapshot()["gpu"]),
            patch.object(metrics_agent, "read_fps_snapshot", return_value=fps_error),
            patch.object(metrics_agent, "read_memory_snapshot", return_value=metrics_agent.empty_snapshot()["memory"]),
            patch.object(metrics_agent, "enumerate_disks", return_value=[]),
            patch.object(metrics_agent, "read_physical_disks", return_value=[]),
            patch.object(metrics_agent, "read_network_snapshot", return_value=metrics_agent.empty_snapshot()["network"]),
            patch.object(metrics_agent, "read_top_processes", return_value=[]),
            patch.object(metrics_agent, "read_lhm_sensor_snapshot", return_value={}),
            patch.object(metrics_agent, "_DPC_TIME_SAMPLER", SimpleNamespace(sample=lambda: None)),
            patch.object(metrics_agent, "_maybe_write_data_trust_log"),
        ):
            snapshot = metrics_agent.build_snapshot()

        self.assertEqual("degraded", snapshot["health"]["status"])
        self.assertEqual("warn", snapshot["alert"]["level"])
        self.assertEqual("采集异常 1 项", snapshot["alert"]["message"])

    def test_lhm_sensor_snapshot_returns_stale_cache_while_background_refresh_runs(self):
        if hasattr(metrics_agent, "_reset_lhm_cache_for_tests"):
            metrics_agent._reset_lhm_cache_for_tests()
        cached = {"cpu_clock_mhz": 1734.0, "source": "lhm", "status": "live"}
        metrics_agent._lhm_cache_value = cached
        metrics_agent._lhm_cache_expires_at = 0.0
        metrics_agent._lhm_last_success_at = time.monotonic()
        refresh_calls = []

        with (
            patch.object(
                metrics_agent,
                "_fetch_json",
                side_effect=AssertionError("sync LHM fetch should not run"),
            ),
            patch.object(
                metrics_agent,
                "_start_lhm_sensor_refresh_locked",
                side_effect=lambda: refresh_calls.append(True),
            ),
        ):
            sensors = metrics_agent.read_lhm_sensor_snapshot()

        self.assertEqual(1734.0, sensors["cpu_clock_mhz"])
        self.assertEqual("lhm_stale", sensors["source"])
        self.assertEqual("stale", sensors["status"])
        self.assertEqual([True], refresh_calls)

    def test_lhm_sensor_snapshot_cold_cache_returns_connecting_without_blocking(self):
        if hasattr(metrics_agent, "_reset_lhm_cache_for_tests"):
            metrics_agent._reset_lhm_cache_for_tests()
        refresh_calls = []

        with (
            patch.object(
                metrics_agent,
                "_fetch_json",
                side_effect=AssertionError("sync LHM fetch should not run"),
            ),
            patch.object(
                metrics_agent,
                "_start_lhm_sensor_refresh_locked",
                side_effect=lambda: refresh_calls.append(True),
            ),
        ):
            sensors = metrics_agent.read_lhm_sensor_snapshot()

        self.assertEqual("lhm", sensors["source"])
        self.assertEqual("connecting", sensors["status"])
        self.assertEqual([True], refresh_calls)

    def test_lhm_sensor_snapshot_drops_values_after_stale_window(self):
        metrics_agent._lhm_cache_value = {
            "cpu_clock_mhz": 1734.0,
            "source": "lhm",
            "status": "live",
        }
        metrics_agent._lhm_cache_expires_at = 0.0
        metrics_agent._lhm_last_success_at = (
            time.monotonic() - metrics_agent.LHM_LAST_GOOD_MAX_AGE_SECONDS - 1.0
        )

        with patch.object(metrics_agent, "_start_lhm_sensor_refresh_locked"):
            sensors = metrics_agent.read_lhm_sensor_snapshot()

        self.assertNotIn("cpu_clock_mhz", sensors)
        self.assertEqual("lhm", sensors["source"])
        self.assertEqual("unavailable", sensors["status"])

    def test_lhm_refresh_failure_marks_recent_cache_stale(self):
        metrics_agent._lhm_cache_value = {
            "cpu_clock_mhz": 1734.0,
            "source": "lhm",
            "status": "live",
        }
        metrics_agent._lhm_last_success_at = time.monotonic()
        metrics_agent._lhm_refreshing = True

        with patch.object(
            metrics_agent,
            "_fetch_lhm_sensor_payload",
            side_effect=OSError("endpoint down"),
        ):
            metrics_agent._refresh_lhm_sensor_cache()
        sensors = metrics_agent.read_lhm_sensor_snapshot()

        self.assertEqual(1734.0, sensors["cpu_clock_mhz"])
        self.assertEqual("lhm_stale", sensors["source"])
        self.assertEqual("stale", sensors["status"])

    def test_network_rate_sampler_reports_bytes_per_second_from_delta(self):
        readings = iter(
            [
                SimpleNamespace(bytes_recv=1_000, bytes_sent=2_000),
                SimpleNamespace(bytes_recv=2_024, bytes_sent=3_024),
            ]
        )
        timestamps = iter([10.0, 10.5])
        sampler = metrics_agent.NetworkRateSampler(
            read_counters=lambda: next(readings),
            now=lambda: next(timestamps),
        )

        first = sampler.sample()
        second = sampler.sample()

        self.assertIsNone(first["rx_bytes_per_sec"])
        self.assertIsNone(first["tx_bytes_per_sec"])
        self.assertEqual(2048.0, second["rx_bytes_per_sec"])
        self.assertEqual(2048.0, second["tx_bytes_per_sec"])

    def test_network_latency_sampler_reports_ping_jitter_and_loss(self):
        readings = iter([55.0, 61.0, None])
        sampler = metrics_agent.NetworkLatencySampler(
            read_ping_ms=lambda: next(readings),
            now=lambda: 10.0,
            ttl_seconds=30.0,
        )

        sampler._refresh()
        first = sampler.sample()
        sampler._refresh()
        second = sampler.sample()
        sampler._refresh()
        third = sampler.sample()

        self.assertEqual(55.0, first["ping_ms"])
        self.assertEqual(0.0, first["jitter_ms"])
        self.assertEqual(0.0, first["packet_loss_percent"])
        self.assertEqual(61.0, second["ping_ms"])
        self.assertEqual(6.0, second["jitter_ms"])
        self.assertEqual(33.3, third["packet_loss_percent"])

    def test_network_latency_cold_refresh_is_non_blocking(self):
        release = threading.Event()

        def slow_ping():
            release.wait(timeout=1.0)
            return 55.0

        sampler = metrics_agent.NetworkLatencySampler(
            read_ping_ms=slow_ping,
            ttl_seconds=30.0,
        )

        started = time.perf_counter()
        result = sampler.sample()
        elapsed = time.perf_counter() - started
        release.set()

        self.assertIsNone(result["ping_ms"])
        self.assertEqual("connecting", result["status"])
        self.assertLess(elapsed, 0.1)

    def test_build_snapshot_does_not_wait_for_slow_ping(self):
        release = threading.Event()

        def slow_ping():
            release.wait(timeout=1.0)
            return 55.0

        network_sampler = metrics_agent.NetworkRateSampler(
            read_counters=lambda: SimpleNamespace(bytes_recv=1_000, bytes_sent=2_000),
            latency_sampler=metrics_agent.NetworkLatencySampler(
                read_ping_ms=slow_ping,
                ttl_seconds=30.0,
            ),
        )

        with (
            patch.object(metrics_agent, "read_weather_snapshot", return_value=metrics_agent.empty_snapshot()["weather"]),
            patch.object(metrics_agent, "read_foreground_app", return_value=metrics_agent.empty_snapshot()["foreground_app"]),
            patch.object(metrics_agent, "read_cpu_snapshot", return_value=metrics_agent.empty_snapshot()["cpu"]),
            patch.object(metrics_agent, "read_gpu_snapshot", return_value=metrics_agent.empty_snapshot()["gpu"]),
            patch.object(metrics_agent, "read_fps_snapshot", return_value=metrics_agent.empty_snapshot()["fps"]),
            patch.object(metrics_agent, "read_memory_snapshot", return_value=metrics_agent.empty_snapshot()["memory"]),
            patch.object(metrics_agent, "enumerate_disks", return_value=[]),
            patch.object(metrics_agent, "read_physical_disks", return_value=[]),
            patch.object(metrics_agent, "read_network_snapshot", side_effect=network_sampler.sample),
            patch.object(metrics_agent, "read_top_processes", return_value=[]),
            patch.object(metrics_agent, "read_lhm_sensor_snapshot", return_value={}),
            patch.object(metrics_agent, "_maybe_write_data_trust_log"),
        ):
            started = time.perf_counter()
            metrics_agent.build_snapshot()
            elapsed = time.perf_counter() - started
            release.set()

        self.assertLess(elapsed, 0.2)

    def test_process_activity_sampler_reports_cpu_and_memory_from_deltas(self):
        class FakeProcess:
            def __init__(self, pid, name, cpu_seconds, rss):
                self.info = {
                    "pid": pid,
                    "name": name,
                    "create_time": 1.0,
                    "cpu_times": SimpleNamespace(user=cpu_seconds, system=0.0),
                    "memory_info": SimpleNamespace(rss=rss),
                }

        samples = iter(
            [
                [FakeProcess(10, "game.exe", 1.0, 3 * 1024 * 1024 * 1024)],
                [FakeProcess(10, "game.exe", 1.5, 3 * 1024 * 1024 * 1024)],
            ]
        )
        timestamps = iter([20.0, 21.0])
        sampler = metrics_agent.ProcessActivitySampler(
            process_iter=lambda attrs=None: next(samples),
            now=lambda: next(timestamps),
            cpu_count=2,
        )

        first = sampler.sample(limit=1)
        second = sampler.sample(limit=1)

        self.assertIsNone(first[0]["cpu_percent"])
        self.assertEqual(25.0, second[0]["cpu_percent"])
        self.assertEqual(3072.0, second[0]["memory_mb"])

    def test_process_activity_sampler_excludes_system_idle_process(self):
        class FakeProcess:
            def __init__(self, pid, name, cpu_seconds, rss):
                self.info = {
                    "pid": pid,
                    "name": name,
                    "create_time": 1.0,
                    "cpu_times": SimpleNamespace(user=cpu_seconds, system=0.0),
                    "memory_info": SimpleNamespace(rss=rss),
                }

        sampler = metrics_agent.ProcessActivitySampler(
            process_iter=lambda attrs=None: [
                FakeProcess(0, "System Idle Process", 10.0, 0),
                FakeProcess(20, "chrome.exe", 2.0, 512 * 1024 * 1024),
            ],
            now=lambda: 30.0,
            cpu_count=2,
        )

        processes = sampler.sample(limit=5)

        self.assertEqual(["chrome.exe"], [process["name"] for process in processes])

    def test_process_activity_sampler_aggregates_processes_by_app_name(self):
        class FakeProcess:
            def __init__(self, pid, name, cpu_seconds, rss):
                self.info = {
                    "pid": pid,
                    "name": name,
                    "create_time": float(pid),
                    "cpu_times": SimpleNamespace(user=cpu_seconds, system=0.0),
                    "memory_info": SimpleNamespace(rss=rss),
                }

        samples = iter(
            [
                [
                    FakeProcess(20, "chrome.exe", 1.0, 512 * 1024 * 1024),
                    FakeProcess(21, "chrome.exe", 2.0, 256 * 1024 * 1024),
                ],
                [
                    FakeProcess(20, "chrome.exe", 1.2, 512 * 1024 * 1024),
                    FakeProcess(21, "chrome.exe", 2.4, 256 * 1024 * 1024),
                ],
            ]
        )
        timestamps = iter([40.0, 41.0])
        sampler = metrics_agent.ProcessActivitySampler(
            process_iter=lambda attrs=None: next(samples),
            now=lambda: next(timestamps),
            cpu_count=2,
        )

        sampler.sample(limit=5)
        processes = sampler.sample(limit=5)

        self.assertEqual(1, len(processes))
        self.assertEqual("chrome.exe", processes[0]["name"])
        self.assertEqual(30.0, processes[0]["cpu_percent"])
        self.assertEqual(768.0, processes[0]["memory_mb"])

    def test_top_processes_reads_helper_cache_file_without_sampling(self):
        if hasattr(metrics_agent, "_reset_top_processes_cache_for_tests"):
            metrics_agent._reset_top_processes_cache_for_tests()
        cache_path = Path(__file__).resolve().parent / "out" / "test-top-processes.json"
        cache_path.parent.mkdir(exist_ok=True)
        payload = {
            "schema_version": 1,
            "generated_at_unix_ms": int(time.time() * 1000),
            "processes": [
                {
                    "name": "Typora.exe",
                    "description": None,
                    "pid": 42,
                    "cpu_percent": 3.0,
                    "gpu_percent": None,
                    "memory_mb": 512.0,
                    "memory_gb": 0.5,
                    "source": "top_processes_helper",
                }
            ],
        }
        cache_path.write_text(json.dumps(payload), encoding="utf-8")

        with (
            patch.object(metrics_agent, "TOP_PROCESSES_CACHE_PATH", str(cache_path)),
            patch.object(
                metrics_agent,
                "_PROCESS_SAMPLER",
                SimpleNamespace(sample=lambda limit=5: (_ for _ in ()).throw(AssertionError("main agent must not sample processes"))),
            ),
        ):
            processes = metrics_agent.read_top_processes()

        self.assertEqual("Typora.exe", processes[0]["name"])
        self.assertEqual("top_processes_helper", processes[0]["source"])

    def test_top_processes_refresh_interval_keeps_dashboard_feeling_live(self):
        self.assertLessEqual(metrics_agent.TOP_PROCESSES_HELPER_MAX_AGE_SECONDS, 10.0)
        self.assertGreaterEqual(metrics_agent.TOP_PROCESSES_HELPER_MAX_AGE_SECONDS, 8.0)

    def test_top_processes_ignores_stale_helper_cache_without_blocking(self):
        if hasattr(metrics_agent, "_reset_top_processes_cache_for_tests"):
            metrics_agent._reset_top_processes_cache_for_tests()
        cache_path = Path(__file__).resolve().parent / "out" / "test-top-processes-stale.json"
        cache_path.parent.mkdir(exist_ok=True)
        payload = {
            "schema_version": 1,
            "generated_at_unix_ms": int((time.time() - 60) * 1000),
            "processes": [
                {
                    "name": "stale.exe",
                    "description": None,
                    "pid": 99,
                    "cpu_percent": 99.0,
                    "gpu_percent": None,
                    "memory_mb": 1.0,
                    "memory_gb": 0.0,
                    "source": "top_processes_helper",
                }
            ],
        }
        cache_path.write_text(json.dumps(payload), encoding="utf-8")
        with (
            patch.object(metrics_agent, "TOP_PROCESSES_CACHE_PATH", str(cache_path)),
            patch.object(
                metrics_agent,
                "_PROCESS_SAMPLER",
                SimpleNamespace(sample=lambda limit=5: (_ for _ in ()).throw(AssertionError("sync sample should not run"))),
            ),
        ):
            processes = metrics_agent.read_top_processes()

        self.assertEqual([], processes)

    def test_top_processes_helper_writes_cache_atomically(self):
        import importlib.util

        helper_path = Path(__file__).resolve().parent / "top_processes_helper.py"
        spec = importlib.util.spec_from_file_location("top_processes_helper_test", helper_path)
        helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(helper)
        cache_path = Path(__file__).resolve().parent / "out" / "helper-write-test.json"
        cache_path.parent.mkdir(exist_ok=True)

        helper.write_cache_atomic(
            cache_path,
            [{"name": "Typora.exe", "cpu_percent": 7.5, "memory_mb": 512.0}],
            generated_at_unix_ms=123456,
        )

        payload = json.loads(cache_path.read_text(encoding="utf-8"))
        self.assertEqual(1, payload["schema_version"])
        self.assertEqual(123456, payload["generated_at_unix_ms"])
        self.assertEqual("Typora.exe", payload["processes"][0]["name"])

    def test_top_processes_helper_reuses_sampler_for_cpu_deltas(self):
        import importlib.util

        helper_path = Path(__file__).resolve().parent / "top_processes_helper.py"
        spec = importlib.util.spec_from_file_location("top_processes_helper_test", helper_path)
        helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(helper)

        class FakeSampler:
            def __init__(self):
                self.calls = 0

            def sample(self, limit=5):
                self.calls += 1
                return [
                    {
                        "name": "Game.exe",
                        "pid": 100,
                        "cpu_percent": float(self.calls),
                        "memory_mb": 512.0,
                    }
                ]

        fake_sampler = FakeSampler()
        helper._PROCESS_SAMPLER = fake_sampler

        first = helper.collect_top_processes(limit=5)
        second = helper.collect_top_processes(limit=5)

        self.assertEqual(2, fake_sampler.calls)
        self.assertEqual(1.0, first[0]["cpu_percent"])
        self.assertEqual(2.0, second[0]["cpu_percent"])

    def test_top_processes_helper_loop_compensates_for_sampling_cost(self):
        import importlib.util

        helper_path = Path(__file__).resolve().parent / "top_processes_helper.py"
        spec = importlib.util.spec_from_file_location("top_processes_helper_test", helper_path)
        helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(helper)

        self.assertAlmostEqual(2.4, helper._loop_sleep_seconds(10.0, 10.6, 3.0), places=2)
        self.assertAlmostEqual(0.2, helper._loop_sleep_seconds(10.0, 13.5, 3.0), places=2)

    def test_fps_snapshot_cold_cache_reports_connecting_not_idle(self):
        with patch.object(
            metrics_agent,
            "read_timeaudit_latest_snapshot",
            return_value={"_status": "connecting"},
        ):
            fps = metrics_agent.read_fps_snapshot()

        self.assertIsNone(fps["current"])
        self.assertEqual("connecting", fps["status"])
        self.assertEqual("timeaudit_postgres", fps["source"])

    def test_timeaudit_snapshot_is_disabled_when_dsn_missing(self):
        if hasattr(metrics_agent, "_reset_timeaudit_cache_for_tests"):
            metrics_agent._reset_timeaudit_cache_for_tests()
        with patch.object(metrics_agent, "TIMEAUDIT_DSN", None):
            snapshot = metrics_agent.read_timeaudit_latest_snapshot()

        self.assertEqual("disabled", snapshot["_status"])

    def test_fps_snapshot_uses_timeaudit_latest_row_when_present(self):
        timestamp = metrics_agent.dt.datetime.now(metrics_agent.dt.timezone.utc)
        with patch.object(
            metrics_agent,
            "read_timeaudit_latest_snapshot",
            return_value={
                "_status": "ok",
                "timestamp": timestamp,
                "current_fps": 144.4,
                "average_fps": 141.2,
                "one_percent_low_fps": 118.6,
                "frametime_ms": 6.9,
            },
        ):
            fps = metrics_agent.read_fps_snapshot()

        self.assertEqual(144.4, fps["current"])
        self.assertEqual(141.2, fps["average"])
        self.assertEqual(118.6, fps["low_1_percent"])
        self.assertEqual(6.9, fps["frame_time_ms"])
        self.assertEqual("active", fps["status"])
        self.assertLess(fps["sample_age_seconds"], 1.0)
        self.assertEqual("timeaudit_postgres", fps["source"])

    def test_fps_snapshot_fresh_zero_row_is_normal_idle(self):
        timestamp = metrics_agent.dt.datetime.now(metrics_agent.dt.timezone.utc)
        with patch.object(
            metrics_agent,
            "read_timeaudit_latest_snapshot",
            return_value={
                "_status": "ok",
                "timestamp": timestamp,
                "current_fps": 0.0,
                "average_fps": 0.0,
                "one_percent_low_fps": 0.0,
                "frametime_ms": 0.0,
            },
        ):
            fps = metrics_agent.read_fps_snapshot()

        self.assertEqual("idle", fps["status"])
        self.assertEqual("timeaudit_postgres", fps["source"])
        self.assertIsNone(fps["current"])

    def test_fps_snapshot_old_row_is_stale_not_live(self):
        timestamp = metrics_agent.dt.datetime.now(metrics_agent.dt.timezone.utc) - metrics_agent.dt.timedelta(seconds=30)
        with patch.object(
            metrics_agent,
            "read_timeaudit_latest_snapshot",
            return_value={
                "_status": "ok",
                "timestamp": timestamp,
                "current_fps": 144.0,
                "average_fps": 140.0,
                "one_percent_low_fps": 110.0,
                "frametime_ms": 6.9,
            },
        ):
            fps = metrics_agent.read_fps_snapshot()

        self.assertEqual("stale", fps["status"])
        self.assertGreaterEqual(fps["sample_age_seconds"], 29.0)

    def test_fps_snapshot_propagates_collection_error(self):
        with patch.object(
            metrics_agent,
            "read_timeaudit_latest_snapshot",
            return_value={"_status": "error", "_detail": "TimeoutError"},
        ):
            fps = metrics_agent.read_fps_snapshot()

        self.assertEqual("error", fps["status"])
        self.assertEqual("TimeoutError", fps["detail"])

    def test_fps_idle_does_not_lower_trust(self):
        item = metrics_agent._trust_fps(
            {
                "status": "idle",
                "source": "timeaudit_postgres",
                "current": None,
                "frame_time_ms": None,
            }
        )

        self.assertEqual(100, item["score"])
        self.assertEqual("ok", item["status"])
        self.assertEqual([], item["missing"])

    def test_timeaudit_latest_snapshot_returns_stale_cache_while_background_refresh_runs(self):
        if hasattr(metrics_agent, "_reset_timeaudit_cache_for_tests"):
            metrics_agent._reset_timeaudit_cache_for_tests()
        cached = {"current_fps": 144.0}
        metrics_agent._timeaudit_cache_value = cached
        metrics_agent._timeaudit_cache_expires_at = 0.0
        refresh_calls = []

        with (
            patch.object(
                metrics_agent.asyncio,
                "run",
                side_effect=AssertionError("sync TimeAudit query should not run"),
            ),
            patch.object(
                metrics_agent,
                "_start_timeaudit_refresh_locked",
                side_effect=lambda: refresh_calls.append(True),
            ),
        ):
            snapshot = metrics_agent.read_timeaudit_latest_snapshot()

        self.assertEqual(cached, snapshot)
        self.assertEqual([True], refresh_calls)

    def test_timeaudit_latest_snapshot_cold_cache_returns_connecting_without_blocking(self):
        if hasattr(metrics_agent, "_reset_timeaudit_cache_for_tests"):
            metrics_agent._reset_timeaudit_cache_for_tests()
        refresh_calls = []

        with (
            patch.object(
                metrics_agent.asyncio,
                "run",
                side_effect=AssertionError("sync TimeAudit query should not run"),
            ),
            patch.object(
                metrics_agent,
                "_start_timeaudit_refresh_locked",
                side_effect=lambda: refresh_calls.append(True),
            ),
        ):
            snapshot = metrics_agent.read_timeaudit_latest_snapshot()

        self.assertEqual("connecting", snapshot["_status"])
        self.assertEqual([True], refresh_calls)

    def test_timeaudit_refresh_exposes_query_failure(self):
        metrics_agent._reset_timeaudit_cache_for_tests()

        with patch.object(
            metrics_agent,
            "_read_timeaudit_latest_snapshot_async",
            side_effect=TimeoutError("database timeout"),
        ):
            metrics_agent._refresh_timeaudit_cache()

        self.assertEqual("error", metrics_agent._timeaudit_cache_value["_status"])
        self.assertEqual("TimeoutError", metrics_agent._timeaudit_cache_value["_detail"])
        self.assertFalse(metrics_agent._timeaudit_refreshing)

    def test_timeaudit_query_uses_bounded_connection_and_command_timeouts(self):
        calls = {}

        class FakeConnection:
            async def fetchrow(self, query):
                calls["query"] = query
                return {
                    "timestamp": metrics_agent.dt.datetime.now(metrics_agent.dt.timezone.utc),
                    "current_fps": 0.0,
                }

            async def close(self):
                calls["closed"] = True

        class FakeAsyncpg:
            async def connect(self, dsn, **kwargs):
                calls["dsn"] = dsn
                calls["kwargs"] = kwargs
                return FakeConnection()

        with (
            patch.object(metrics_agent, "TIMEAUDIT_DSN", "postgresql://local/test"),
            patch.object(metrics_agent, "_optional_import", return_value=FakeAsyncpg()),
        ):
            result = metrics_agent.asyncio.run(
                metrics_agent._read_timeaudit_latest_snapshot_async()
            )

        self.assertEqual("ok", result["_status"])
        self.assertEqual(
            metrics_agent.TIMEAUDIT_CONNECT_TIMEOUT_SECONDS,
            calls["kwargs"]["timeout"],
        )
        self.assertEqual(
            metrics_agent.TIMEAUDIT_QUERY_TIMEOUT_SECONDS,
            calls["kwargs"]["command_timeout"],
        )
        self.assertTrue(calls["closed"])

    def test_gpu_snapshot_falls_back_with_stable_schema_when_sources_fail(self):
        with (
            patch.object(metrics_agent, "_read_gpu_snapshot_nvml", return_value=None),
            patch.object(metrics_agent, "_read_gpu_snapshot_nvidia_smi", return_value=None),
        ):
            gpu = metrics_agent.read_gpu_snapshot()

        self.assertEqual(
            {
                "usage_percent",
                "temperature_c",
                "temperature_celsius",
                "name",
                "model",
                "power_watts",
                "core_voltage",
                "core_clock_mhz",
                "core_clock_ghz",
                "memory_clock_mhz",
                "memory_clock_ghz",
                "mem_clock_mhz",
                "vram_used_gb",
                "vram_total_gb",
                "load_history_percent",
                "status",
                "source",
            },
            set(gpu.keys()),
        )
        self.assertIsNone(gpu["usage_percent"])
        self.assertIsNone(gpu["temperature_c"])
        self.assertIsNone(gpu["name"])
        self.assertIsNone(gpu["core_clock_mhz"])
        self.assertIsNone(gpu["mem_clock_mhz"])
        self.assertEqual("fallback", gpu["source"])

    def test_gpu_snapshot_reuses_recent_good_sample_when_probe_temporarily_fails(self):
        live = metrics_agent._gpu_snapshot(
            source="nvidia-smi",
            name="NVIDIA GeForce RTX 5090 D",
            usage_percent=24,
            temperature_c=57,
            power_watts=120,
            core_clock_mhz=2947,
            memory_clock_mhz=16601,
            vram_used_gb=4.0,
            vram_total_gb=32.0,
        )
        with (
            patch.object(metrics_agent, "_read_gpu_snapshot_nvml", side_effect=[live, None]),
            patch.object(metrics_agent, "_read_gpu_snapshot_nvidia_smi", return_value=None),
            patch.object(metrics_agent.time, "monotonic", side_effect=[100.0, 102.0]),
        ):
            first = metrics_agent.read_gpu_snapshot()
            stale = metrics_agent.read_gpu_snapshot()

        self.assertEqual("nvidia-smi", first["source"])
        self.assertEqual(24.0, stale["usage_percent"])
        self.assertEqual(57.0, stale["temperature_celsius"])
        self.assertEqual("nvidia-smi+stale", stale["source"])
        self.assertEqual("stale", stale["status"])

    def test_gpu_snapshot_stops_reusing_sample_after_stale_safety_window(self):
        live = metrics_agent._gpu_snapshot(
            source="nvidia-smi",
            name="NVIDIA GeForce RTX 5090 D",
            usage_percent=24,
            temperature_c=57,
            power_watts=120,
            core_clock_mhz=2947,
            memory_clock_mhz=16601,
            vram_used_gb=4.0,
            vram_total_gb=32.0,
        )
        expired_at = 100.0 + metrics_agent.GPU_LAST_GOOD_MAX_AGE_SECONDS + 1.0
        with (
            patch.object(metrics_agent, "_read_gpu_snapshot_nvml", side_effect=[live, None]),
            patch.object(metrics_agent, "_read_gpu_snapshot_nvidia_smi", return_value=None),
            patch.object(metrics_agent.time, "monotonic", side_effect=[100.0, expired_at]),
        ):
            metrics_agent.read_gpu_snapshot()
            expired = metrics_agent.read_gpu_snapshot()

        self.assertEqual("fallback", expired["source"])
        self.assertIsNone(expired["usage_percent"])

    def test_parse_nvidia_smi_csv_returns_gpu_metrics(self):
        gpu = metrics_agent._parse_nvidia_smi_csv(
            "NVIDIA GeForce RTX 5090 D, 24, 57, 2947, 16601\n"
        )

        self.assertEqual("NVIDIA GeForce RTX 5090 D", gpu["name"])
        self.assertEqual(24.0, gpu["usage_percent"])
        self.assertEqual(57, gpu["temperature_c"])
        self.assertEqual(2947, gpu["core_clock_mhz"])
        self.assertEqual(16601, gpu["memory_clock_mhz"])
        self.assertEqual(16601, gpu["mem_clock_mhz"])
        self.assertEqual("nvidia-smi", gpu["source"])

    def test_gpu_snapshot_uses_nvml_when_available(self):
        class FakeNvml:
            NVML_TEMPERATURE_GPU = 0
            NVML_CLOCK_GRAPHICS = 0
            NVML_CLOCK_MEM = 2

            def nvmlInit(self):
                pass

            def nvmlDeviceGetCount(self):
                return 1

            def nvmlDeviceGetHandleByIndex(self, index):
                return object()

            def nvmlDeviceGetName(self, handle):
                return b"NVIDIA GeForce RTX 5090 D"

            def nvmlDeviceGetUtilizationRates(self, handle):
                return SimpleNamespace(gpu=28, memory=14)

            def nvmlDeviceGetTemperature(self, handle, sensor):
                return 58

            def nvmlDeviceGetPowerUsage(self, handle):
                return 174390

            def nvmlDeviceGetClockInfo(self, handle, clock_type):
                return 16601 if clock_type == self.NVML_CLOCK_MEM else 3217

            def nvmlDeviceGetMemoryInfo(self, handle):
                return SimpleNamespace(
                    used=4529 * 1024 * 1024,
                    total=32607 * 1024 * 1024,
                )

            def nvmlShutdown(self):
                pass

        with patch.object(metrics_agent, "_optional_import", return_value=FakeNvml()):
            gpu = metrics_agent._read_gpu_snapshot_nvml()

        self.assertEqual("NVIDIA GeForce RTX 5090 D", gpu["name"])
        self.assertEqual(28.0, gpu["usage_percent"])
        self.assertEqual(58.0, gpu["temperature_celsius"])
        self.assertEqual(174.39, gpu["power_watts"])
        self.assertEqual(3217.0, gpu["core_clock_mhz"])
        self.assertEqual(16601.0, gpu["memory_clock_mhz"])
        self.assertEqual(4.42, gpu["vram_used_gb"])
        self.assertEqual(31.84, gpu["vram_total_gb"])
        self.assertEqual("nvml", gpu["source"])

    def test_nvidia_smi_reader_uses_timeout_and_returns_none_on_timeout(self):
        with (
            patch.object(metrics_agent.shutil, "which", return_value="nvidia-smi"),
            patch.object(
                metrics_agent.subprocess,
                "run",
                side_effect=subprocess.TimeoutExpired("nvidia-smi", 0.1),
            ) as run,
        ):
            self.assertIsNone(metrics_agent._read_gpu_snapshot_nvidia_smi())

        self.assertIn("timeout", run.call_args.kwargs)
        self.assertLessEqual(run.call_args.kwargs["timeout"], 1.0)

    def test_cpu_usage_sampler_reports_percent_from_time_delta(self):
        readings = iter(
            [
                (100, 1_000, 1_000),
                (125, 1_100, 1_100),
            ]
        )
        sampler = metrics_agent.CpuUsageSampler(lambda: next(readings))

        self.assertEqual(87.5, sampler.sample())

    def test_cpu_snapshot_has_stable_schema_when_sampler_fails(self):
        sampler = metrics_agent.CpuUsageSampler(lambda: None)

        with (
            patch.object(metrics_agent, "_CPU_SAMPLER", sampler),
            patch.object(metrics_agent, "_optional_import", return_value=None),
        ):
            cpu = metrics_agent.read_cpu_snapshot()

        self.assertEqual(
            {
                "model",
                "usage_percent",
                "temperature_celsius",
                "power_watts",
                "clock_ghz",
                "clock_mhz",
                "core_voltage",
                "load_history_percent",
                "logical_count",
                "status",
                "source",
            },
            set(cpu.keys()),
        )
        self.assertIsNone(cpu["usage_percent"])
        self.assertEqual("fallback", cpu["source"])

    def test_cpu_snapshot_uses_psutil_load_but_rejects_nominal_clock(self):
        sampler = metrics_agent.CpuUsageSampler(lambda: None)
        fake_psutil = SimpleNamespace(
            cpu_percent=lambda interval=None: 42.5,
            cpu_freq=lambda: SimpleNamespace(current=5557.0),
        )

        with (
            patch.object(metrics_agent, "_CPU_SAMPLER", sampler),
            patch.object(metrics_agent, "_optional_import", return_value=fake_psutil),
            patch.object(metrics_agent, "_read_cpu_model", return_value="AMD Ryzen 9"),
        ):
            cpu = metrics_agent.read_cpu_snapshot()

        self.assertEqual("AMD Ryzen 9", cpu["model"])
        self.assertEqual(42.5, cpu["usage_percent"])
        self.assertIsNone(cpu["clock_mhz"])
        self.assertEqual("active", cpu["status"])
        self.assertEqual("psutil", cpu["source"])
        self.assertGreaterEqual(len(cpu["load_history_percent"]), 1)

    def test_cpu_snapshot_does_not_label_psutil_nominal_clock_as_dynamic(self):
        readings = iter(
            [
                (100, 1_000, 1_000),
                (150, 1_100, 1_100),
            ]
        )
        sampler = metrics_agent.CpuUsageSampler(lambda: next(readings))
        fake_psutil = SimpleNamespace(
            cpu_percent=lambda interval=None: 99.0,
            cpu_freq=lambda: SimpleNamespace(current=5557.0),
        )

        with (
            patch.object(metrics_agent, "_CPU_SAMPLER", sampler),
            patch.object(metrics_agent, "_optional_import", return_value=fake_psutil),
            patch.object(metrics_agent, "_read_cpu_model", return_value="AMD Ryzen 9"),
        ):
            cpu = metrics_agent.read_cpu_snapshot()

        self.assertEqual(75.0, cpu["usage_percent"])
        self.assertIsNone(cpu["clock_mhz"])
        self.assertEqual("win32_getsystemtimes", cpu["source"])

    def test_build_snapshot_copies_vram_into_memory_block(self):
        fake_gpu = metrics_agent._fallback_gpu_snapshot()
        fake_gpu["vram_used_gb"] = 4.42
        fake_gpu["vram_total_gb"] = 31.84
        fake_memory = metrics_agent.empty_snapshot()["memory"]

        with (
            patch.object(metrics_agent, "read_weather_snapshot", return_value=metrics_agent.empty_snapshot()["weather"]),
            patch.object(metrics_agent, "enumerate_disks", return_value=[]),
            patch.object(metrics_agent, "read_physical_disks", return_value=[]),
            patch.object(metrics_agent, "read_top_processes", return_value=[]),
            patch.object(metrics_agent, "read_network_snapshot", return_value=metrics_agent.empty_snapshot()["network"]),
            patch.object(metrics_agent, "read_foreground_app", return_value=metrics_agent.empty_snapshot()["foreground_app"]),
            patch.object(metrics_agent, "read_cpu_snapshot", return_value=metrics_agent.empty_snapshot()["cpu"]),
            patch.object(metrics_agent, "read_gpu_snapshot", return_value=fake_gpu),
            patch.object(metrics_agent, "read_memory_snapshot", return_value=fake_memory),
            patch.object(metrics_agent, "read_lhm_sensor_snapshot", return_value={}),
        ):
            snapshot = metrics_agent.build_snapshot()

        self.assertEqual(4.42, snapshot["memory"]["vram_used_gb"])
        self.assertEqual(31.84, snapshot["memory"]["vram_total_gb"])
        self.assertEqual(13.9, snapshot["memory"]["vram_usage_percent"])


if __name__ == "__main__":
    unittest.main()
