import argparse
import json
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 18080

KNOWN_LOCATIONS = {
    "101220405": {
        "id": "101220405",
        "name": "田家庵",
        "adm2": "淮南",
        "adm1": "安徽",
        "country": "中国",
        "latitude": 32.65,
        "longitude": 117.02,
    }
}

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


def resolve_location(location_id):
    text = str(location_id or "").strip()
    if text in KNOWN_LOCATIONS:
        return dict(KNOWN_LOCATIONS[text])

    if "," in text:
        first, second = [part.strip() for part in text.split(",", 1)]
        first_value = float(first)
        second_value = float(second)
        if abs(first_value) > 90:
            longitude, latitude = first_value, second_value
        else:
            latitude, longitude = first_value, second_value
        return {
            "id": f"{longitude:.4f},{latitude:.4f}",
            "name": text,
            "adm2": "",
            "adm1": "",
            "country": "",
            "latitude": latitude,
            "longitude": longitude,
        }

    return dict(KNOWN_LOCATIONS["101220405"])


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


def build_now_payload(location_id, open_meteo_payload, lang="zh"):
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


def build_city_lookup_payload(query):
    location = resolve_location(query)
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
                "tz": "Asia/Shanghai",
                "utcOffset": "+08:00",
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
        location_id = query.get("location", ["101220405"])[0]
        lang = query.get("lang", ["zh"])[0]

        try:
            if parsed.path.endswith("/v7/weather/now"):
                location = resolve_location(location_id)
                weather_payload = fetch_open_meteo(location)
                try:
                    weather_payload = merge_air_quality(weather_payload, fetch_open_meteo_air_quality(location))
                except Exception:
                    pass
                payload = build_now_payload(location_id, weather_payload, lang)
                self.write_json(200, payload)
            elif parsed.path.endswith("/geo/v2/city/lookup"):
                self.write_json(200, build_city_lookup_payload(location_id))
            else:
                self.write_json(404, {"code": "404", "message": "Unknown TURZX weather shim endpoint"})
        except Exception as exc:
            self.write_json(500, {"code": "500", "message": str(exc)})

    def log_message(self, fmt, *args):
        print("[%s] %s" % (datetime.now().strftime("%Y-%m-%d %H:%M:%S"), fmt % args))

    def write_json(self, status, payload):
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


def run_server(host=DEFAULT_HOST, port=DEFAULT_PORT):
    server = ThreadingHTTPServer((host, port), WeatherShimHandler)
    print(f"TURZX weather shim listening on http://{host}:{port}")
    server.serve_forever()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    args = parser.parse_args()
    run_server(args.host, args.port)


if __name__ == "__main__":
    main()
