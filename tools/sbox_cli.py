"""Project-bound sbox-cli access. Attach only; never launch or stop a user's editor.

This file is shared with the other MiniBox projects. No third-party dependency.
The engine's installed --skill and references/protocol.md describe the transport.
"""
from __future__ import annotations

import argparse
import base64
from collections import deque
import json
import hashlib
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import time

sys.dont_write_bytecode = True


def find_cli() -> Path:
    explicit = os.environ.get("SBOX_CLI")
    if explicit:
        path = Path(explicit).expanduser()
        if not path.is_file():
            raise FileNotFoundError(f"SBOX_CLI does not exist: {path}")
        return path
    engines = [os.environ.get("FACEPUNCH_ENGINE"), os.environ.get("SBOX_ROOT")]
    if os.name == "nt":
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
                engines.append(winreg.QueryValueEx(key, "FACEPUNCH_ENGINE")[0])
        except OSError:
            pass
    executable = shutil.which("sbox-cli")
    candidates = ([Path(executable)] if executable else [])
    candidates += [Path(p) / "bin/managed/sbox-cli.exe" for p in engines if p]
    candidates.append(Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)"))
                      / "Steam/steamapps/common/sbox/bin/managed/sbox-cli.exe")
    for path in candidates:
        if path.is_file():
            return path
    raise FileNotFoundError("sbox-cli not found. Set FACEPUNCH_ENGINE to the engine folder or SBOX_CLI to its executable.")


def project_file(value: str | Path) -> Path:
    path = Path(value).expanduser().resolve()
    if path.is_file() and path.suffix.lower() == ".sbproj":
        return path
    if path.is_dir():
        files = list(path.glob("*.sbproj"))
        if len(files) == 1:
            return files[0]
        raise ValueError(f"Expected exactly one .sbproj in {path}; pass the specific .sbproj file.")
    raise FileNotFoundError(f"s&box project not found: {path}")


def same_path(a: str | Path, b: str | Path) -> bool:
    return os.path.normcase(os.path.realpath(a)) == os.path.normcase(os.path.realpath(b))


def project_from_directory(start: str | Path | None = None) -> Path:
    directory = Path(start or Path.cwd()).resolve()
    for candidate in (directory, *directory.parents):
        if list(candidate.glob("*.sbproj")):
            return project_file(candidate)
    raise ValueError("No s&box project in this directory or its parents; pass --project explicitly.")


def read_result(result: dict):
    if result.get("structuredContent") is not None:
        return result["structuredContent"]
    texts = [c.get("text", "") for c in result.get("content", []) if c.get("type") == "text"]
    if len(texts) == 1:
        try:
            return json.loads(texts[0])
        except ValueError:
            pass
    return "\n".join(texts)


def save_images(result: dict, directory: Path) -> list[str]:
    """Keep image tool results usable outside an MCP client, as the native --call does."""
    paths = []
    for block in result.get("content", []):
        if block.get("type") != "image":
            continue
        data = base64.b64decode(block["data"], validate=True)
        extension = {"image/png": ".png", "image/jpeg": ".jpg", "image/webp": ".webp"}.get(block.get("mimeType"), ".bin")
        directory.mkdir(parents=True, exist_ok=True)
        path = directory / (hashlib.sha256(data).hexdigest()[:20] + extension)
        path.write_bytes(data)
        paths.append(str(path.resolve()))
    return paths


class CliSession:
    """One serialized MCP connection, with identity checks before any tool call."""
    def __init__(self, project: str | Path, timeout: float = 60, *, cli_command=None, launch=False):
        self.project = project_file(project)
        self.timeout = timeout
        self.command = list(cli_command) if cli_command else [str(find_cli())]
        self.launch = launch  # Only explicit QA callers may opt into ownership.
        self.process = None
        self.responses = queue.Queue()
        self.diagnostics = deque(maxlen=50)
        self.request_id = 0
        self.meta = {}
        self.initial = {}
        self.status = {}
        self.threads = []

    def __enter__(self):
        mode = "--project" if self.launch else "--connect"
        command = [*self.command, mode, str(self.project), "--request-timeout", str(max(1, int(self.timeout)))]
        self.process = subprocess.Popen(command, cwd=self.project.parent, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        self.identity = {"pid": self.process.pid, "started": time.time(), "command": command}
        def reader():
            try:
                for line in self.process.stdout:
                    self.responses.put(line)
            finally:
                self.responses.put(None)
        def errors():
            for line in self.process.stderr:
                self.diagnostics.append(line.decode("utf-8", errors="replace").rstrip())
        self.threads = [threading.Thread(target=reader, daemon=True), threading.Thread(target=errors, daemon=True)]
        for thread in self.threads:
            thread.start()
        try:
            self.initial = self.request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                        "clientInfo": {"name": "minibox-project-tools", "version": "2.0"}})
            self.meta = self.initial.get("_meta", {})
            actual = self.meta.get("project")
            if self.meta.get("kind") != "editor" or not actual or not same_path(actual, self.project):
                raise RuntimeError(f"Wrong instance: expected editor {self.project}, received {self.meta}")
            self.notify("notifications/initialized")
            self.entry_points = self.request("tools/list", {})
            self.status = read_result(self._tool("editor_status", {}))
            root = self.status.get("Paths", {}).get("ProjectRoot") if isinstance(self.status, dict) else None
            if not root or not same_path(root, self.project.parent):
                raise RuntimeError(f"Wrong project root: expected {self.project.parent}, received {root}")
            return self
        except BaseException:
            self.close()
            raise

    def notify(self, method, params=None):
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        self._write(message)

    def _write(self, message):
        if not self.process or self.process.poll() is not None:
            raise RuntimeError("sbox-cli disconnected: " + "\n".join(self.diagnostics))
        data = (json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        if len(data) > 8 * 1024 * 1024:
            raise ValueError("MCP request exceeds the engine's 8 MiB limit.")
        self.process.stdin.write(data)
        self.process.stdin.flush()

    def request(self, method, params):
        self.request_id += 1
        request_id = self.request_id
        self._write({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
        deadline = time.monotonic() + self.timeout
        while True:
            try:
                line = self.responses.get(timeout=max(0, deadline - time.monotonic()))
            except queue.Empty:
                raise TimeoutError(f"sbox-cli timed out during {method}; inspect state before retrying a mutation.") from None
            if line is None:
                raise RuntimeError("sbox-cli disconnected: " + "\n".join(self.diagnostics))
            message = json.loads(line.decode("utf-8-sig"))
            if message.get("error") and message.get("id") is None:
                raise RuntimeError(json.dumps(message["error"], ensure_ascii=False))
            if message.get("id") != request_id:
                continue
            if message.get("error"):
                raise RuntimeError(json.dumps(message["error"], ensure_ascii=False))
            if "result" not in message:
                raise RuntimeError("MCP response has no result.")
            return message["result"]

    def _tool(self, name, arguments):
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        if result.get("isError"):
            raise RuntimeError(str(read_result(result)))
        return result

    def call(self, name: str, arguments=None):
        return self._tool(name, arguments or {})

    def close(self):
        if not self.process:
            return
        try:
            if not self.process.stdin.closed:
                self.process.stdin.close()
            try:
                self.process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                self.process.terminate()  # This exact child handle; never the attached editor.
                self.process.wait(timeout=5)
        finally:
            for thread in self.threads:
                thread.join(timeout=2)
            self.process.stdout.close()
            self.process.stderr.close()
            # An empty exchange directory belongs to this connection. Preserve actual outputs.
            exchange = self.meta.get("exchangeDirectory")
            if exchange:
                try:
                    Path(exchange).rmdir()
                except OSError:
                    pass

    def __exit__(self, *_):
        self.close()


def call_tool(project, name, arguments=None, timeout=60):
    with CliSession(project, timeout) as session:
        return session.call(name, arguments)


def main():
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", help="Exact .sbproj or directory containing one project")
    parser.add_argument("--stdio", action="store_true", help="Expose the native MCP connection to an agent")
    parser.add_argument("--list", action="store_true")
    parser.add_argument("--skill", nargs="?", const="sbox-cli", help="Read the installed engine skill or a named reference")
    parser.add_argument("--call")
    parser.add_argument("--args", default="{}")
    parser.add_argument("--args-file", help="UTF-8 JSON arguments, avoids PowerShell 5.1 quoting loss")
    parser.add_argument("--raw", action="store_true", help="Return the JSON-RPC result envelope")
    parser.add_argument("--output-dir", help="Keep image results here (default: the project's tmp/sbox-cli)")
    parser.add_argument("--timeout", type=float, default=60)
    args = parser.parse_args()
    if args.stdio or args.list or args.skill:
        command = [str(find_cli())]
        if args.stdio:
            project = project_file(args.project) if args.project else project_from_directory()
            command += ["--connect", str(project)]
        elif args.list:
            command.append("--list")
        else:
            command += ["--skill", args.skill]
        # The inherited stdin reaches EOF when the MCP client disconnects.
        process = subprocess.Popen(command, stdin=sys.stdin.buffer, stdout=sys.stdout.buffer,
                                   stderr=sys.stderr.buffer,
                                   creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        try:
            return process.wait()
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=5)
    if not args.project or not args.call:
        parser.error("--call requires --project; this helper never selects an unrelated editor")
    arguments = json.loads(Path(args.args_file).read_text(encoding="utf-8-sig") if args.args_file else args.args)
    if not isinstance(arguments, dict):
        parser.error("tool arguments must be a JSON object")
    try:
        result = call_tool(args.project, args.call, arguments, args.timeout)
        value = {"jsonrpc": "2.0", "id": 1, "result": result} if args.raw else read_result(result)
        if not args.raw:
            directory = Path(args.output_dir) if args.output_dir else project_file(args.project).parent / "tmp/sbox-cli"
            images = save_images(result, directory)
            if images:
                value = {"result": value, "images": images}
        print(value if isinstance(value, str) else json.dumps(value, ensure_ascii=False, indent=2))
        return 0
    except (OSError, ValueError, RuntimeError, TimeoutError) as error:
        print(str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
