import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import patch_turzx_weather_urls as patcher
import turzx_weather_shim as shim


SAMPLE_LOCATION = {
    "id": "test-location",
    "name": "Test City",
    "adm2": "Test District",
    "adm1": "Test Region",
    "country": "Test Country",
    "timezone": "Etc/UTC",
    "utc_offset": "+00:00",
    "latitude": 12.5,
    "longitude": 34.5,
}


class QWeatherShimTests(unittest.TestCase):
    def test_build_now_payload_matches_qweather_shape(self):
        open_meteo_payload = {
            "current": {
                "time": "2026-01-01T00:00",
                "temperature_2m": 27.4,
                "relative_humidity_2m": 61,
                "apparent_temperature": 29.1,
                "weather_code": 3,
                "wind_speed_10m": 16.2,
                "wind_direction_10m": 45,
                "pressure_msl": 1006.8,
                "us_aqi": 74.6,
            },
            "daily": {
                "temperature_2m_max": [34.2],
                "temperature_2m_min": [26.1],
                "precipitation_probability_max": [45],
            },
        }

        payload = shim.build_now_payload(open_meteo_payload, "zh")

        self.assertEqual(payload["code"], "200")
        self.assertEqual(payload["now"]["temp"], "27")
        self.assertEqual(payload["now"]["text"], "多云")
        self.assertEqual(payload["now"]["windDir"], "东北")
        self.assertEqual(payload["now"]["windScale"], "3")
        self.assertEqual(payload["now"]["humidity"], "61")
        self.assertEqual(payload["now"]["aqi"], "75")
        self.assertEqual(payload["daily"]["temperature_max"], 34.2)
        self.assertEqual(payload["daily"]["temperature_min"], 26.1)
        self.assertEqual(
            payload["daily"]["precipitation_probability_max"],
            45,
        )

    def test_wind_direction_is_compact_for_small_turzx_panel(self):
        self.assertEqual(shim.wind_direction_cn(22.5), "东北")
        self.assertLessEqual(len(shim.weather_text(1, "zh")), 3)

    def test_configured_location_handles_legacy_non_coordinate_request(self):
        location = shim.resolve_location("legacy-request", SAMPLE_LOCATION)

        self.assertEqual(location["name"], "Test City")
        self.assertAlmostEqual(location["latitude"], 12.5, places=2)
        self.assertAlmostEqual(location["longitude"], 34.5, places=2)

    def test_coordinate_request_can_override_configured_location(self):
        location = shim.resolve_location("12.5,34.5", SAMPLE_LOCATION)

        self.assertAlmostEqual(location["latitude"], 12.5, places=2)
        self.assertAlmostEqual(location["longitude"], 34.5, places=2)

    def test_missing_location_fails_closed(self):
        with self.assertRaisesRegex(ValueError, "no built-in location"):
            shim.resolve_location("legacy-request")

    def test_location_can_come_from_environment(self):
        location = shim.load_location_config(
            environ={
                "TURZX_WEATHER_LATITUDE": "12.5",
                "TURZX_WEATHER_LONGITUDE": "34.5",
                "TURZX_WEATHER_LOCATION_NAME": "Test City",
            }
        )

        self.assertEqual(location["name"], "Test City")
        self.assertEqual(location["latitude"], 12.5)
        self.assertEqual(location["longitude"], 34.5)

    def test_location_file_can_use_side_screen_weather_section(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            config_path = Path(temp_dir) / "weather.json"
            config_path.write_text(
                json.dumps({"weather": SAMPLE_LOCATION}),
                encoding="utf-8",
            )

            location = shim.load_location_config(config_path)

        self.assertEqual(location["id"], "test-location")


class UrlPatchTests(unittest.TestCase):
    def test_patcher_uses_external_urls(self):
        config = {
            "old_geo": "geo-old-url",
            "new_geo": "geo-new-url",
            "old_now": "now-old-url",
            "new_now": "now-new-url",
        }
        original = (
            b"prefix"
            + patcher.encoded_bytes(config["old_geo"])
            + b"middle"
            + patcher.encoded_bytes(config["old_now"])
            + b"suffix"
        )

        with tempfile.TemporaryDirectory() as temp_dir:
            source = Path(temp_dir) / "source.exe"
            output = Path(temp_dir) / "patched.exe"
            source.write_bytes(original)

            patcher.patch_exe(source, output, config)
            patched = output.read_bytes()

        self.assertIn(patcher.encoded_bytes(config["new_geo"]), patched)
        self.assertIn(patcher.encoded_bytes(config["new_now"]), patched)
        self.assertNotIn(patcher.encoded_bytes(config["old_geo"]), patched)
        self.assertNotIn(patcher.encoded_bytes(config["old_now"]), patched)


if __name__ == "__main__":
    unittest.main()
