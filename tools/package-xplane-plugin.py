"""Extract only the pinned native X-Plane plugin; never bundle the Python client."""
import hashlib
import io
import argparse
import subprocess
import tempfile
from pathlib import Path
import urllib.request
import zipfile

URL = "https://github.com/JianyueLab-Org/can-xplane-client/releases/download/v0.0.1/xpc-for-can-0.0.1-win-x64.zip"
SHA256 = "ebed57dc965b5e3e74de1946313066022022cc5d0d6732d9373eeb1345ff56bc"
ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "native" / "XPC_for_CAN-plugin.zip"
PREFIX = "xpc-for-can-0.0.1-win-x64/native_plugin/"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, help="Local copy of the pinned release zip (gh release download also works)")
    args = parser.parse_args()
    if args.source:
        data = args.source.read_bytes()
    else:
        try:
            with urllib.request.urlopen(URL, timeout=120) as response:
                data = response.read()
        except urllib.error.HTTPError:
            # Private repositories return 404 to anonymous downloads; reuse gh authentication.
            with tempfile.TemporaryDirectory() as temporary:
                subprocess.run(["gh", "release", "download", "v0.0.1", "--repo", "JianyueLab-Org/can-xplane-client",
                                "--pattern", "xpc-for-can-0.0.1-win-x64.zip", "--dir", temporary], check=True)
                data = (Path(temporary) / "xpc-for-can-0.0.1-win-x64.zip").read_bytes()
    if hashlib.sha256(data).hexdigest() != SHA256:
        raise RuntimeError("X-Plane source release checksum mismatch")
    with zipfile.ZipFile(io.BytesIO(data)) as source, zipfile.ZipFile(OUTPUT, "w", zipfile.ZIP_DEFLATED) as output:
        for name in source.namelist():
            if name.startswith(PREFIX) and not name.endswith("/"):
                output.writestr(name[len(PREFIX):], source.read(name))
        output.write(ROOT / "native" / "XPLANE-NOTICES.md", "XPC_for_CAN/NOTICES.md")
    print(OUTPUT)


if __name__ == "__main__":
    main()
