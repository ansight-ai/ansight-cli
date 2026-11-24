"""Stage the pinned Moonshine macOS runtime for release signing."""
import hashlib
import io
import json
from pathlib import Path
import sys
import urllib.request
import zipfile

manifest_path, output_path = map(Path, sys.argv[1:])
asset = json.loads(manifest_path.read_text())["runtimes"]["osx-arm64"]
with urllib.request.urlopen(asset["url"], timeout=60) as response:
    wheel = response.read(asset["size"] + 1)

if len(wheel) != asset["size"] or hashlib.sha256(wheel).hexdigest() != asset["sha256"]:
    raise SystemExit("The pinned Moonshine wheel failed integrity verification.")

with zipfile.ZipFile(io.BytesIO(wheel)) as archive:
    names = archive.namelist()
    libraries = [name for name in names if name.startswith("moonshine_voice/") and name.endswith(".dylib")]
    if not {"libmoonshine.dylib", "libonnxruntime.1.23.2.dylib"} <= {Path(name).name for name in libraries}:
        raise SystemExit("The pinned Moonshine wheel is missing its native libraries.")

    for name in libraries:
        (output_path / Path(name).name).write_bytes(archive.read(name))

    for name in names:
        filename = Path(name).name
        if ".dist-info/" in name and filename.upper().startswith(("LICENSE", "NOTICE", "THIRDPARTY")):
            (output_path / ("Moonshine-" + filename)).write_bytes(archive.read(name))
