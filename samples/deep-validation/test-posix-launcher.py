"""Verify native POSIX launch, argument forwarding, and exact exit status."""

import errno
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ARGS = ("run", "--project", "samples/deep-validation/TypeSharp.DeepValidation.csproj")
STATUSES = (0, 1, 7)


def check_call(command, cwd, env, trace, expected_cwd, expected_args, status):
    try:
        result = subprocess.run(
            command, cwd=cwd, env=env, shell=False, capture_output=True, timeout=10
        )
    except OSError as error:
        name = errno.errorcode.get(error.errno, str(error.errno))
        raise RuntimeError(
            f"{command!r}: expected exit {status}; launch failed with "
            f"{type(error).__name__} ({name}); dotnet trace present={trace.exists()}"
        ) from error
    expected = b"\0".join(map(os.fsencode, (expected_cwd, *expected_args))) + b"\0"
    observed = trace.read_bytes() if trace.exists() else None
    if (result.returncode, observed) != (status, expected):
        raise RuntimeError(
            f"{command!r}: expected exit {status} and trace {expected!r}; "
            f"got exit {result.returncode} and trace {observed!r}; "
            f"stdout={result.stdout!r}, stderr={result.stderr!r}"
        )


def verify(launcher, *, baseline=False):
    root = launcher.parents[2]
    with tempfile.TemporaryDirectory(prefix="posix launcher ") as directory:
        temp = Path(directory).resolve()
        private_bin = temp / "bin"
        private_bin.mkdir()
        for name in ("bash", "dirname"):
            found = shutil.which(name)
            if found is None:
                raise RuntimeError(f"Required tool not found: {name}")
            (private_bin / name).symlink_to(Path(found).resolve(strict=True))
        stub = private_bin / "dotnet"
        stub.write_text(
            "#!/bin/sh\n"
            "printf '%s\\0' \"$(pwd -P)\" \"$@\" >> \"$TEST_TRACE\" || exit 125\n"
            "exit \"$TEST_EXIT\"\n",
            encoding="ascii",
        )
        stub.chmod(0o755)

        def run(label, command, status, expected_cwd, expected_args):
            trace = temp / f"{label}-{status}.trace"
            env = {
                "PATH": str(private_bin),
                "TEST_TRACE": str(trace),
                "TEST_EXIT": str(status),
            }
            check_call(command, temp, env, trace, expected_cwd, expected_args, status)
            if baseline:
                print(f"{label} exit={status} exact single NUL trace={trace.read_bytes()!r}")

        run("positive", [str(stub), "mount control"], 0, temp, ("mount control",))
        if baseline:
            for status in STATUSES:
                run("explicit-bash", [str(private_bin / "bash"), str(launcher)],
                    status, root, ARGS)
            try:
                run("native", [str(launcher)], 0, root, ARGS)
            except RuntimeError as error:
                if (
                    isinstance(error.__cause__, PermissionError)
                    and error.__cause__.errno == errno.EACCES
                    and not (temp / "native-0.trace").exists()
                ):
                    print(f"RED: {error}")
                    return
                raise
            raise RuntimeError("Baseline unexpectedly launched natively")
        for status in STATUSES:
            run("native", [str(launcher)], status, root, ARGS)


if __name__ == "__main__":
    try:
        verify(Path(__file__).resolve().with_name("run.sh"))
    except (RuntimeError, OSError, subprocess.TimeoutExpired) as error:
        sys.exit(f"POSIX launcher FAIL: {error}")
    print("POSIX launcher PASS (exit 0/1/7, exact cwd and argv)")
