#!/usr/bin/env python3
"""Measure first and repeat URL imports against an API; no third-party modules required."""
import argparse
import json
import math
import statistics
import time
import urllib.error
import urllib.request


def percentile(values, fraction):
    return sorted(values)[max(0, math.ceil(len(values) * fraction) - 1)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api", required=True, help="API base URL, e.g. http://localhost:8080")
    parser.add_argument("--rounds", type=int, default=5)
    parser.add_argument("urls", nargs="+")
    args = parser.parse_args()
    if args.rounds < 2:
        parser.error("--rounds must be at least 2")
    endpoint = args.api.rstrip("/") + "/Parser/Parse"
    first, repeats = [], []
    failures = 0
    for url in args.urls:
        for index in range(args.rounds):
            request = urllib.request.Request(endpoint, data=json.dumps({"url": url}).encode(),
                                             headers={"Content-Type": "application/json"})
            started = time.perf_counter()
            try:
                with urllib.request.urlopen(request, timeout=130) as response:
                    recipe = json.load(response)
                    if not recipe.get("title") or not recipe.get("ingredients") or not recipe.get("stepSections"):
                        raise ValueError("Response is missing recipe content")
                duration = (time.perf_counter() - started) * 1000
                (first if index == 0 else repeats).append(duration)
                print(json.dumps({"url": url, "attempt": index + 1, "ms": round(duration, 2), "ok": True}))
            except (urllib.error.URLError, TimeoutError, ValueError) as error:
                failures += 1
                print(json.dumps({"url": url, "attempt": index + 1, "ok": False, "error": str(error)}))
    for name, values in [("first-request", first), ("repeat-request", repeats)]:
        if values:
            print(json.dumps({"group": name, "count": len(values),
                              "p50_ms": round(statistics.median(values), 2),
                              "p95_ms": round(percentile(values, .95), 2)}))
    raise SystemExit(1 if failures else 0)


if __name__ == "__main__":
    main()
