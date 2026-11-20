using System;
using System.Collections.Generic;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CloudpunkVR.Film
{
    /// <summary>
    /// Holds the main menu's input while the clean frame hides it or the free camera flies there, so a stray press
    /// can't start the game behind a take: event system navigation off, InControl modules' mouse and touch off, the
    /// game's actions off, cursor hidden (and locked for mouse look). The event system and its modules stay on, so the
    /// selection is kept. Owners say every frame whether they want it. Only what we flipped is flipped back on release,
    /// and what failed to come back is retried at the next release.
    /// </summary>
    internal static class MenuInput
    {
        public const int CleanTitle = 1, FreeCamera = 2;

        private sealed class Module
        {
            public InControlInputModule M;
            public bool Mouse, Touch; // switched off by us, not back yet
        }

        private static int owners, lockOwners;
        private static bool released; // the last release ended a real hold
        private static bool leaving;  // leaving the main menu: its buttons go, don't compare the selection

        // what a hold switched off and isn't back yet
        private static EventSystem es;
        private static IntPtr esPtr;
        private static bool navTaken;
        private static readonly List<Module> modules = new List<Module>();
        private static float nextModuleScan;
        private static PlayerActions actions;
        private static bool actionsTaken;
        private static bool cursorShown;                 // we hid it
        private static bool lockTaken;                   // we locked it, from lockBefore
        private static CursorLockMode lockBefore;
        private static GameObject selection;             // selected when the hold began
        private static IntPtr selectionEs;               // in that event system
        private static string selectionName;

        public static bool Held => owners != 0;

        /// <summary>The game is leaving the main menu (fade to black): skip the selection check until the next hold.</summary>
        public static void NoteLeaving() => leaving = true;

        public static bool HeldBy(int owner) => (owners & owner) != 0;

        /// <summary>Called every frame by each owner: hold the menu's input (and lock the cursor) or not.</summary>
        public static void Want(int owner, bool hold, bool lockCursor = false)
        {
            int before = owners;
            owners = hold ? owners | owner : owners & ~owner;
            lockOwners = hold && lockCursor ? lockOwners | owner : lockOwners & ~owner;
            if (owners != 0)
            {
                if (before == 0) Begin();
                Hold();
            }
            else
            {
                released = before != 0;
                if (released) Release();
            }
        }

        /// <summary>New hold: note the selection. What the last release couldn't give back stays noted for this one.</summary>
        private static void Begin()
        {
            Last = "";
            Verdict = null;
            leaving = false;
            nextModuleScan = 0f;
            var e = EventSystem.current;
            selection = e != null ? e.currentSelectedGameObject : null;
            selectionEs = e != null ? e.Pointer : IntPtr.Zero;
            selectionName = selection != null ? selection.name : null;
        }

        private static void Hold()
        {
            var current = EventSystem.current;
            if (current != null && current.Pointer != esPtr)
            {
                // another event system: give the old one back, take this one
                if (es != null) GiveBackEventSystem(new List<string>());
                es = current;
                esPtr = current.Pointer;
                navTaken = false;
                modules.Clear();
                nextModuleScan = 0f;
            }
            if (es != null)
            {
                if (es.m_sendNavigationEvents) { es.m_sendNavigationEvents = false; navTaken = true; }
                // look for new modules once a second, hold them all every frame
                if (Time.unscaledTime >= nextModuleScan) FindModules();
                for (int i = 0; i < modules.Count; i++)
                {
                    var m = modules[i];
                    if (m.M == null) continue;
                    if (m.M.allowMouseInput) { m.M.allowMouseInput = false; m.Mouse = true; }
                    if (m.M.allowTouchInput) { m.M.allowTouchInput = false; m.Touch = true; }
                }
            }

            var gi = GameInput.Instance;
            var a = gi != null ? gi.playerActions : null;
            if (a != null && (actions == null || a.Pointer != actions.Pointer))
            {
                if (actions != null) GiveBackActions();
                actions = a;
                actionsTaken = false;
            }
            if (actions != null && actions.Enabled) { actions.Enabled = false; actionsTaken = true; }

            if (Cursor.visible) { Cursor.visible = false; cursorShown = true; }
            if (lockOwners != 0)
            {
                if (Cursor.lockState != CursorLockMode.Locked)
                {
                    if (!lockTaken) { lockBefore = Cursor.lockState; lockTaken = true; }
                    Cursor.lockState = CursorLockMode.Locked;
                }
            }
            else UnlockIfOurs();
        }

        private static void FindModules()
        {
            nextModuleScan = Time.unscaledTime + 1f;
            var found = es.GetComponents<InControlInputModule>();
            for (int i = 0; i < found.Length; i++)
            {
                var m = found[i];
                if (m == null) continue;
                bool known = false;
                for (int k = 0; k < modules.Count && !known; k++) known = modules[k].M != null && modules[k].M.Pointer == m.Pointer;
                if (!known) modules.Add(new Module { M = m });
            }
        }

        /// <summary>Undo our cursor lock if it's still locked (one the game set since is the game's).</summary>
        private static string UnlockIfOurs()
        {
            if (!lockTaken) return null;
            if (Cursor.lockState != CursorLockMode.Locked) { lockTaken = false; return null; }
            Cursor.lockState = lockBefore;
            lockTaken = Cursor.lockState == CursorLockMode.Locked && lockBefore != CursorLockMode.Locked;
            return lockTaken ? null : $"the cursor unlocked ({lockBefore})";
        }

        /// <summary>Navigation first, then each module on its own, so one that throws doesn't block the others.</summary>
        private static string GiveBackEventSystem(List<string> failed)
        {
            if (es == null)
            {
                // destroyed with its scene, modules too: nothing left to give back
                navTaken = false;
                for (int i = 0; i < modules.Count; i++) modules[i].Mouse = modules[i].Touch = false;
                return null;
            }
            if (navTaken)
            {
                try
                {
                    if (!es.m_sendNavigationEvents) es.m_sendNavigationEvents = true;
                    navTaken = !es.m_sendNavigationEvents;
                }
                catch (Exception e) { failed.Add("the navigation (" + e.Message + ")"); }
            }
            int mouse = 0;
            for (int i = 0; i < modules.Count; i++)
            {
                var m = modules[i];
                try
                {
                    if (m.M == null) { m.Mouse = m.Touch = false; continue; } // destroyed with its scene
                    if (m.Mouse)
                    {
                        if (!m.M.allowMouseInput) m.M.allowMouseInput = true;
                        m.Mouse = !m.M.allowMouseInput;
                        if (!m.Mouse) mouse++;
                    }
                    if (m.Touch)
                    {
                        if (!m.M.allowTouchInput) m.M.allowTouchInput = true;
                        m.Touch = !m.M.allowTouchInput;
                    }
                }
                catch (Exception e) { failed.Add("an InControl module's mouse (" + e.Message + ")"); }
            }
            return $"navigation {(es.m_sendNavigationEvents ? "on" : "off")} on '{es.name}', the mouse back in {mouse} InControl module(s)";
        }

        /// <summary>
        /// Reselect the button from the start of the hold if something moved it. Skipped if it was destroyed or the
        /// event system changed; a button gone inactive (submenu over it) counts as not back.
        /// </summary>
        private static string GiveBackSelection(List<string> notBack)
        {
            if (selectionName == null) return Selection(es);
            if (leaving) return "the selection not compared (the game is leaving the menu)";
            if (es == null || es.Pointer != selectionEs) return $"the selection not compared ('{selectionName}' was in another event system)";
            if (selection == null) return $"the selection not compared ('{selectionName}' was destroyed), {Selection(es)}";
            if (!selection.activeInHierarchy)
            {
                notBack.Add($"'{selectionName}' inactive (something opened over the menu)");
                return $"'{selectionName}' inactive, {Selection(es)}";
            }
            var now = es.currentSelectedGameObject;
            if (now != null && now.Pointer == selection.Pointer) return Selection(es);
            es.SetSelectedGameObject(selection);
            var after = es.currentSelectedGameObject;
            if (after == null || after.Pointer != selection.Pointer) notBack.Add($"the selection not put back on '{selectionName}'");
            return $"the selection put back on '{selectionName}' (it had moved to {(now != null ? "'" + now.name + "'" : "nothing")})";
        }

        private static string GiveBackActions()
        {
            if (!actionsTaken) return null;
            if (actions == null) { actionsTaken = false; return null; }
            if (!actions.Enabled) actions.Enabled = true;
            actionsTaken = !actions.Enabled;
            return actionsTaken ? null : "the game's actions on";
        }

        /// <summary>Everything back, piece by piece; logs what came back and what didn't.</summary>
        private static void Release()
        {
            var parts = new List<string>();
            var failed = new List<string>();
            var notBack = new List<string>();
            void Step(string what, Func<string> give)
            {
                try { var s = give(); if (s != null) parts.Add(s); }
                catch (Exception e) { failed.Add($"{what} ({e.Message})"); }
            }
            Step("the event system", () => GiveBackEventSystem(failed));
            Step("the selection", () => GiveBackSelection(notBack));
            Step("the game's actions", GiveBackActions);
            // only if still hidden as we left it: a cursor the game showed since is the game's
            Step("the cursor", () =>
            {
                if (!cursorShown) return null;
                if (!Cursor.visible) Cursor.visible = true;
                cursorShown = !Cursor.visible;
                return cursorShown ? null : "the cursor shown";
            });
            // the lock separately, whatever the game did to visibility meanwhile
            Step("the cursor's lock", UnlockIfOurs);
            lockOwners = 0;

            // anything still noted as taken didn't come back
            if (es != null && navTaken) notBack.Add("navigation off");
            for (int i = 0; i < modules.Count; i++)
            {
                var m = modules[i];
                if (m.Mouse || m.Touch) notBack.Add($"{(m.Mouse ? "the mouse" : "touch")} off in '{(m.M != null ? m.M.name : "a module")}'");
            }
            if (actionsTaken) notBack.Add("the game's actions off");
            if (cursorShown) notBack.Add("the cursor hidden");
            if (lockTaken) notBack.Add("the cursor locked");
            foreach (var f in failed) notBack.Add(f + " NOT given back");
            Verdict = notBack.Count > 0 ? string.Join(", ", notBack) : null;

            Last = (parts.Count > 0 ? string.Join(", ", parts) : "nothing to give back") + (Verdict != null ? "; NOT back: " + Verdict : "");
        }

        /// <summary>What the last release gave back, for the owners' log lines.</summary>
        public static string Last { get; private set; } = "";

        /// <summary>What the last release couldn't give back, or null.</summary>
        private static string Verdict;

        /// <summary>For an owner's log line after letting go.</summary>
        public static string AfterLetGo() => Held ? "the menu's input still held by " + Who()
                                            : released ? "the menu's input back: " + Last : "the menu's input was not held";

        private static string Who() => (owners & CleanTitle) != 0 && (owners & FreeCamera) != 0 ? "the clean frame and the free camera"
                                     : (owners & CleanTitle) != 0 ? "the clean frame" : "the free camera";

        /// <summary>What it holds now, for the log.</summary>
        public static string Describe()
        {
            if (owners == 0)
            {
                var e = EventSystem.current;
                var gi = GameInput.Instance;
                var a = gi != null ? gi.playerActions : null;
                return $"the menu's input free (navigation {(e == null ? "none" : e.m_sendNavigationEvents ? "on" : "OFF")}, the game's actions {(a == null ? "none" : a.Enabled ? "on" : "OFF")}, " +
                       $"the cursor {(Cursor.visible ? "shown" : "hidden")} {Cursor.lockState}, {Selection(e)})";
            }
            int mouseOff = 0;
            for (int i = 0; i < modules.Count; i++) if (modules[i].M != null && !modules[i].M.allowMouseInput) mouseOff++;
            return $"the menu's input held by {Who()} (navigation {(es == null ? "no event system" : es.m_sendNavigationEvents ? "ON" : "off")}{(es != null ? " on '" + es.name + "'" : "")}, " +
                   $"the mouse off in {mouseOff} of {modules.Count} InControl module(s), the game's actions {(actions == null ? "none" : actions.Enabled ? "ON" : "off")}, " +
                   $"the cursor {(Cursor.visible ? "SHOWN" : "hidden")} {Cursor.lockState}, {Selection(es)})";
        }

        /// <summary>The button Cross would press now.</summary>
        private static string Selection(EventSystem e)
        {
            var sel = e != null ? e.currentSelectedGameObject : null;
            return sel != null ? $"the selection on '{sel.name}'" : "nothing selected";
        }

#if DEV
        /// <summary>Everything it holds is still held.</summary>
        public static bool AllHeld()
        {
            if (owners == 0) return false;
            if (es != null && es.m_sendNavigationEvents) return false;
            for (int i = 0; i < modules.Count; i++) if (modules[i].M != null && (modules[i].M.allowMouseInput || modules[i].M.allowTouchInput)) return false;
            if (actions != null && actions.Enabled) return false;
            return !Cursor.visible;
        }

        /// <summary>While held: why the selection isn't the one from the start of the hold, or null (same, or not comparable).</summary>
        public static string SelectionMoved()
        {
            if (owners == 0 || leaving || selectionName == null || selection == null) return null;
            var e = EventSystem.current;
            if (e == null || e.Pointer != selectionEs) return null;
            if (!selection.activeInHierarchy) return $"the selected '{selectionName}' went inactive (something opened over the menu)";
            var now = e.currentSelectedGameObject;
            if (now != null && now.Pointer == selection.Pointer) return null;
            return $"the selection moved from '{selectionName}' to {(now != null ? "'" + now.name + "'" : "nothing")}";
        }

        /// <summary>After letting go: what didn't come back (null: all of it), or who still holds it.</summary>
        public static string NotBack() => owners != 0 ? "still held by " + Who() : Verdict;
#endif
    }
}
