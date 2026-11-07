"""Conservative publication check. Prints locations, never matching secret values."""
import argparse
import json
import re
import subprocess
from pathlib import Path

RULES = {
    "provider_token": rb"(?:sk-(?:proj-|ant-)?|gsk_)[A-Za-z0-9_-]{20,}",
    "github_token": rb"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})",
    "aws_access_key": rb"(?:AKIA|ASIA)[A-Z0-9]{16}",
    "private_key": rb"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
    "url_credentials": rb"https?://[^\s/:'\"]+:[^\s/@'\"]+@",
}
PATTERNS = {name: re.compile(value) for name, value in RULES.items()}
FORBIDDEN = {"models.json", "preview-settings.json", "startup-ui-original.json", ".env", ".env.local"}


def findings(content):
    return [name for name, pattern in PATTERNS.items() if pattern.search(content)]


def git(*args):
    return subprocess.check_output(["git", *args])


def audit(history=False):
    issues = []
    files = git("ls-files", "--cached", "--others", "--exclude-standard", "-z").split(b"\0")
    for raw in filter(None, files):
        path = raw.decode("utf-8")
        target = Path(path)
        if target.name.lower() in FORBIDDEN or target.suffix.lower() in {".dll", ".exe", ".dwg", ".dxf", ".pdf"}:
            issues.append({"path": path, "rule": "private_config_or_binary"})
        if target.is_file():
            issues.extend({"path": path, "rule": rule} for rule in findings(target.read_bytes()))
    scanned = 0
    if history:
        objects = {}
        for line in git("rev-list", "--objects", "--all").splitlines():
            parts = line.split(b" ", 1)
            if len(parts) == 2:
                objects[parts[0]] = parts[1].decode("utf-8", errors="replace")
        data = subprocess.check_output(["git", "cat-file", "--batch"], input=b"\n".join(objects) + b"\n")
        offset = 0
        while offset < len(data):
            end = data.index(b"\n", offset)
            oid, kind, size = data[offset:end].split()
            start = end + 1
            content = data[start:start + int(size)]
            offset = start + int(size) + 1
            if kind != b"blob":
                continue
            scanned += 1
            path = objects[oid]
            if Path(path).name.lower() in FORBIDDEN or Path(path).suffix.lower() in {".dll", ".exe", ".dwg", ".dxf", ".pdf"}:
                issues.append({"path": path, "object": oid.decode(), "rule": "private_config_or_binary"})
            issues.extend({"path": path, "object": oid.decode(), "rule": rule} for rule in findings(content))
    return {"worktree_files": len(list(filter(None, files))), "historical_blobs": scanned, "findings": issues,
            "scope": "Known token patterns and forbidden paths; not a guarantee that all sensitive content is absent."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--history", action="store_true")
    report = audit(parser.parse_args().history)
    print(json.dumps(report, ensure_ascii=True, indent=2))
    raise SystemExit(bool(report["findings"]))
