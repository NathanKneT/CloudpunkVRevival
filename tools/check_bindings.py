"""Checks the default bindings in bindings/ against the controller profiles installed with SteamVR.

Run: python tools/check_bindings.py [path to SteamVR]
Every input path, mode output and pose/haptic path must exist in the controller's input profile (with the right
hand side), and every action used must exist in the action manifest. Exit code 1 on any problem.
"""
import glob
import json
import os
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
DEFAULT_STEAMVR = r"C:\Program Files (x86)\Steam\steamapps\common\SteamVR"
PROFILES = {  # controller_type -> profile file, relative to SteamVR (also searched for other installs)
    "playstation_vr2_sense": None,  # ships with the PlayStation VR2 app, located below
    "knuckles": r"drivers\indexcontroller\resources\input\index_controller_profile.json",
    "oculus_touch": r"drivers\oculus\resources\input\touch_profile.json",
    "pico_controller": r"drivers\vrlink\resources\input\pico_controller_profile.json",
    "frame_controller": r"drivers\frame_controller\resources\input\frame_controller_profile.json",
}


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def find_profile(steamvr, ctype):
    rel = PROFILES.get(ctype)
    if rel:
        p = os.path.join(steamvr, rel)
        return p if os.path.exists(p) else None
    common = os.path.dirname(os.path.dirname(steamvr.rstrip("\\/")))
    for p in glob.glob(os.path.join(common, "common", "*", "**", f"{ctype}*profile.json"), recursive=True):
        return p
    for p in glob.glob(os.path.join(os.path.dirname(steamvr.rstrip("\\/")), "*", "**", f"{ctype}*profile.json"), recursive=True):
        return p
    return None


def main():
    steamvr = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_STEAMVR
    manifest = load(os.path.join(ROOT, "bindings", "actions.json"))
    actions = {a["name"].lower() for a in manifest["actions"]}
    problems = 0
    for entry in manifest["default_bindings"]:
        ctype = entry["controller_type"]
        binding = load(os.path.join(ROOT, "bindings", entry["binding_url"]))
        profile_path = find_profile(steamvr, ctype)
        if not profile_path:
            print(f"?  {ctype}: profile not installed here, skipped")
            continue
        sources = load(profile_path)["input_source"]

        def known(path):
            # /user/hand/<side>/input/x -> /input/x, checking the side when the profile restricts it
            parts = path.split("/")
            side, rest = parts[3], "/" + "/".join(parts[4:])
            src = sources.get(rest)
            return src is not None and src.get("side", side) == side

        before = problems
        used = []
        for set_name, b in binding["bindings"].items():
            for s in b.get("sources", []):
                if not known(s["path"]):
                    print(f"X  {ctype}: unknown input {s['path']}")
                    problems += 1
                for out in s.get("inputs", {}).values():
                    used.append(out["output"].lower())
            for p in b.get("poses", []) + b.get("haptics", []):
                if not known(p["path"]):
                    print(f"X  {ctype}: unknown path {p['path']}")
                    problems += 1
                used.append(p["output"].lower())
        missing = sorted(set(used) - actions)
        for m in missing:
            print(f"X  {ctype}: action {m} is not in actions.json")
            problems += 1
        status = "OK" if problems == before else "!!"
        print(f"{status} {ctype}: {len(used)} outputs checked against {os.path.relpath(profile_path, os.path.dirname(steamvr))}")
    sys.exit(1 if problems else 0)


if __name__ == "__main__":
    main()
