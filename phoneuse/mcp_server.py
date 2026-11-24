"""MCP transports for local Phone Use commands."""

import base64
import json
import math
import os
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from . import commands


PROTOCOLS = ("2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05")
MCP_PORT = 8779
COMMAND_TIMEOUT = 300
STEPS_TIMEOUT = 60
MAX_STEPS = 4
STEP_STATUSES = {
    "open_app": {"ok"},
    "tap_on": {"tapped", "confirmed"},
    "scroll_by": {"ok"},
    "type_text": {"ok"},
    "press_key": {"ok"},
    "wait_for": {"visible"},
    "look": {"ok"},
    "inspect_region": {"captured"},
}
OBSERVATION_TOOLS = {"look", "inspect_region"}
INPUT_TOOLS = {"open_app", "tap_on", "scroll_by", "type_text", "press_key"}
RUN_STEPS = {
    "name": "run_steps",
    "description": (
        f"Prefer batching 1–{MAX_STEPS} steps within {STEPS_TIMEOUT} seconds when you can supply their usual arguments up front. "
        "For example, open an app or scroll, then look. The screen need not be visible in advance: "
        "tap_on finds a live label, expect checks the transition, and wait_for waits for a label. "
        "Use look or inspect_region only as the last step. "
        "Returns brief step statuses and the final result. Stops on failure or uncertainty. "
        "Use individual calls when you need to inspect a result or get owner approval before continuing."
    ),
    "inputSchema": {
        "type": "object", "required": ["steps"], "additionalProperties": False,
        "properties": {"steps": {
            "type": "array", "minItems": 1, "maxItems": MAX_STEPS,
            "items": {
                "type": "object", "required": ["name"], "additionalProperties": False,
                "properties": {"name": {"type": "string", "enum": list(STEP_STATUSES)},
                               "arguments": {"type": "object"}},
            },
        }},
    },
}
INSTRUCTIONS = (
    "Use Phone Use's MCP tools to operate the iPhone. Call status and show_mirror before acting. "
    "On Windows, show_mirror prefers the native WinUI window. If the MCP server is down, "
    "open Phone Use from Start or launch windows/dist/PhoneUseMirror.exe from a checkout. "
    "If the link drops, check status. Use reconnect_bluetooth once when Bluetooth drops. "
    "If the link stays down or Screen Mirroring stops, ask the owner to follow the recovery steps. "
    "If the phone is locked, use request_unlock and follow its result. For manual unlock, ask the owner, "
    "call wait_for_unlock once, and stop tool calls until it returns. Resume only on unlocked. "
    "On timeout, wait for the owner before retrying. Never ask for or enter a passcode. "
    "Wait for the owner to restore access after a denial or takeover. Never approve prompts on their behalf. "
    "Get owner authorization before sending, posting, buying, deleting, or changing account or security settings. "
    "Use only task-relevant phone content. Avoid tapping the Dynamic Island, where Screen Mirroring can stop. "
    "Inspect before retrying uncertain input. Call end_session when finished."
)


def invoke(command, arguments, artifact=None, job=None, *, timeout=COMMAND_TIMEOUT):
    args = command.arguments(arguments, artifact)
    root = Path(__file__).resolve().parent.parent
    launcher = root / "bin" / "phoneuse"
    entry = [str(launcher)] if launcher.is_file() else ["-m", "phoneuse.cli"]
    argv = [sys.executable, *entry, *args]
    popen = job.popen if job else subprocess.Popen
    with popen(argv, cwd=root, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
               encoding="utf-8", errors="replace", env={**os.environ, "PYTHONUTF8": "1"}) as process:
        try:
            stdout, stderr = process.communicate(timeout=timeout)
        except subprocess.TimeoutExpired:
            process.kill()
            process.communicate()
            raise
        return subprocess.CompletedProcess(argv, process.returncode, stdout, stderr)


def valid_value(rule, value):
    kind = rule["type"]
    if kind == "string":
        return (isinstance(value, str) and len(value) >= rule.get("minLength", 0)
                and ("enum" not in rule or value in rule["enum"])
                and ("pattern" not in rule or re.fullmatch(rule["pattern"], value) is not None))
    if kind == "boolean":
        return isinstance(value, bool)
    if kind == "array":
        return (isinstance(value, list) and rule["minItems"] <= len(value) <= rule["maxItems"]
                and all(valid_value(rule["items"], item) for item in value))
    types = {int} if kind == "integer" else {int, float}
    return (type(value) in types and math.isfinite(value)
            and rule.get("minimum", -math.inf) <= value <= rule.get("maximum", math.inf)
            and value > rule.get("exclusiveMinimum", -math.inf))


def validate(command, args):
    if not isinstance(args, dict) or set(args) - command.properties.keys():
        raise ValueError("Unexpected tool arguments")
    for name in command.positional:
        if name not in args:
            raise ValueError(f"Missing required argument: {name}")
    for name, value in args.items():
        if not valid_value(command.properties[name], value):
            raise ValueError(f"Invalid value for {name}")
    if "region" in args:
        commands.check_region(args["region"])


def tool_result(text, error=False, **extra):
    return {"isError": error, "content": [{"type": "text", "text": text}], **extra}


def call_tool(name, args, job=None, *, timeout=COMMAND_TIMEOUT):
    if name == "run_steps":
        return run_steps(args, job)
    command = commands.TOOLS.get(name)
    if not command:
        return tool_result("Unknown PhoneUse tool", error=True)
    try:
        validate(command, args)
        if name == "wait_for_unlock":
            # Bound even an unresponsive screen read, with room for CLI startup and cleanup.
            timeout = min(timeout, args.get("timeout", commands.UNLOCK_WAIT_DEFAULT) + 4)
        with tempfile.TemporaryDirectory() as folder:
            artifact = Path(folder) / "crop.png" if command.output == "png" else None
            reply = invoke(command, args, artifact, job, timeout=timeout)
            text = reply.stdout.strip() or reply.stderr.strip() or "PhoneUse returned no output."
            extra = {}
            if command.output in {"json", "png"} and reply.stdout.strip():
                details = json.loads(reply.stdout)
                text = json.dumps(details, ensure_ascii=False, separators=(",", ":"))
                extra["structuredContent"] = details
            result = tool_result(text, error=reply.returncode != 0, **extra)
            if artifact is not None and reply.returncode == 0:
                result["content"].append({"type": "image", "mimeType": "image/png",
                                          "data": base64.b64encode(artifact.read_bytes()).decode("ascii")})
            return result
    except subprocess.TimeoutExpired:
        if name == "wait_for_unlock":
            return tool_result('{"status":"timeout"}', error=True, structuredContent={"status": "timeout"})
        return tool_result("PhoneUse timed out. Input may have been sent. Inspect before retrying.", error=True)
    except (ValueError, OSError, OverflowError) as e:
        return tool_result(str(e), error=True)


def validate_steps(args):
    if not isinstance(args, dict) or set(args) != {"steps"}:
        raise ValueError("Supply a steps array")
    steps = args["steps"]
    if not isinstance(steps, list) or not 1 <= len(steps) <= MAX_STEPS:
        raise ValueError(f"Supply 1 to {MAX_STEPS} steps")
    validated = []
    for index, step in enumerate(steps, start=1):
        try:
            if not isinstance(step, dict) or step.keys() - {"name", "arguments"}:
                raise ValueError("Supply a tool name and optional arguments")
            name = step.get("name")
            if not isinstance(name, str) or name not in STEP_STATUSES:
                raise ValueError("Tool is unavailable in run_steps")
            if name in OBSERVATION_TOOLS and index != len(steps):
                raise ValueError(f"{name} must be the last step")
            values = step.get("arguments", {})
            command = commands.TOOLS[name]
            validate(command, values)
            for key, value in values.items():
                if not isinstance(value, str):
                    continue
                invalid_label = name != "type_text" and (not value.strip() or "\r" in value or "\n" in value)
                if "\0" in value or invalid_label:
                    raise ValueError(f"Invalid value for {key}")
            if name == "inspect_region":
                commands.check_region(values[key] for key in ("left", "top", "right", "bottom"))
            command.arguments(values)  # Check CLI argument restrictions before sending any input.
        except (ValueError, OverflowError) as e:
            raise ValueError(f"Step {index}: {e}") from e
        validated.append((name, values))
    return validated


def run_steps(args, job=None):
    try:
        steps = validate_steps(args)
    except ValueError as e:
        return tool_result(str(e), error=True)

    deadline = time.monotonic() + STEPS_TIMEOUT
    summary = {"status": "completed", "steps": []}
    images = []
    for index, (name, values) in enumerate(steps, start=1):
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            summary.update(status="stopped", stopped_at=index,
                           result={"status": "deadline_exceeded", "input_sent": False})
            break

        reply = call_tool(name, values, job, timeout=remaining)
        details = reply.get("structuredContent")
        if isinstance(details, dict):
            status = details.get("status", "error")
        else:
            details = {"text": reply["content"][0]["text"]}
            status = "ok" if commands.TOOLS[name].output == "text" and not reply["isError"] else "error"
        step = {"name": name, "status": status}
        if name in INPUT_TOOLS:
            sent = True if commands.TOOLS[name].output == "text" and status == "ok" else None
            step["input_sent"] = details.get("input_sent", sent)
        summary["steps"].append(step)

        if reply["isError"] or status not in STEP_STATUSES[name]:
            summary.update(status="stopped", stopped_at=index, result=details)
            break
        if index == len(steps):
            summary["result"] = details
            images = reply["content"][1:]

    text = json.dumps(summary, ensure_ascii=False, separators=(",", ":"))
    result = tool_result(text, error=summary["status"] == "stopped", structuredContent=summary)
    result["content"].extend(images)
    return result


def dispatch(request, job=None):
    if not isinstance(request, dict) or request.get("jsonrpc") != "2.0" or not isinstance(request.get("method"), str):
        return {"jsonrpc": "2.0", "id": None, "error": {"code": -32600, "message": "Invalid request"}}
    if "id" not in request:
        return None  # initialized, cancelled and other notifications have no reply
    response = {"jsonrpc": "2.0", "id": request["id"]}
    method, params = request["method"], request.get("params", {})
    if not isinstance(params, dict):
        return {**response, "error": {"code": -32602, "message": "Invalid params"}}
    if method == "initialize":
        version = params.get("protocolVersion")
        result = {"protocolVersion": version if version in PROTOCOLS else PROTOCOLS[0],
                  "serverInfo": {"name": "phoneuse", "version": "1.0.0"}, "capabilities": {"tools": {}},
                  "instructions": INSTRUCTIONS}
    elif method == "ping":
        result = {}
    elif method == "tools/list":
        result = {"tools": [{key: item[key] for key in ("name", "description", "inputSchema")}
                            for item in commands.manifest()] + [RUN_STEPS]}
    elif method == "tools/call" and isinstance(params.get("name"), str):
        result = call_tool(params["name"], params.get("arguments", {}), job)
    else:
        return {**response, "error": {"code": -32601, "message": "Method not found"}}
    return {**response, "result": result}


def main():
    for line in sys.stdin.buffer:
        try:
            response = dispatch(json.loads(line))
        except (ValueError, UnicodeError):
            response = {"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Parse error"}}
        if response is not None:
            sys.stdout.buffer.write(json.dumps(response, ensure_ascii=False).encode("utf-8") + b"\n")
            sys.stdout.buffer.flush()


def http_app(job=None):
    """A stateless Streamable HTTP endpoint, accessible only to local MCP clients."""
    import asyncio
    from aiohttp import web

    tools_lock = asyncio.Lock()

    async def endpoint(request):
        port = request.transport.get_extra_info("sockname")[1]
        if request.host not in {f"127.0.0.1:{port}", f"localhost:{port}"} or "Origin" in request.headers:
            raise web.HTTPForbidden()
        if request.method != "POST":
            raise web.HTTPMethodNotAllowed(request.method, ["POST"])
        if request.content_type != "application/json":
            raise web.HTTPUnsupportedMediaType()
        try:
            message = await request.json()
        except ValueError:
            return web.json_response({"jsonrpc": "2.0", "id": None,
                                      "error": {"code": -32700, "message": "Parse error"}}, status=400)
        if isinstance(message, dict) and message.get("method") == "tools/call":
            async with tools_lock:
                response = await asyncio.to_thread(dispatch, message, job)
        else:
            response = dispatch(message, job)
        return web.Response(status=202) if response is None else web.json_response(response)

    app = web.Application()
    app.router.add_route("*", "/mcp", endpoint)
    return app


def serve_winui():
    """Serve until WinUI closes the inherited pipe, including on app crashes."""
    import threading
    from aiohttp import web
    from .windows.procs import Job

    def watch_winui():
        try:
            sys.stdin.buffer.read()
        finally:
            os._exit(0)  # Windows closes the job handle and stops any in-flight tool subprocess.

    job = Job()
    threading.Thread(target=watch_winui, daemon=True).start()
    web.run_app(http_app(job), host="127.0.0.1", port=MCP_PORT, print=None, access_log=None)


if __name__ == "__main__":
    if sys.argv[1:] == ["--winui"]:
        serve_winui()
    else:
        main()
