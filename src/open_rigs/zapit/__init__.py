"""open_rigs.zapit: the Zapit maths, free of hardware, Qt and sockets.

Licensed LGPL-2.1-only, upstream's terms (Zapit-Optostim/zapit): see LICENSE in this folder.
"""
from importlib.metadata import PackageNotFoundError, version as _version

try:
    __version__ = _version("open-rigs")
except PackageNotFoundError:  # a source checkout that is not installed
    __version__ = "0.1.0"


def _installed_commit() -> str | None:
    """The commit a git-installed package was built from (PEP 610 ``direct_url.json``), else None."""
    import json
    from importlib.metadata import distribution

    try:
        text = distribution("open-rigs").read_text("direct_url.json")
        commit = json.loads(text).get("vcs_info", {}).get("commit_id") if text else None
    except (PackageNotFoundError, ValueError, AttributeError):
        return None
    return commit[:12] if isinstance(commit, str) and commit else None


def source() -> str:
    """The code that ran, for the records it writes: ``<version>+g<commit12>[-dirty]``. The version is the
    distribution's without its local part. The commit comes from the install record when the package was installed
    from git (it has no .git beside it), else from the checkout that tracks this folder (-dirty for local edits); the version
    alone when neither is there."""
    import subprocess
    from pathlib import Path

    base = __version__.split("+")[0]
    commit = _installed_commit()
    if commit:
        return f"{base}+g{commit}"

    def git(*args):
        return subprocess.run(["git", "-C", str(Path(__file__).resolve().parent), *args], capture_output=True,
                              text=True, timeout=5, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    try:
        # Only a checkout that tracks this folder says which code ran (not an install inside another repo).
        if git("ls-files", "--error-unmatch", "__init__.py").returncode:
            return base
        head = git("rev-parse", "--short=12", "HEAD")
        if head.returncode:
            return base
        dirty = git("status", "--porcelain", "--untracked-files=no", "--", ".").stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return base
    return f"{base}+g{head.stdout.strip()}{'-dirty' if dirty else ''}"
