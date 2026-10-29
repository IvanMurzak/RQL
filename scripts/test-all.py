#!/usr/bin/env python3
"""Build and test every runtime against the shared golden vectors. Exit non-zero on any failure.

Usage: python3 scripts/test-all.py   (Windows: python scripts\\test-all.py)

Stdlib only, Python 3.9+. Runs on Linux, macOS and Windows: no shell, no PowerShell. Exit codes: 0 all green,
1 a step failed (its name is printed), 2 a required tool is not on PATH.
"""
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CPP_BUILD = os.path.join(ROOT, "cpp", "build")


def tool(name):
    path = shutil.which(name)  # honours PATHEXT, so `npm` finds npm.cmd on Windows
    if not path:
        print("test-all: '%s' is not on PATH" % name, file=sys.stderr)
        sys.exit(2)
    return path


def run(step, args, cwd=None, quiet=False):
    sys.stdout.flush()
    rc = subprocess.run(args, cwd=cwd, stdout=subprocess.DEVNULL if quiet else None).returncode
    if rc != 0:
        print("test-all: FAILED (exit %d): %s" % (rc, step), file=sys.stderr)
        sys.exit(1)


def cpp_conformance_exe():
    # Single-config generators (Makefiles, Ninja) put it in build/; multi-config ones (Visual Studio, Xcode) in
    # build/Release/.
    name = "rql_conformance.exe" if os.name == "nt" else "rql_conformance"
    for path in (os.path.join(CPP_BUILD, name), os.path.join(CPP_BUILD, "Release", name)):
        if os.path.isfile(path):
            return path
    print("test-all: FAILED: %s not built under %s" % (name, CPP_BUILD), file=sys.stderr)
    sys.exit(1)


def main():
    home_dotnet = os.path.join(os.path.expanduser("~"), ".dotnet")
    if not os.environ.get("DOTNET_ROOT") and os.access(
        os.path.join(home_dotnet, "dotnet.exe" if os.name == "nt" else "dotnet"), os.X_OK
    ):
        os.environ["DOTNET_ROOT"] = home_dotnet
        os.environ["PATH"] = home_dotnet + os.pathsep + os.environ.get("PATH", "")
    dotnet, npm, cmake, ctest = tool("dotnet"), tool("npm"), tool("cmake"), tool("ctest")

    print("== .NET (unit + conformance, compiled and reflection accessors)")
    run(".NET tests", [dotnet, "test", os.path.join(ROOT, "dotnet", "Rql.slnx"), "--nologo", "-v", "q"])
    print("== .NET conformance runner")
    run(".NET conformance", [dotnet, "run", "--project", os.path.join(ROOT, "dotnet", "Rql.Conformance"), "-v", "q", "--", "check"])

    print("== TypeScript")
    ts = os.path.join(ROOT, "typescript")
    if not os.path.isdir(os.path.join(ts, "node_modules")):
        run("npm install", [npm, "install", "--no-audit", "--no-fund"], cwd=ts)
    run("TypeScript tests", [npm, "test"], cwd=ts)

    print("== C++")
    run("C++ configure", [cmake, "-S", os.path.join(ROOT, "cpp"), "-B", CPP_BUILD, "-DCMAKE_BUILD_TYPE=Release"], quiet=True)
    run("C++ build", [cmake, "--build", CPP_BUILD, "--config", "Release", "-j", "8"])
    run("C++ tests", [ctest, "--test-dir", CPP_BUILD, "-C", "Release", "--output-on-failure"])
    run("C++ conformance", [cpp_conformance_exe(), ROOT])

    print("== Task catalog self-check (reference agent, no LLM)")
    run("task self-check", [dotnet, "run", "--project", os.path.join(ROOT, "evaluation", "runner", "Rql.Eval"), "-v", "q", "--",
                            "reference", "--split", "all"])
    print("ALL GREEN")


if __name__ == "__main__":
    main()
