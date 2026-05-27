#!/usr/bin/env python3
# Read a build log and append a summary of compiler/linker errors to
# the GitHub Actions step summary so they show up in notification emails.
#
# Usage: extract_build_errors.py <build_log>

import os
import re
import sys

# MSVC:    foo.cpp(123): error C2065: ...           (also: fatal error, warning)
# Clang/GCC: foo.cpp:123:45: error: ...             (also: fatal error, warning)
# Linker:  LINK : fatal error LNK1104: ...
# Ninja:   FAILED: target
ERROR_PATTERNS = [
    re.compile(r".*\b(error|fatal error)\b.*", re.IGNORECASE),
    re.compile(r"^FAILED:\s.*"),
    re.compile(r"^ninja:\serror:.*"),
    re.compile(r".*:\s+undefined reference to .*"),
]

# Patterns we want to ignore even if they match the above (e.g. summary lines).
IGNORE_PATTERNS = [
    re.compile(r"^\s*\d+\s+error[s]?\s+generated\.\s*$"),
    re.compile(r"^\s*\d+\s+warning[s]?\s+and\s+\d+\s+error[s]?\s+generated\.\s*$"),
]


def is_error_line(line: str) -> bool:
    if any(p.search(line) for p in IGNORE_PATTERNS):
        return False
    return any(p.search(line) for p in ERROR_PATTERNS)


def main() -> int:
    if len(sys.argv) != 2:
        print(f"usage: {sys.argv[0]} <build_log>", file=sys.stderr)
        return 2

    log_path = sys.argv[1]
    if not os.path.exists(log_path):
        print(f"build log not found: {log_path}", file=sys.stderr)
        return 0

    with open(log_path, "r", encoding="utf-8", errors="replace") as f:
        lines = f.readlines()

    error_lines = []
    seen = set()
    for line in lines:
        stripped = line.rstrip()
        if not is_error_line(stripped):
            continue
        if stripped in seen:
            continue
        seen.add(stripped)
        error_lines.append(stripped)

    if not error_lines:
        print("no error lines extracted from build log")
        return 0

    # Cap the summary so we don't overflow GitHub's 1MB step-summary limit.
    max_lines = 200
    truncated = len(error_lines) > max_lines
    shown = error_lines[:max_lines]

    summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
    if not summary_path:
        # Local invocation: print to stdout for inspection.
        for line in shown:
            print(line)
        return 0

    with open(summary_path, "a", encoding="utf-8") as out:
        out.write("## Build Errors\n\n")
        out.write("```\n")
        for line in shown:
            out.write(line + "\n")
        if truncated:
            out.write(f"... {len(error_lines) - max_lines} more line(s) truncated\n")
        out.write("```\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
