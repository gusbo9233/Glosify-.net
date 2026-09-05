"""Local runner shared by MCP and hooks. No shell interpolation."""
import json
import os
from pathlib import Path
import subprocess
import sys


def repository(start=None):
    current = Path(start or os.getcwd()).resolve()
    result = subprocess.run(["git", "-C", str(current), "rev-parse", "--show-toplevel"], capture_output=True, text=True)
    if result.returncode == 0:
        return Path(result.stdout.strip())
    for candidate in (current, *current.parents):
        if (candidate / ".project-visualization").is_dir():
            return candidate
    return current


def command(action, root):
    config = root / ".project-visualization/local/tool.json"
    configured = json.loads(config.read_text()).get("toolRoot") if config.exists() else None
    home = os.environ.get("PROJECT_VAULT_HOME") or configured or (root / "tools/project-vault" if (root / "tools/project-vault/server").is_dir() else None)
    if not home:
        raise RuntimeError("Set PROJECT_VAULT_HOME to the built tool directory, or start Project Vault for this repository. No static index is required.")
    home = Path(home).resolve()
    dll = home / "server/bin/Debug/net10.0/ProjectVault.dll"
    if not dll.exists():
        raise RuntimeError("Project Vault is not built. Run its setup script.")
    return ["dotnet", str(dll), action, "--repo", str(root), "--tool-root", str(home)]


if __name__ == "__main__":
    try:
        root = repository()
        args = command(sys.argv[1] if len(sys.argv) > 1 else "status", root)
        os.execvp(args[0], args)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(1)
