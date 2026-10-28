"""Explicit native-Linux PBX maintenance CLI; never enables a gateway/firewall.

Credentials never enter argv, stdout, stderr or exception messages. The caller
selects one trusted local image and one already-existing Docker PBX. No image
pull, arbitrary command, automatic migration, call or SMS action is provided.
"""
import json
import re
import sys


_TOKEN = re.compile(r"[A-Za-z0-9_-]{43}\Z")
_IMAGE = re.compile(r"sha256:[a-f0-9]{64}\Z")
_CONTAINER = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\Z")
_ERRORS = {
    "invalid": "Invalid explicit PBX application request or private state",
    "conflict": "PBX application source, target or operation changed; read state before continuing",
    "busy": "PBX application is busy or requires explicit recovery",
    "uncertain": "PBX application outcome requires inspection; do not replay",
}


def _parse(arguments):
    allowed = {"--action", "--store", "--base", "--container", "--image", "--restart-policy",
               "--revision", "--operation", "--confirm-restart", "--confirm-v2-management"}
    result = {}
    position = 0
    while position < len(arguments):
        key = arguments[position]
        if key not in allowed or key in result:
            raise ValueError()
        position += 1
        if key in ("--confirm-restart", "--confirm-v2-management"):
            result[key] = True
        else:
            if position >= len(arguments) or arguments[position].startswith("--"):
                raise ValueError()
            result[key] = arguments[position]
            position += 1
    action = result.get("--action")
    required = {"--action", "--base"}
    if action not in ("status", "overview"):
        required |= {"--store", "--container", "--image"}
        if not _CONTAINER.fullmatch(result.get("--container", "")) or not _IMAGE.fullmatch(result.get("--image", "")):
            raise ValueError()
    if action in ("prepare", "apply"):
        required.add("--revision")
        if not _TOKEN.fullmatch(result.get("--revision", "")):
            raise ValueError()
    if action == "prepare":
        # Preparation enrolls the target in the v2-only writer discipline,
        # deliberately leaving a permanent guard against legacy four-file apply.
        required.add("--confirm-v2-management")
    if action in ("apply", "recover"):
        required |= {"--operation", "--confirm-restart"}
        if not _TOKEN.fullmatch(result.get("--operation", "")):
            raise ValueError()
    elif action not in ("prepare", "status", "overview"):
        raise ValueError()
    optional = {"--restart-policy"} if action not in ("status", "overview") else set()
    if not required <= result.keys() or not result.keys() <= required | optional:
        raise ValueError()
    for key in ("--base", "--store"):
        if key in result:
            value = result[key]
            if (not value.startswith("/") or value.startswith("//") or value == "/" or "\x00" in value
                    or any(part in (".", "..") for part in value.split("/"))):
                raise ValueError()
    if result.get("--restart-policy", "unless-stopped") not in ("no", "unless-stopped"):
        raise ValueError()
    return result


def main(arguments=None):
    code = "invalid"
    error_type = None
    try:
        options = _parse(sys.argv[1:] if arguments is None else arguments)
        if sys.platform != "linux" or sys.stdin.isatty() or sys.stdin.buffer.read(1):
            raise ValueError()
        from admin_v2_apply import (
            ApplicationV2Error, application_status, prepare_application,
            apply_application, recover_application,
        )
        error_type = ApplicationV2Error
        if options["--action"] in ("apply", "recover"):
            code = "uncertain"
        if options["--action"] == "overview":
            from admin_v2_apply import application_overview
            value = application_overview(options["--base"])
        elif options["--action"] == "status":
            value = application_status(options["--base"])
        else:
            from admin_v2_docker import DockerApplicationBackend
            backend = DockerApplicationBackend(options["--container"], options["--base"], options["--image"],
                                                options.get("--restart-policy", "unless-stopped"))
            common = (options["--store"], options["--base"])
            if options["--action"] == "prepare":
                value = prepare_application(*common, options["--revision"], backend)
            elif options["--action"] == "apply":
                value = apply_application(*common, options["--revision"], options["--operation"], backend)
            else:
                value = recover_application(*common, options["--operation"], backend)
        print(json.dumps({"ok": True, "value": value}, ensure_ascii=False, separators=(",", ":")))
        return 0
    except BaseException as error:
        # The inner exception and its traceback may contain private paths or
        # model values. Neither is printed, chained or reflected in diagnostics.
        if error_type is not None and isinstance(error, error_type):
            candidate = error.code
            if type(candidate) is str and candidate in _ERRORS:
                code = candidate
    print(json.dumps({"ok": False, "code": code, "error": _ERRORS[code]}, separators=(",", ":")))
    return 1


if __name__ == "__main__":
    sys.exit(main())
