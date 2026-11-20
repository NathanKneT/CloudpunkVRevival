"""Generates the SteamVR Input action manifest and default bindings in bindings/.

Run: python tools/make_bindings.py
The layout copies a gamepad, and the mod makes the game's prompts match the controller in hand:
left controller = left half of the pad, right controller = right half.
"""
import json
import os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bindings")

ACTIONS = [
    # name, type, English, French
    ("move", "vector2", "Walk / steer", "Marcher / diriger"),
    ("look", "vector2", "Turn (on foot) / climb and dive (HOVA)", "Tourner (à pied) / monter et descendre (HOVA)"),
    ("interact", "boolean", "Interact / confirm", "Interagir / valider"),
    ("back", "boolean", "Back / cancel", "Retour / annuler"),
    ("freelook", "boolean", "Square: free camera (menus)", "Carré : caméra libre (menus)"),
    ("inventory", "boolean", "Inventory", "Inventaire"),
    ("accelerate", "vector1", "Accelerate (HOVA)", "Accélérer (HOVA)"),
    ("brake", "vector1", "Brake and reverse (HOVA), sneak (on foot)", "Freiner et reculer (HOVA), discrétion (à pied)"),
    ("strafeleft", "boolean", "Strafe left (HOVA)", "Glisser à gauche (HOVA)"),
    ("straferight", "boolean", "Strafe right (HOVA)", "Glisser à droite (HOVA)"),
    ("menu", "boolean", "Pause menu", "Menu pause"),
    ("map", "boolean", "Map", "Carte"),
    ("switchview", "boolean", "Cockpit or outside view", "Vue cockpit ou extérieure"),
    ("togglehud", "boolean", "Show or hide the HUD", "Afficher ou masquer l'interface"),
    ("recenter", "boolean", "Recenter view", "Recentrer la vue"),
    ("clickleft", "boolean", "Menu pointer click (left hand)", "Clic du pointeur (main gauche)"),
    ("clickright", "boolean", "Menu pointer click (right hand)", "Clic du pointeur (main droite)"),
    ("poseleft", "pose", "Left hand pointer", "Pointeur main gauche"),
    ("poseright", "pose", "Right hand pointer", "Pointeur main droite"),
]
OUTPUTS = [
    ("hapticleft", "vibration", "Left vibration", "Vibration gauche"),
    ("hapticright", "vibration", "Right vibration", "Vibration droite"),
]
# The hands: SteamVR's hand skeleton, and what the PS VR2 Sense's touch sensors say of the fingers (thumb on a face
# button or the stick, index on L2/R2, middle finger on the grip button L1/R1, the grip's press, and the trigger's and
# the grip's proximity sensors).
HAND = [
    ("skeletonleft", "skeleton", "Left hand (fingers)", "Main gauche (doigts)"),
    ("skeletonright", "skeleton", "Right hand (fingers)", "Main droite (doigts)"),
    ("thumbleft", "boolean", "Left thumb on a button", "Pouce gauche sur un bouton"),
    ("thumbright", "boolean", "Right thumb on a button", "Pouce droit sur un bouton"),
    ("indexleft", "boolean", "Left index on its trigger", "Index gauche sur sa gâchette"),
    ("indexright", "boolean", "Right index on its trigger", "Index droit sur sa gâchette"),
    ("middleleft", "boolean", "Left middle finger on the grip", "Majeur gauche sur le grip"),
    ("middleright", "boolean", "Right middle finger on the grip", "Majeur droit sur le grip"),
    ("squeezeleft", "vector1", "Left grip squeeze (fingers)", "Grip gauche serré (doigts)"),
    ("squeezeright", "vector1", "Right grip squeeze (fingers)", "Grip droit serré (doigts)"),
    ("indexnearleft", "vector1", "Left index near its trigger", "Index gauche près de sa gâchette"),
    ("indexnearright", "vector1", "Right index near its trigger", "Index droit près de sa gâchette"),
    ("middlenearleft", "vector1", "Left middle finger near the grip", "Majeur gauche près du grip"),
    ("middlenearright", "vector1", "Right middle finger near the grip", "Majeur droit près du grip"),
]
OPTIONAL = {"togglehud"}

L, R = "/user/hand/left", "/user/hand/right"


def manifest():
    actions = []
    for name, typ, _, _ in ACTIONS:
        a = {"name": f"/actions/main/in/{name}", "type": typ}
        a["requirement"] = "optional" if name in OPTIONAL else "suggested"
        actions.append(a)
    for name, typ, _, _ in HAND:
        a = {"name": f"/actions/main/in/{name}", "type": typ}
        if typ == "skeleton":
            a["skeleton"] = "/skeleton/hand/" + ("left" if name.endswith("left") else "right")
        else:
            a["requirement"] = "optional"
        actions.append(a)
    for name, typ, _, _ in OUTPUTS:
        actions.append({"name": f"/actions/main/out/{name}", "type": typ})
    loc = []
    for tag, idx in (("en_US", 2), ("fr_FR", 3)):
        d = {"language_tag": tag, "/actions/main": "Cloudpunk"}
        for row in ACTIONS:
            d[f"/actions/main/in/{row[0]}"] = row[idx]
        for row in OUTPUTS:
            d[f"/actions/main/out/{row[0]}"] = row[idx]
        for row in HAND:
            d[f"/actions/main/in/{row[0]}"] = row[idx]
        loc.append(d)
    return {
        "default_bindings": [
            {"controller_type": "playstation_vr2_sense", "binding_url": "bindings_playstation_vr2_sense.json"},
            {"controller_type": "knuckles", "binding_url": "bindings_knuckles.json"},
            {"controller_type": "oculus_touch", "binding_url": "bindings_oculus_touch.json"},
            {"controller_type": "pico_controller", "binding_url": "bindings_pico_controller.json"},
            {"controller_type": "frame_controller", "binding_url": "bindings_frame_controller.json"},
        ],
        "actions": actions,
        "action_sets": [{"name": "/actions/main", "usage": "single"}],
        "localization": loc,
    }


def src(path, mode, inputs, parameters=None):
    s = {"path": path, "mode": mode,
         "inputs": {k: {"output": f"/actions/main/in/{v}"} for k, v in inputs.items()}}
    if parameters:
        s["parameters"] = parameters
    return s


def binding(ctype, name, sources):
    return {
        "action_manifest_version": 0,
        "alias_info": {},
        "app_key": "steam.app.746850",
        "bindings": {"/actions/main": {
            "sources": sources,
            "poses": [
                {"output": "/actions/main/in/poseleft", "path": f"{L}/pose/tip"},
                {"output": "/actions/main/in/poseright", "path": f"{R}/pose/tip"},
            ],
            "haptics": [
                {"output": "/actions/main/out/hapticleft", "path": f"{L}/output/haptic"},
                {"output": "/actions/main/out/hapticright", "path": f"{R}/output/haptic"},
            ],
            "chords": [],
            "skeleton": [
                {"output": "/actions/main/in/skeletonleft", "path": f"{L}/input/skeleton/left"},
                {"output": "/actions/main/in/skeletonright", "path": f"{R}/input/skeleton/right"},
            ],
        }},
        "category": "steamvr_input",
        "controller_type": ctype,
        "description": "Cloudpunk VRevival default layout: the two controllers work as one gamepad, and the game's button prompts follow this controller.",
        "name": name,
        "options": {},
        "simulated_actions": [],
    }


# The Sense senses the thumb on the face buttons, the stick and Options/Create, the index on L2/R2 and the middle
# finger on L1/R1, the grip button (SteamVR's legacy binding and the driver's render model call it the grip); L1/R1
# and L2/R2 also have analog proximity sensors (l1_sensor...). The hands read them through the touch actions.
# Create is not the map: Sony's driver binds it to SteamVR's screenshot in its VR Dashboard bindings
# (vrcompositor_binding_playstation_vr2_sense_controller.json), a system action that fires in every game on top of
# the game's own binding. The map is a long press of Options instead, as on Touch.
PSVR2 = binding("playstation_vr2_sense", "Cloudpunk VRevival - PS VR2 Sense", [
    src(f"{L}/input/left_stick", "joystick", {"position": "move", "click": "switchview", "touch": "thumbleft"}),
    src(f"{R}/input/right_stick", "joystick", {"position": "look", "click": "recenter", "touch": "thumbright"}),
    src(f"{R}/input/cross", "button", {"click": "interact", "touch": "thumbright"}),
    src(f"{R}/input/circle", "button", {"click": "back", "touch": "thumbright"}),
    src(f"{L}/input/square", "button", {"click": "freelook", "touch": "thumbleft"}),
    src(f"{L}/input/triangle", "button", {"click": "inventory", "touch": "thumbleft"}),
    src(f"{R}/input/r2", "trigger", {"pull": "accelerate", "click": "clickright", "touch": "indexright"}),
    src(f"{L}/input/l2", "trigger", {"pull": "brake", "click": "clickleft", "touch": "indexleft"}),
    src(f"{L}/input/l1", "trigger", {"click": "strafeleft", "pull": "squeezeleft", "touch": "middleleft"}),
    src(f"{L}/input/l1_sensor", "trigger", {"pull": "middlenearleft"}),
    src(f"{L}/input/l2_sensor", "trigger", {"pull": "indexnearleft"}),
    src(f"{R}/input/r1", "trigger", {"click": "straferight", "pull": "squeezeright", "touch": "middleright"}),
    src(f"{R}/input/r1_sensor", "trigger", {"pull": "middlenearright"}),
    src(f"{R}/input/r2_sensor", "trigger", {"pull": "indexnearright"}),
    src(f"{R}/input/options", "button", {"single": "menu", "long": "map", "touch": "thumbright"},
        {"long_press_delay": "0.6"}),
    src(f"{L}/input/create", "button", {"touch": "thumbleft"}),
])

KNUCKLES = binding("knuckles", "Cloudpunk VRevival - Valve Index", [
    src(f"{L}/input/thumbstick", "joystick", {"position": "move", "click": "switchview"}),
    src(f"{R}/input/thumbstick", "joystick", {"position": "look", "click": "recenter"}),
    src(f"{R}/input/a", "button", {"click": "interact"}),
    src(f"{R}/input/b", "button", {"click": "back"}),
    src(f"{L}/input/a", "button", {"click": "freelook"}),
    src(f"{L}/input/b", "button", {"click": "inventory"}),
    src(f"{R}/input/trigger", "trigger", {"pull": "accelerate", "click": "clickright"}),
    src(f"{L}/input/trigger", "trigger", {"pull": "brake", "click": "clickleft"}),
    src(f"{L}/input/grip", "button", {"click": "strafeleft"}),
    src(f"{R}/input/grip", "button", {"click": "straferight"}),
    src(f"{R}/input/trackpad", "button", {"click": "menu"}),
    src(f"{L}/input/trackpad", "button", {"click": "map"}),
])

# Quest 2 / 3 / 3S (Link, Air Link, Virtual Desktop) and Rift. The left menu button is /input/system on the left
# hand (the right one is reserved by the system). Touch has no spare button for the map: a long press on the
# menu button opens it, a short press opens the pause menu.
TOUCH_SOURCES = [
    src(f"{L}/input/joystick", "joystick", {"position": "move", "click": "switchview"}),
    src(f"{R}/input/joystick", "joystick", {"position": "look", "click": "recenter"}),
    src(f"{R}/input/a", "button", {"click": "interact"}),
    src(f"{R}/input/b", "button", {"click": "back"}),
    src(f"{L}/input/x", "button", {"click": "freelook"}),
    src(f"{L}/input/y", "button", {"click": "inventory"}),
    src(f"{R}/input/trigger", "trigger", {"pull": "accelerate", "click": "clickright"}),
    src(f"{L}/input/trigger", "trigger", {"pull": "brake", "click": "clickleft"}),
    src(f"{L}/input/grip", "trigger", {"click": "strafeleft"}),
    src(f"{R}/input/grip", "trigger", {"click": "straferight"}),
    src(f"{L}/input/system", "button", {"single": "menu", "long": "map"}),
]
TOUCH = binding("oculus_touch", "Cloudpunk VRevival - Touch (Quest, Rift)", TOUCH_SOURCES)

# Pico 4 / Pico 4 Ultra through Steam Link report "pico_controller". Paths come from SteamVR's own profiles
# (drivers/vrlink/resources/input/pico_controller_profile.json and resources/input/default_pico_controller_profile.json,
# whose compatibility fallback is oculus_touch): same buttons as Touch, menu on the left /input/system.
# Virtual Desktop presents Touch controllers and uses the binding above; PICO Connect is not confirmed.
PICO = binding("pico_controller", "Cloudpunk VRevival - Pico", TOUCH_SOURCES)

# Steam Frame: the two controllers are one Steam gamepad: A/B/X/Y and Menu on the right, d-pad and View on the left.
FRAME = binding("frame_controller", "Cloudpunk VRevival - Steam Frame", [
    src(f"{L}/input/thumbstick", "joystick", {"position": "move", "click": "switchview"}),
    src(f"{R}/input/thumbstick", "joystick", {"position": "look", "click": "recenter"}),
    src(f"{R}/input/a", "button", {"click": "interact"}),
    src(f"{R}/input/b", "button", {"click": "back"}),
    src(f"{R}/input/x", "button", {"click": "freelook"}),
    src(f"{R}/input/y", "button", {"click": "inventory"}),
    src(f"{R}/input/trigger", "trigger", {"pull": "accelerate", "click": "clickright"}),
    src(f"{L}/input/trigger", "trigger", {"pull": "brake", "click": "clickleft"}),
    src(f"{L}/input/bumper", "button", {"click": "strafeleft"}),
    src(f"{R}/input/bumper", "button", {"click": "straferight"}),
    src(f"{R}/input/menu", "button", {"click": "menu"}),
    src(f"{L}/input/view", "button", {"click": "map"}),
    src(f"{L}/input/dpad_up", "button", {"click": "switchview"}),
    src(f"{L}/input/dpad_right", "button", {"click": "togglehud"}),
])


def main():
    os.makedirs(OUT, exist_ok=True)
    files = {
        "actions.json": manifest(),
        "bindings_playstation_vr2_sense.json": PSVR2,
        "bindings_knuckles.json": KNUCKLES,
        "bindings_oculus_touch.json": TOUCH,
        "bindings_pico_controller.json": PICO,
        "bindings_frame_controller.json": FRAME,
    }
    for name, data in files.items():
        with open(os.path.join(OUT, name), "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
            f.write("\n")
    print("wrote", ", ".join(files))


if __name__ == "__main__":
    main()
