#!/usr/bin/env python3
"""A STUB sign-in driver for the actuator harness. It never opens a socket, never touches a
browser and never contacts Google. It exists to (1) record exactly what the actuator handed
it, through every channel, and (2) answer with a chosen verdict, so the ACTUATOR's handling
of each verdict is what gets tested.

Reads $GV_STUB_DIR/driver/mode (one line) and behaves accordingly:
    word:<W>        print W as the only stdout line, exit 0
    noisy:<W>       print two noise lines, then W, exit 0
    nonzero:<W>     print W, exit 1
    crash           raise (traceback on stderr, exit 1), after recording
    garbage         print "OK signed in!" and exit 0
    silent          print nothing, exit 0
    sleep           sleep 30, then print SIGNED_IN (for the timeout case)
    slow:<W>        sleep 3, then print W (for the two-runs-at-once case)
    daemon:<W>      leave a 6 s child holding inherited fds, then print W
    leak:<W>        echo the email and password it received to STDERR, then print W

Records under $GV_STUB_DIR/driver/:
    runs.log        one line per invocation
    stdin.bin       the bytes received on stdin (the harness's FIXTURE credential)
    argv.txt        its own argv
    environ.txt     its own environment, NUL-separated
    ancestors.txt   /proc/<pid>/cmdline of every ancestor up to PID 1, one per line,
                    and ancestors.environ (NUL-separated environments of the same processes)

⚠ The ancestor walk is taken WHILE THE DRIVER IS RUNNING — the one moment the credential
is in flight — so it is a deterministic observation, not the /proc sampling that plan §0.3
rejected (a sampler that happens to look at the wrong moment passes by missing it).
"""
import os
import sys
import time
from pathlib import Path

D = Path(os.environ["GV_STUB_DIR"]) / "driver"


def ancestors():
    pid, cmdlines, environs = os.getppid(), [], []
    while pid > 1:
        try:
            cmdlines.append(Path(f"/proc/{pid}/cmdline").read_bytes().replace(b"\0", b" "))
            try:
                environs.append(Path(f"/proc/{pid}/environ").read_bytes())
            except OSError:
                environs.append(b"")
            stat = Path(f"/proc/{pid}/stat").read_text()
            pid = int(stat.rsplit(")", 1)[1].split()[1])
        except (OSError, ValueError, IndexError):
            break
    return cmdlines, environs


def main():
    D.mkdir(parents=True, exist_ok=True)
    data = sys.stdin.buffer.read()
    with open(D / "runs.log", "a") as fh:
        fh.write(f"run pid={os.getpid()}\n")
    (D / "stdin.bin").write_bytes(data)
    (D / "argv.txt").write_bytes(Path("/proc/self/cmdline").read_bytes().replace(b"\0", b" "))
    (D / "environ.txt").write_bytes(Path("/proc/self/environ").read_bytes())
    cl, env = ancestors()
    (D / "ancestors.txt").write_bytes(b"\n".join(cl))
    (D / "ancestors.environ").write_bytes(b"\n".join(env))

    try:
        mode = (D / "mode").read_text().strip()
    except OSError:
        mode = "word:SIGNED_IN"
    kind, _, word = mode.partition(":")
    if kind == "word":
        print(word)
        return 0
    if kind == "noisy":
        print("step 1 done")
        print("step 2 done")
        print(word)
        return 0
    if kind == "nonzero":
        print(word)
        return 1
    if kind == "crash":
        raise RuntimeError("stub driver crashed on purpose")
    if kind == "garbage":
        print("OK signed in!")
        return 0
    if kind == "silent":
        return 0
    if kind == "sleep":
        time.sleep(30)
        print("SIGNED_IN")
        return 0
    if kind == "slow":
        time.sleep(3)
        print(word)
        return 0
    if kind == "daemon":
        # Leave a child behind that keeps every INHERITED descriptor except stdio — which
        # is how a driver that spawns a helper would hold the actuator's lock (fd 8) after
        # the actuator exits, unless the actuator closed it for the driver.
        if os.fork() == 0:
            os.close(0); os.close(1); os.close(2)
            time.sleep(6)
            os._exit(0)
        print(word)
        return 0
    if kind == "leak":
        fields = dict(l.split("=", 1) for l in data.decode().splitlines() if "=" in l)
        print(f"debug: email={fields.get('email')} password={fields.get('password')}", file=sys.stderr)
        print(word)
        return 0
    print("UNRECOGNISED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
