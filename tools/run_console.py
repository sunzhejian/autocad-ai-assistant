"""Capture AutoCAD's UTF-16 output faithfully and bound a scratch console run."""
import subprocess
import sys
import uuid
from pathlib import Path

executable, script, log = sys.argv[1:]
# FILEDIA and other profile variables are shared with desktop AutoCAD unless
# /isolate is used. Keep even failed or timed-out test runs out of that profile.
user_data = Path(script).resolve().parent / "coreconsole-user"
user_data.mkdir(parents=True, exist_ok=True)
isolation_id = "cadai_" + uuid.uuid4().hex
try:
    result = subprocess.run([executable, "/s", script, "/l", "en-US",
                             "/isolate", isolation_id, str(user_data)], stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, timeout=120,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    output = result.stdout
    code = result.returncode
except subprocess.TimeoutExpired as error:
    output = error.output or b""
    code = 124
encoding = "utf-16-le" if output[:200].count(b"\x00") > 20 else "utf-8"
Path(log).write_text(output.decode(encoding, errors="replace"), encoding="utf-8")
print(f"AutoCAD console exit={code}; log={log}")
sys.exit(code)
