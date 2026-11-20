using System;
using CloudpunkVR.VR;
using InControl;
using UnityEngine;

namespace CloudpunkVR.Input
{
    /// <summary>
    /// On-foot extras: click the left stick to jog (the game has no run), crouch to sneak, and reach a hand to
    /// what the game offers and squeeze the grip to interact.
    /// </summary>
    internal static class OnFoot
    {
        private const float Reach = 0.22f;        // m from the hand to the thing's outline
        private const float CrouchDown = 0.40f, CrouchUp = 0.30f;

        public static bool Jogging { get; private set; }
        // dev command: one simulated reach and squeeze
        public static bool SimReach;
        public static bool Crouching { get; private set; }

        /// <summary>On foot the view button jogs (d-pad up only zooms the game's camera there).</summary>
        public static bool UsesViewButton { get; private set; }
        /// <summary>On foot the grips reach (strafing only exists in the HOVA).</summary>
        public static bool UsesGrips { get; private set; }

        private static float gameSpeed = -1f, ourSpeed = -1f, stickLowSince = -1f;
        private static IntPtr player;
        private static GameObject target;
        private static Bounds targetBounds;
        private static float targetBoundsAt;
        private static bool leftNear, rightNear;

        public static void Update(ViewMode mode, bool ui)
        {
            bool onFoot = mode == ViewMode.OnFoot && !ui;
            UsesViewButton = onFoot && VRConfig.Jog.Value;
            UsesGrips = onFoot && VRConfig.ReachToInteract.Value;
            try { UpdateJog(onFoot); }
            catch (Exception e) { Jogging = false; Note("jog failed: " + e.Message); }
            Crouch(onFoot);
            try { UpdateReach(onFoot); }
            catch (Exception) { leftNear = rightNear = false; }
        }

        private static void UpdateJog(bool onFoot)
        {
            var p = Player.Instance;
            if (p == null) { Jogging = false; return; }
            if (p.Pointer != player) { player = p.Pointer; gameSpeed = ourSpeed = -1f; }
            float move = SteamVRInput.Move.magnitude;
            // released = near centre for 0.3 s, since the stick crosses it when changing direction
            if (move >= 0.15f) stickLowSince = -1f;
            else if (stickLowSince < 0f) stickLowSince = Time.unscaledTime;
            bool released = stickLowSince >= 0f && Time.unscaledTime - stickLowSince > 0.3f;
            if (Jogging && (!onFoot || !VRConfig.Jog.Value || released))
            {
                Jogging = false;
                Note((!onFoot ? "jog ends: not walking freely (menu, dialogue or vehicle)" : "jog ends: stick released") + $" (stick {move:0.00})");
            }
            else if (!Jogging && onFoot && VRConfig.Jog.Value && SteamVRInput.SwitchView.Pressed && move > 0.3f)
            {
                Jogging = true;
                Haptics.Pulse(true, 0.025f, 0.35f, 150f);
                Note($"jog: Rania trots at {VRConfig.JogSpeed.Value:0.0}x her walking speed");
            }

            // pick up the game's speed when it changes (food bonus, new scene), then scale it
            float now = p.runSpeed;
            if (gameSpeed < 0f || !Mathf.Approximately(now, ourSpeed)) gameSpeed = now;
            float want = Jogging ? gameSpeed * VRConfig.JogSpeed.Value : gameSpeed;
            if (!Mathf.Approximately(now, want)) p.runSpeed = want;
            ourSpeed = want;
        }

        private static void Crouch(bool onFoot)
        {
            if (!onFoot || !VRConfig.CrouchToSneak.Value) { Crouching = false; return; }
            float drop = CameraDriver.HeadDrop;
            if (!Crouching && drop > CrouchDown) { Crouching = true; Haptics.Both(0.02f, 0.25f, 120f); }
            else if (Crouching && drop < CrouchUp) Crouching = false;
        }

        private static void UpdateReach(bool onFoot)
        {
            if (!UsesGrips) { leftNear = rightNear = false; target = null; return; }
            var inter = Player.Instance?.interaction;
            GameObject closest = inter != null && inter.isActiveAndEnabled ? inter.closestObject : null;
            if (closest == null || !closest.activeInHierarchy) { leftNear = rightNear = false; target = null; return; }
            if (target == null || target.Pointer != closest.Pointer || Time.unscaledTime > targetBoundsAt + 0.5f)
            {
                target = closest;
                targetBounds = Outline(closest);
                targetBoundsAt = Time.unscaledTime;
            }
            if (SimReach)
            {
                SimReach = false;
                VirtualGamepad.Hold(InputControlType.Action1, 0.12f);
                Plugin.L.LogInfo($"Reach (simulated): left hand interacts with '{target.name}', outline {targetBounds.center} size {targetBounds.size}.");
                return;
            }
            Hand(true, SteamVRInput.LeftHand, SteamVRInput.StrafeLeft, ref leftNear);
            Hand(false, SteamVRInput.RightHand, SteamVRInput.StrafeRight, ref rightNear);
        }

        private static void Hand(bool left, SteamVRInput.HandPose pose, SteamVRInput.Button grip, ref bool near)
        {
            if (!pose.Valid) { near = false; return; }
            CameraDriver.TrackingToWorld(pose.Position, pose.Rotation, out var p, out var r);
            Vector3 palm = p + r * new Vector3(0f, -0.02f, 0.05f);
            bool now = targetBounds.SqrDistance(palm) < Reach * Reach;
            if (now && !near) Haptics.Pulse(left, 0.02f, 0.3f, 170f); // within reach
            near = now;
            if (near && grip.Pressed)
            {
                VirtualGamepad.Hold(InputControlType.Action1, 0.12f);
                Haptics.Pulse(left, 0.04f, 0.6f, 120f);
                Plugin.L.LogInfo($"Reach: {(left ? "left" : "right")} hand interacts with '{target.name}'.");
            }
        }

        /// <summary>What the hand can touch: renderer bounds, else colliders.</summary>
        private static Bounds Outline(GameObject go)
        {
            bool any = false;
            var b = new Bounds(go.transform.position, Vector3.zero);
            var renderers = go.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                var rd = renderers[i];
                if (rd == null || !rd.enabled || rd.TryCast<ParticleSystemRenderer>() != null) continue;
                if (!any) { b = rd.bounds; any = true; } else b.Encapsulate(rd.bounds);
            }
            if (!any)
            {
                var colliders = go.GetComponentsInChildren<Collider>();
                for (int i = 0; i < colliders.Length; i++)
                {
                    var c = colliders[i];
                    if (c == null || !c.enabled || c.isTrigger) continue;
                    if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
                }
            }
            if (!any) b = new Bounds(go.transform.position + Vector3.up, new Vector3(0.4f, 1.6f, 0.4f));
            return b;
        }

        private static int notes;

        /// <summary>Logs the first few changes.</summary>
        private static void Note(string what)
        {
            if (notes >= 8) return;
            notes++;
            Plugin.L.LogInfo("On foot: " + what + ".");
        }

        public static string Describe()
        {
            string reach = target == null ? "nothing to reach" : $"'{target.name}' outline {targetBounds.center} size {targetBounds.size}, left near {leftNear}, right near {rightNear}";
            return $"jog {Jogging} (stick {SteamVRInput.Move.magnitude:0.00}, game speed {gameSpeed:0.0}, ours {ourSpeed:0.0}), crouch {Crouching} (head drop {CameraDriver.HeadDrop:0.00} m), reach: {reach}";
        }
    }
}
