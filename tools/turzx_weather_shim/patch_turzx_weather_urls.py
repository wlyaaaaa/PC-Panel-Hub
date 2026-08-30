import argparse
import json
import os
from pathlib import Path


URL_CONFIG_ENV = "TURZX_WEATHER_URL_PATCH_CONFIG"
URL_SEED = 19
REQUIRED_URL_FIELDS = ("old_geo", "new_geo", "old_now", "new_now")


def encode_turzx_string(text, seed=URL_SEED):
    num = 2004917786 + seed + 14 + 89 + 89 + 65
    chars = []
    for char in text:
        code = ord(char)
        decoded_low = code & 0xFF
        decoded_high = (code >> 8) & 0xFF
        encoded_low = decoded_high ^ (num & 0xFF)
        num += 1
        encoded_high = decoded_low ^ (num & 0xFF)
        num += 1
        chars.append(chr((encoded_high << 8) | encoded_low))
    return "".join(chars)


def encoded_bytes(text):
    return encode_turzx_string(text).encode("utf-16le", "surrogatepass")


def load_url_config(path):
    if not path:
        raise ValueError(
            "URL patch config is required. Set TURZX_WEATHER_URL_PATCH_CONFIG or use --url-config."
        )
    payload = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(payload, dict):
        raise ValueError("URL patch config must be a JSON object.")

    result = {}
    for field in REQUIRED_URL_FIELDS:
        value = payload.get(field)
        if not isinstance(value, str) or not value:
            raise ValueError(f"URL patch config requires non-empty {field}.")
        result[field] = value
    return result


def patch_bytes(data, old_text, new_text, label):
    if len(old_text) != len(new_text):
        raise ValueError(
            f"{label} replacement length mismatch: {len(old_text)} != {len(new_text)}"
        )

    old_bytes = encoded_bytes(old_text)
    new_bytes = encoded_bytes(new_text)
    offset = data.find(old_bytes)
    if offset < 0:
        raise ValueError(f"Could not find encoded {label} URL.")
    if data.find(old_bytes, offset + 1) >= 0:
        raise ValueError(f"Encoded {label} URL appears more than once.")

    data[offset : offset + len(old_bytes)] = new_bytes
    return offset


def patch_exe(exe_path, output_path, url_config):
    data = bytearray(exe_path.read_bytes())
    geo_offset = patch_bytes(
        data,
        url_config["old_geo"],
        url_config["new_geo"],
        "geo",
    )
    now_offset = patch_bytes(
        data,
        url_config["old_now"],
        url_config["new_now"],
        "current-weather",
    )
    output_path.write_bytes(data)
    return geo_offset, now_offset


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", default="TURZX.exe")
    parser.add_argument("--out", default="TURZX.weatherfix.exe")
    parser.add_argument(
        "--url-config",
        default=os.environ.get(URL_CONFIG_ENV),
        help="Machine-local JSON URL config path.",
    )
    args = parser.parse_args()

    try:
        url_config = load_url_config(args.url_config)
        geo_offset, now_offset = patch_exe(
            Path(args.exe),
            Path(args.out),
            url_config,
        )
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        parser.error(str(exc))

    print(f"Patched geo URL at byte offset {geo_offset}")
    print(f"Patched now URL at byte offset {now_offset}")
    print(f"Output: {Path(args.out).resolve()}")


if __name__ == "__main__":
    main()
