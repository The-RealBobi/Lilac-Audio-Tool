#!/usr/bin/env python3
from __future__ import annotations

import argparse
import shutil
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
GUI_PROJECT = ROOT / "AudioTool.Gui" / "AudioTool.Gui.csproj"
HCA_TOOL_PROJECT = ROOT / "tools" / "CriHcaTool" / "CriHcaTool.csproj"


def run(command: list[str]) -> None:
    subprocess.run(command, cwd=ROOT, check=True)


def publish(runtime: str, configuration: str, self_contained: bool) -> Path:
    output = ROOT / "dist" / runtime
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)

    command = [
        "dotnet",
        "publish",
        str(GUI_PROJECT),
        "-c",
        configuration,
        "-r",
        runtime,
        "--self-contained",
        "true" if self_contained else "false",
        "-o",
        str(output),
        "-p:PublishSingleFile=false",
    ]
    run(command)
    publish_hca_tool(output, runtime, configuration)
    return output


def publish_hca_tool(output: Path, runtime: str, configuration: str) -> None:
    helper_output = output / "tools" / "CriHcaToolRuntime"
    if helper_output.exists():
        shutil.rmtree(helper_output)
    helper_output.mkdir(parents=True)

    run([
        "dotnet",
        "publish",
        str(HCA_TOOL_PROJECT),
        "-c",
        configuration,
        "-r",
        runtime,
        "--self-contained",
        "true",
        "-o",
        str(helper_output),
        "-p:PublishSingleFile=false",
    ])


def copy_readme(output: Path) -> None:
    readme = ROOT / "README.md"
    if readme.exists():
        shutil.copy2(readme, output / "README.md")


def main() -> None:
    parser = argparse.ArgumentParser(description="Publish Lilac Audio Tool into a redistributable folder.")
    parser.add_argument("--runtime", required=True, help="RID such as osx-arm64, win-x64, linux-x64.")
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--framework-dependent", action="store_true", help="Do not bundle the .NET runtime.")
    args = parser.parse_args()

    output = publish(args.runtime, args.configuration, not args.framework_dependent)
    copy_readme(output)
    print(output)


if __name__ == "__main__":
    main()
