import argparse
import json
import math
import os
from pathlib import Path
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 18080
LOCATION_CONFIG_ENV = "TURZX_WEATHER_CONFIG"
LOCATION_LATITUDE_ENV = "TURZX_WEATHER_LATITUDE"
LOCATION_LONGITUDE_ENV = "TURZX_WEATHER_LONGITUDE"
LOCATION_ID_ENV = "TURZX_WEATHER_LOCATION_ID"
LOCATION_NAME_ENV = "TURZX_WEATHER_LOCATION_NAME"

WEATHER_TEXT_ZH = {
    0: "晴",
    1: "晴多",
    2: "少云",
    3: "多云",
    45: "雾",
    48: "雾",
    51: "小雨",
    53: "小雨",
    55: "小雨",
    56: "冻雨",
    57: "冻雨",
    61: "小雨",
    63: "中雨",
    65: "大雨",
    66: "冻雨",
    67: "冻雨",
    71: "小雪",
    73: "中雪",
    75: "大雪",
    77: "雪",
    80: "阵雨",
    81: "阵雨",
    82: "强阵雨",
    85: "阵雪",
    86: "阵雪",
    95: "雷雨",
    96: "雷雨",
    99: "强雷雨",
}


def optional_text(value):
    text = str(value or "").strip()
    return text or None


def normalize_location(payload):
    if not isinstance(payload, dict):
        raise ValueError("Weather location config must be a JSON object.")

    try:
        latitude = float(payload["latitude"])
        longitude = float(payload["longitude"])
    except (KeyError, TypeError, ValueError) as exc:
        raise ValueError(
            "Weather location config requires numeric latitude and longitude."
        ) from exc

    if not math.isfinite(latitude) or not -90 <= latitude <= 90:
        raise ValueError("Weather latitude must be between -90 and 90.")
    if not math.isfinite(longitude) or not -180 <= longitude <= 180:
        raise ValueError("Weather longitude must be between -180 and 180.")

    location_id = optional_text(payload.get("id")) or f"{longitude:.4f},{latitude:.4f}"
    name = optional_text(payload.get("name")) or optional_text(payload.get("city")) or location_id
    return {
        "id": location_id,
        "name": name,
        "adm2": optional_text(payload.get("adm2")) or "",
        "adm1": optional_text(payload.get("adm1")) or "",
        "country": optional_text(payload.get("country")) or "",
        "timezone": optional_text(payload.get("timezone")) or "",
        "utc_offset": optional_text(payload.get("utc_offset")) or "",
        "latitude": latitude,
        "longitude": longitude,
    }


def load_location_config(config_path=None, environ=None):
    source = os.environ if environ is None else environ
    selected_path = optional_text(config_path) or optional_text(
        source.get(LOCATION_CONFIG_ENV)
    )
    if selected_path:
        payload = json.loads(Path(selected_path).read_text(encoding="utf-8-sig"))
        if isinstance(payload, dict) and isinstance(payload.get("weather"), dict):
            payload = payload["weather"]
        return normalize_location(payload)

    latitude = optional_text(source.get(LOCATION_LATITUDE_ENV))
    longitude = optional_text(source.get(LOCATION_LONGITUDE_ENV))
    if latitude is None and longitude is None:
        raise ValueError(
            "Weather location is not configured. Set TURZX_WEATHER_CONFIG or both "
            "TURZX_WEATHER_LATITUDE and TURZX_WEATHER_LONGITUDE."
        )
    if latitude is None or longitude is None:
        raise ValueError(
            "TURZX_WEATHER_LATITUDE and TURZX_WEATHER_LONGITUDE must be set together."
        )

    return normalize_location(
        {
            "latitude": latitude,
            "longitude": longitude,
            "id": source.get(LOCATION_ID_ENV),
            "name": source.get(LOCATION_NAME_ENV),
        }
    )


def coordinate_location(text):
    first, second = [part.strip() for part in text.split(",", 1)]
    first_value = float(first)
    second_value = float(second)
    if abs(first_value) > 90:
        longitude, latitude = first_value, second_value
    else:
        latitude, longitude = first_value, second_value
    return normalize_location(
        {
            "id": f"{longitude:.4f},{latitude:.4f}",
            "name": text,
            "latitude": latitude,
            "longitude": longitude,
        }
    )


def resolve_location(requested_location, configured_location=None):
    text = optional_text(requested_location) or ""
    if "," in text:
        return coordinate_location(text)
    if configured_location is not None:
        return normalize_location(configured_location)
    raise ValueError(
        "Weather location is not configured. The shim has no built-in location."
    )


def weather_text(code, lang="zh"):
    if str(lang).lower().startswith("zh"):
        return WEATHER_TEXT_ZH.get(int(code), "多云")
    return "Cloudy" if int(code) == 3 else "Clear"


def wind_direction_cn(degrees):
    directions = [
        "北",
        "东北",
        "东",
        "东南",
        "南",
        "西南",
        "西",
        "西北",
    ]
    index = int((float(degrees) % 360 + 22.5) // 45) % 8
    return directions[index]


def beaufort_scale(speed_kmh):
    thresholds = [1, 6, 12, 20, 29, 39, 50, 62, 75, 89, 103, 118]
    speed = float(speed_kmh)
    for scale, threshold in enumerate(thresholds):
        if speed < threshold:
            return str(scale)
    return "12"


def rounded_text(value):
    return str(int(round(float(value))))


def build_now_payload(open_meteo_payload, lang="zh"):
    current = open_meteo_payload["current"]
    daily = open_meteo_payload.get("daily")
    daily = daily if isinstance(daily, dict) else {}
    now_time = current.get("time") or datetime.now(timezone.utc).isoformat()
    wind_degrees = current.get("wind_direction_10m", 0)
    wind_speed = current.get("wind_speed_10m", 0)
    aqi = current.get("us_aqi")

    return {
        "code": "200",
        "updateTime": now_time,
        "fxLink": "",
        "now": {
            "obsTime": now_time,
            "temp": rounded_text(current.get("temperature_2m", 0)),
            "feelsLike": rounded_text(current.get("apparent_temperature", current.get("temperature_2m", 0))),
            "icon": str(current.get("weather_code", 3)),
            "text": weather_text(current.get("weather_code", 3), lang),
            "wind360": rounded_text(wind_degrees),
            "windDir": wind_direction_cn(wind_degrees),
            "windScale": beaufort_scale(wind_speed),
            "windSpeed": rounded_text(wind_speed),
            "humidity": rounded_text(current.get("relative_humidity_2m", 0)),
            "aqi": rounded_text(aqi) if aqi is not None else "",
            "precip": "0.0",
            "pressure": rounded_text(current.get("pressure_msl", 0)),
            "vis": "",
            "cloud": "",
            "dew": "",
        },
        "daily": {
            "temperature_max": first_value(
                daily.get("temperature_2m_max")
            ),
            "temperature_min": first_value(
                daily.get("temperature_2m_min")
            ),
            "precipitation_probability_max": first_value(
                daily.get("precipitation_probability_max")
            ),
        },
        "refer": {"sources": ["open-meteo"], "license": ["CC BY 4.0"]},
    }


def first_value(value):
    if isinstance(value, list) and value:
        return value[0]
    return None


def build_city_lookup_payload(query, configured_location=None):
    location = resolve_location(query, configured_location)
    return {
        "code": "200",
        "location": [
            {
                "name": location["name"],
                "id": location["id"],
                "lat": str(location["latitude"]),
                "lon": str(location["longitude"]),
                "adm2": location["adm2"],
                "adm1": location["adm1"],
                "country": location["country"],
                "tz": location["timezone"],
                "utcOffset": location["utc_offset"],
                "isDst": "0",
                "type": "city",
                "rank": "10",
                "fxLink": "",
            }
        ],
        "refer": {"sources": ["open-meteo"], "license": ["CC BY 4.0"]},
    }


def fetch_open_meteo(location):
    params = {
        "latitude": location["latitude"],
        "longitude": location["longitude"],
        "current": ",".join(
            [
                "temperature_2m",
                "relative_humidity_2m",
                "apparent_temperature",
                "weather_code",
                "wind_speed_10m",
                "wind_direction_10m",
                "pressure_msl",
            ]
        ),
        "daily": ",".join(
            [
                "temperature_2m_max",
                "temperature_2m_min",
                "precipitation_probability_max",
            ]
        ),
        "forecast_days": 1,
        "wind_speed_unit": "kmh",
        "timezone": "auto",
    }
    url = "https://api.open-meteo.com/v1/forecast?" + urllib.parse.urlencode(params)
    with urllib.request.urlopen(url, timeout=10) as response:
        return json.loads(response.read().decode("utf-8"))


def fetch_open_meteo_air_quality(location):
    params = {
        "latitude": location["latitude"],
        "longitude": location["longitude"],
        "current": "us_aqi",
        "timezone": "auto",
    }
    url = "https://air-quality-api.open-meteo.com/v1/air-quality?" + urllib.parse.urlencode(params)
    with urllib.request.urlopen(url, timeout=10) as response:
        return json.loads(response.read().decode("utf-8"))


def merge_air_quality(weather_payload, air_quality_payload):
    weather_current = weather_payload.get("current")
    air_current = air_quality_payload.get("current") if isinstance(air_quality_payload, dict) else None
    if not isinstance(weather_current, dict) or not isinstance(air_current, dict):
        return weather_payload

    aqi = air_current.get("us_aqi")
    if aqi is not None:
        weather_current["us_aqi"] = aqi
    return weather_payload


class WeatherShimHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        query = urllib.parse.parse_qs(parsed.query)
        location_id = query.get("location", [""])[0]
        lang = query.get("lang", ["zh"])[0]
        configured_location = getattr(self.server, "configured_location", None)

        try:
            if parsed.path.endswith("/v7/weather/now"):
                location = resolve_location(location_id, configured_location)
                weather_payload = fetch_open_meteo(location)
                try:
                    weather_payload = merge_air_quality(weather_payload, fetch_open_meteo_air_quality(location))
                except Exception:
                    pass
                payload = build_now_payload(weather_payload, lang)
                self.write_json(200, payload)
            elif parsed.path.endswith("/geo/v2/city/lookup"):
                self.write_json(
                    200,
                    build_city_lookup_payload(location_id, configured_location),
                )
            else:
                self.write_json(404, {"code": "404", "message": "Unknown TURZX weather shim endpoint"})
        except ValueError as exc:
            self.write_json(503, {"code": "503", "message": str(exc)})
        except Exception as exc:
            print(f"Weather upstream failed: {type(exc).__name__}")
            self.write_json(502, {"code": "502", "message": "Weather upstream failed"})

    def log_message(self, fmt, *args):
        print("[%s] %s" % (datetime.now().strftime("%Y-%m-%d %H:%M:%S"), fmt % args))

    def write_json(self, status, payload):
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def run_server(
    host=DEFAULT_HOST,
    port=DEFAULT_PORT,
    configured_location=None,
    config_path=None,
    environ=None,
):
    location = (
        normalize_location(configured_location)
        if configured_location is not None
        else load_location_config(config_path, environ)
    )
    server = ThreadingHTTPServer((host, port), WeatherShimHandler)
    server.configured_location = location
    print(f"TURZX weather shim listening on http://{host}:{port}")
    server.serve_forever()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument(
        "--config",
        default=os.environ.get(LOCATION_CONFIG_ENV),
        help="Machine-local JSON config path (or set TURZX_WEATHER_CONFIG).",
    )
    args = parser.parse_args()
    try:
        run_server(args.host, args.port, config_path=args.config)
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        parser.error(str(exc))


if __name__ == "__main__":
    main()
