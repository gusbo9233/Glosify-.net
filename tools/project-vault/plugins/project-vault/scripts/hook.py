import json
from pathlib import Path
import subprocess
import sys
from runner import repository, command


def decision(fresh, continued, message=""):
    if fresh:
        return {}
    if continued:
        return {"continue": False, "stopReason": "Project Vault documentation review is blocked. Published explanations still need review. " + message,
                "systemMessage": "Implementation may be finished, but documentation review is blocked. Do not claim affected documents are reviewed."}
    return {"decision": "block", "reason": "Project Vault has authored documents needing review, or review status is unavailable. Use vault_document_impacts, inspect changed source and semantic impacts, then revise affected documents or record an evidenced review. Refreshing the static index does not review documents. Retry once on failure and report remaining blockage. " + message}


def main():
    payload = json.load(sys.stdin)
    root = repository(payload.get("cwd"))
    if not (root / ".project-visualization").exists():
        return {}
    if len(sys.argv) > 1 and sys.argv[1] == "start":
        return {"hookSpecificOutput": {"hookEventName": "SessionStart", "additionalContext": "This repository uses Project Vault. Load the project-vault skill. After EVERY coherent implementation step and relevant verification, inspect vault_document_impacts and review affected authored documents before proceeding. Use source and optional static analysis as reference and sanity checks; user questions and agent understanding drive content. Publish revised explanations or record an evidenced unchanged review. Check vault_document_status before completion. Index refresh cannot mark documents reviewed. Documentation requests do not authorize code changes."}}
    try:
        result = subprocess.run(command("document-status", root), capture_output=True, text=True, timeout=20)
        status = json.loads(result.stdout) if result.returncode == 0 else {}
        return decision(status.get("fresh", False), payload.get("stop_hook_active", False), status.get("error") or "")
    except Exception as exc:
        return decision(False, payload.get("stop_hook_active", False), str(exc))


if __name__ == "__main__":
    try:
        print(json.dumps(main()))
    except Exception as exc:
        print(json.dumps({"systemMessage": "Project Vault hook could not inspect this session: " + str(exc)}))
