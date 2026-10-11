#!/usr/bin/env python3
"""Run only workflow gates/Git functions in private LOCAL fixtures; no SSH/deploy invocation."""
import hashlib
import json
import os
import pathlib
import shlex
import shutil
import subprocess
import tempfile

BASE = pathlib.Path(__file__).resolve().parents[2]
WORKFLOW = json.loads((BASE / ".github/workflows/deploy.yml").read_text(encoding="utf-8"))
BASH = os.environ.get("LARAFASHION_HARNESS_BASH", "bash")
GIT = shutil.which("git")
ROOT = pathlib.Path(tempfile.mkdtemp(prefix="LaraFashionRecoveryGit-"))
os.umask(0o077)
COUNT = 0


def passed(name):
    global COUNT
    COUNT += 1
    print("PASS recovery " + name, flush=True)


def git(*args, cwd=BASE):
    return subprocess.run([GIT, *args], cwd=cwd, check=True, capture_output=True).stdout


def posix(path):
    value = str(path).replace("\\", "/")
    return "/" + value[0].lower() + value[2:] if os.name == "nt" else value


def main():
    assert GIT
    initial_status = git("status", "--porcelain", "--untracked-files=all")
    initial_head = git("rev-parse", "HEAD")
    deploy = WORKFLOW["jobs"]["deploy"]
    gate, checkout, action = deploy["steps"]
    remote = (BASE / "deploy/actions-remote.sh").read_text(encoding="utf-8")
    assert WORKFLOW["on"]["push"] == {"branches": ["main"]} and "workflow_dispatch" in WORKFLOW["on"]
    assert WORKFLOW["concurrency"] == {"group": "larafashion-production", "cancel-in-progress": False}
    assert WORKFLOW["permissions"] == {"contents": "read", "actions": "read"}
    assert deploy["needs"] == "validate" and deploy["runs-on"] == "ubuntu-22.04"
    assert checkout["with"]["ref"] == "${{ github.sha }}" and checkout["with"]["persist-credentials"] is False
    assert action["run"] == "bash deploy/actions-ssh.sh"
    assert action["env"]["SSH_KEY"] == "${{ secrets.SSH_KEY }}"
    assert action["env"]["SSH_HOST_FINGERPRINT"] == gate["env"]["SSH_HOST_FINGERPRINT"]
    assert action["env"]["DEPLOY_COMMIT"] == "${{ github.sha }}"
    assert "reset --hard" not in remote and "git clean" not in remote
    assert "158.178.131.94" not in json.dumps(WORKFLOW)
    assert "flock -w 900 9" in remote
    lock_marker = "# BEGIN LOCK GUARD FUNCTIONS\n"
    lock_end = "# END LOCK GUARD FUNCTIONS"
    script = (BASE / "deploy/deploy-production.sh").read_text(encoding="utf-8")
    assert remote.split(lock_marker, 1)[1].split(lock_end, 1)[0] == script.split(lock_marker, 1)[1].split(lock_end, 1)[0]
    assert "prepare_deploy_lock ||" in remote and "verify_deploy_lock_fd ||" in remote
    passed("workflow main/SHA/concurrency/new host/mandatory fingerprint/least permissions")

    # The real authorization is dispatch-only and checked again against a root-owned server record.
    for name, requested, fingerprint, until, success in [
        ("exact SHA absent", "", "SHA256:" + "A" * 43, "2026-10-11T23:38:00Z", False),
        ("exact SHA differs", "2" * 40, "SHA256:" + "A" * 43, "2026-10-11T23:38:00Z", False),
        ("fingerprint absent", "1" * 40, "", "2026-10-11T23:38:00Z", False),
        ("fingerprint malformed", "1" * 40, "untrusted", "2026-10-11T23:38:00Z", False),
        ("explicit bounded dispatch", "1" * 40, "SHA256:PY1p4k7jJ1WXl+Mz2ze1wUFQX9oEfM4YKsBAO6gRKws", "2026-10-11T23:38:00Z", True),
    ]:
        env = os.environ.copy()
        env.update(DEPLOY_COMMIT="1" * 40,REQUESTED_COMMIT=requested,TRANSITION_RUN_ID="lf-cutover-synthetic",AUTHORIZED_UNTIL_UTC=until,SSH_HOST_FINGERPRINT=fingerprint)
        # A fixture clock makes this policy test independent of real authorization expiry.
        clock="date() { if [[ \"$*\" == *'-d '* ]]; then printf '7200\\n'; else printf '0\\n'; fi; }\n"
        result = subprocess.run([BASH, "-c", clock+gate["run"]], env=env, capture_output=True)
        assert (result.returncode == 0) == success
        passed(name)

    # Extract ONLY the two Git functions. Never execute the remote wrapper, fetch, or deployment.
    functions = remote.split("# BEGIN SAFE CHECKOUT FUNCTIONS\n", 1)[1].split("# END SAFE CHECKOUT FUNCTIONS", 1)[0]
    history = git("rev-list", "HEAD", "--max-count=100").decode().splitlines()
    pair = None
    for commit in history:
        parents = git("rev-list", "--parents", "-n", "1", commit).decode().split()
        if len(parents) != 2:
            continue
        added = git("diff-tree", "--no-commit-id", "--diff-filter=A", "--name-only", "-r", "-z", commit).decode().split("\0")
        path = next((p for p in added if p.endswith(".cs")), None)
        if path:
            pair = (parents[1], commit, path)
            break
    assert pair, "History is required for local Git fixtures (checkout fetch-depth: 0)"
    parent, candidate, added_path = pair
    for mode in ("clean", "tracked", "staged", "untracked", "ignored-collision", "ignored-unrelated", "unfinished", "invalid-sha", "outside-main"):
        fixture = ROOT / mode
        git("clone", "--local", "--no-hardlinks", "--no-checkout", str(BASE), str(fixture), cwd=ROOT)
        # Actions checks out a detached SHA; a local clone then has no origin/main.
        # Define only this synthetic fixture's accepted main tip, without modifying the source repository.
        git("update-ref", "refs/remotes/origin/main", initial_head.decode().strip(), cwd=fixture)
        git("-c", "core.hooksPath=/dev/null", "checkout", "--detach", parent, cwd=fixture)
        tracked = next(p for p in git("ls-tree", "-r", "--name-only", parent, cwd=fixture).decode().splitlines() if p.endswith(".cs"))
        sentinel = None
        requested = candidate
        if mode in ("tracked", "staged"):
            sentinel = fixture / tracked
            sentinel.write_bytes(sentinel.read_bytes() + b"\n// unsaved synthetic fixture\n")
            if mode == "staged":
                git("add", "--", tracked, cwd=fixture)
        elif mode in ("untracked", "ignored-collision", "ignored-unrelated"):
            name = added_path if mode == "ignored-collision" else "private synthetic file.txt"
            sentinel = fixture / name
            sentinel.parent.mkdir(parents=True, exist_ok=True)
            assert not sentinel.exists()
            sentinel.write_bytes(b"preserve synthetic file\n")
            if mode.startswith("ignored"):
                with (fixture / ".git/info/exclude").open("a", encoding="utf-8") as stream:
                    stream.write("\n/" + name + "\n")
        elif mode == "unfinished":
            sentinel = fixture / ".git/MERGE_HEAD"
            sentinel.write_text(parent + "\n")
        elif mode == "invalid-sha":
            requested = "main"
        elif mode == "outside-main":
            git("update-ref", "refs/remotes/origin/main", parent, cwd=fixture)
        before_head = git("rev-parse", "HEAD", cwd=fixture)
        before_hash = hashlib.sha256(sentinel.read_bytes()).hexdigest() if sentinel else None
        command = "set +x\nset -Eeuo pipefail\nexport PATH=/usr/bin:/bin:$PATH\ncd " + shlex.quote(posix(fixture)) + "\n" + functions + "\ncheckout_candidate " + shlex.quote(requested)
        result = subprocess.run([BASH, "-c", command], capture_output=True)
        success = mode in ("clean", "ignored-unrelated")
        assert (result.returncode == 0) == success, mode + ": " + result.stderr.decode(errors="replace")
        assert git("rev-parse", "HEAD", cwd=fixture) == (candidate.encode() + b"\n" if success else before_head)
        if sentinel:
            assert before_hash == hashlib.sha256(sentinel.read_bytes()).hexdigest()
        if not success:
            # Git may refresh index timestamps; compare its staged tree instead of byte metadata.
            assert git("diff", "--cached", "--name-only", cwd=fixture).decode().strip() == (tracked if mode == "staged" else "")
        passed("real Git " + mode + " preserves user files and selects exact SHA")
    assert initial_head == git("rev-parse", "HEAD") and initial_status == git("status", "--porcelain", "--untracked-files=all")
    print("RECOVERY_SAFETY_RESULT: " + str(COUNT) + "/" + str(COUNT) + " passed; fixtures retained in " + str(ROOT), flush=True)


if __name__ == "__main__":
    main()
