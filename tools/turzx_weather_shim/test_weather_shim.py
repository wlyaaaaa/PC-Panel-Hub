import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import turzx_weather_shim as shim


class QWeatherShimTests(unittest.TestCase):
    def test_build_now_payload_matches_qweather_shape_for_current_city(self):
        open_meteo_payload = {
            "current": {
                "time": "2026-07-03T10:15",
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

        payload = shim.build_now_payload("101220405", open_meteo_payload, "zh")

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

    def test_current_turzx_city_code_resolves_to_tianjiaan(self):
        location = shim.resolve_location("101220405")

        self.assertEqual(location["name"], "田家庵")
        self.assertAlmostEqual(location["latitude"], 32.65, places=2)
        self.assertAlmostEqual(location["longitude"], 117.02, places=2)


if __name__ == "__main__":
    unittest.main()
