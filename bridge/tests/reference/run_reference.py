"""Runs the unmodified original bridge under test conditions.

Only the environment is adjusted, never the original source:
  * HOME points at a throw-away directory (set by the harness),
  * the MIDI port lists are narrowed to the test ports,
  * HTTP requests to the real sites are redirected to the local fixture server.
"""

from __future__ import annotations

import os
import runpy
import sys


def main() -> None:
    script = sys.argv[1]

    import mido

    midi_in = os.environ["BRIDGE_TEST_MIDI_IN"]
    midi_out = os.environ["BRIDGE_TEST_MIDI_OUT"]
    real_inputs, real_outputs = mido.get_input_names, mido.get_output_names
    mido.get_input_names = lambda *a, **k: [n for n in real_inputs(*a, **k) if midi_in in n]
    mido.get_output_names = lambda *a, **k: [n for n in real_outputs(*a, **k) if midi_out in n]

    base = os.environ.get("BRIDGE_HTTP_FIXTURE_BASE")
    if base:
        import requests

        real_request = requests.Session.request

        def redirected(self, method, url, *args, **kwargs):
            for host in ("freemidi.org", "bitmidi.com"):
                marker = f"://{host}/" if "www." not in url else f"://www.{host}/"
                if marker in url:
                    url = f"{base}/{host}/" + url.split(marker, 1)[1]
                    break
            return real_request(self, method, url, *args, **kwargs)

        requests.Session.request = redirected

    sys.argv = [script]
    runpy.run_path(script, run_name="__main__")


if __name__ == "__main__":
    main()
