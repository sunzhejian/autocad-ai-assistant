"""Capture AutoCAD's UTF-16 output faithfully and bound a scratch console run."""
import subprocess
import sys
from pathlib import Path

executable, script, log = sys.argv[1:]
try:
    result = subprocess.run([executable, "/s", script, "/l", "en-US"], stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, timeout=120)
    output = result.stdout
    code = result.returncode
except subprocess.TimeoutExpired as error:
    output = error.output or b""
    code = 124
encoding = "utf-16-le" if output[:200].count(b"\x00") > 20 else "utf-8"
Path(log).write_text(output.decode(encoding, errors="replace"), encoding="utf-8")
print(f"AutoCAD console exit={code}; log={log}")
sys.exit(code)
