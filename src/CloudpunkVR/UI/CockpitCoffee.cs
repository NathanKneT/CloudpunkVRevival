using System;
using System.Collections.Generic;
using System.Globalization;
using CloudpunkVR.Input;
using CloudpunkVR.VR;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InControl;
using UnityEngine;
using N = System.Numerics;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Rania's coffee on the console: sloshes with the HOVA, grab with strafe, sip at the mouth, throw it around.
    /// Lives in the cockpit set on the interior camera's layer, so HovaBody and the hull fix never touch it.
    /// </summary>
    internal static class CockpitCoffee
    {
        // ---------------------------------------------------------------- constants

        private const float Step = 1f / 360f;
        private const int MaxSteps = 24;
        private const float Gravity = 9.81f;
        private const float MaxCarPush = 60f;           // m/s2 cap (a crash); HovaPush shapes the rest
        private const float CarSmoothing = 0.04f;       // s: car velocity only changes on physics steps
        private const float HandSmoothing = 0.03f;      // s: tracking jitter
        private const float MaxHandPush = 30f;
        private const float GrabGap = 0.045f;           // closed hand to cup axis
        private const float GripNear = 0.10f;           // a press this close is the cup's, not a strafe
        private const float GrabWindow = 0.25f;
        private const float TipToGrip = 0.07f;          // no skeleton: controller tip back to the closed hand
        private const float GrabBlend = 0.09f, ReleaseBlend = 0.12f;
        private const float DropRadius = 0.04f, DropAbove = 0.06f, DropBelow = 0.015f, DropTilt = 35f;
        private const float DropTime = 0.12f;
        private const float SipReach = 0.05f;           // pouring lip to mouth
        private const float SipLip = 0.003f;            // coffee to lip
        private const float SipStep = CoffeeModel.Voxel, SipEvery = 1.0f;
        private static readonly N.Vector3 MouthFromEyes = new N.Vector3(0f, -0.075f, 0.075f);
        private const float CoolTime = 240f;            // s, heat and steam halve in under 3 min
        private const float SpillLine = 0.003f;         // level lost in one spill before Rania reacts
        private const float LineGap = 6f, SpillLineGap = 20f;
        private const float NearHand = 0.15f;
        private const float OnFootForRefill = 3f;
        private const float SteamCeiling = 0.25f;       // puffs stay this far under the resting eyes
        private const int Puffs = 16, Drops = 24;
        private const float DropPerLevel = 0.00035f;    // one drop per this much level spilled
        private const float EmptyLevel = 0.0005f;
        private const string EmptyNote = "empty. A fresh one next time you get in.", FreshNote = "fresh and hot.";

        // thrown cup: flies in the holder's frame (turns with the HOVA), lands on CabinShape via CoffeeBody
        private const float MaxThrow = 7f, MaxSpin = 25f, ThrowWindow = 0.04f;
        private const float RestSpeed = 0.04f, RestSpin = 0.4f, RestTime = 0.35f;
        private const float WakeOver = 1.15f, WakeAgain = 1f; // x friction to wake; m/s2 over the settled push
        private const int RimPieces = 24;
        private const float SplashFrom = 1.2f, SplashPer = 0.004f; // m/s, then 4 mm of level per m/s over
        private const float Reach = 0.70f;              // resting further than this from the shoulder: goes back
        private const float FarWait = 3f, FarGap = 0.5f, LostGap = 1.5f, FadeTime = 0.3f, GrowTime = 0.35f, MaxFlight = 8f;

        // ---------------------------------------------------------------- state

        private enum Where { Holder, Held, Dropping, Returning, Flying, Lying, Fading }
        private static Where where = Where.Holder;

        // this frame: world to set rotation, holder pose, head, resting eyes
        private static N.Quaternion toSet = N.Quaternion.Identity, toWorld = N.Quaternion.Identity, trackToSet = N.Quaternion.Identity;
        private static N.Vector3 seatPos, seatUp = N.Vector3.UnitY, seatFwd = N.Vector3.UnitZ, seatRight = N.Vector3.UnitX, eyeRest, headPos, mouth;
        private static N.Quaternion seatRot = N.Quaternion.Identity, headRot = N.Quaternion.Identity;

        // cup in the set, and its motion there
        private static N.Vector3 cupPos, cupVel, cupAccel, lastCupPos;
        private static N.Quaternion cupRot = N.Quaternion.Identity, lastWorldRot = N.Quaternion.Identity;
        private static bool haveMotion, haveRot;
        private static N.Vector3 moveFrom, moveVia;                   // path back, holder frame
        private static N.Quaternion moveFromRot = N.Quaternion.Identity;
        private static float moveT, moveTime;

        // released cup (CoffeeBody holds its pose and motion)
        private static float flyingSince, stillFor, farFrom = -1f, lastTick = -10f, fadeAt, fadeGap, growT = 1f, cupScale = 1f, drawnScale = 1f;
        private static int bounces, throws, wakes;
        private static bool throwLeft, hitLogged, farLogged;
        private static float settledPush, wheelGapAtRest = float.PositiveInfinity, leverGapAtRest = float.PositiveInfinity;
        private static ulong bouncedOff; // bit per thing hit this flight, logged once each
        private static N.Vector3 lastMiddle; // cup middle last frame (eye frame), for pane crossings
        private static float splashed; // level knocked out this frame

        // recent cup positions in the hand (holder frame), for the throw velocity
        private const int Trail = 10;
        private static readonly N.Vector3[] trailPos = new N.Vector3[Trail];
        private static readonly N.Quaternion[] trailRot = new N.Quaternion[Trail];
        private static readonly float[] trailTime = new float[Trail];
        private static int trailCount, trailHead;

        // the HOVA
        private static N.Vector3 carVel, carAccel, hovaPush, devPush, lastRawVel; // hovaPush: felt by the cup (set frame)
        private static float devPushUntil, nextJoltLog;
        private static bool haveCar, jolting;
        private static int knocks, kicks; // jolts with a collision / without
        private const float KnockSpeed = 2f;
        private static N.Vector3 feltLast;
        private static bool haveFelt;
        private static float accumulator;

        // the coffee
        private static float filledAt = -1000f, lastSip = -10f, lastLine = -100f, lastSpillLine = -100f;
        private static int sips, spills;
        private static float spilled;
        private static bool filled, spilling;
        private static float spillLost, spillPeak, lastSpillAt, dropDebt;
        private static N.Vector3 spillPush;
        private static string refillWhy;
        private static float onFootSince = -1f;
        private static bool emptyNoted, emptyHinted, freshNote;
        private static float levelWhenTyping = -1f; // "coffee level <cm> typing": applied once a wrist line types

        // peaks since the last "coffee" command
        private static float peakTilt, peakOver = float.NegativeInfinity, peakFwd, peakSide, peakUp, peakFelt;

        private sealed class HandState
        {
            public bool Left;
            public bool Valid;
            public N.Vector3 CtrlPos, Grip, Back;
            public N.Quaternion CtrlRot = N.Quaternion.Identity;
            public float Gap = float.PositiveInfinity, Blend, PressedAt = -10f;
            public bool Swallow;
            public HandGrip Pose;
        }

        /// <summary>Hand pose round the cup this frame, world space (for VoxelHands).</summary>
        public struct HandGrip
        {
            public float Weight;                 // 0 own pose .. 1 closed round the cup
            public Vector3 Point, Up, Out;       // axis at palm height; cup up; axis to palm
            public float Radius;                 // outer radius there
        }

        private static readonly HandState left = new HandState { Left = true }, right = new HandState();
        private static readonly HandState[] hands = { left, right };
        private static HandState holder;
        private static N.Vector3 holdPos, contact = N.Vector3.UnitX;
        private static N.Quaternion holdRot = N.Quaternion.Identity;

        /// <summary>Outer radius at the grip, plus a bit for voxel corners.</summary>
        public static readonly float GripRadius = CoffeeModel.OuterRadius(CoffeeModel.GripHeight) + 0.3f * CoffeeModel.Voxel;

        // steam and drops
        private struct Bit
        {
            public bool Alive, Landed, Drawn;
            public N.Vector3 Pos, Vel;
            public float Age, Life, Size, Phase;
        }
        private static readonly Bit[] puffs = new Bit[Puffs], drops = new Bit[Drops];
        private static float nextPuff, highestPuff = float.NaN, widestPuff;
        private static readonly System.Random rng = new System.Random(1986);

        // drawing
        private static GameObject root;
        private static Transform cupT, holderT, bitsT;
        private static MeshRenderer[] renderers;
        private static MeshRenderer liquidRenderer;
        private static CoffeeModel.Surface surface;
        private static Il2CppStructArray<Vector3> liquidVerts, bitVerts;
        private static float[] drawnTops;
        private static Mesh bitsMesh;
        private static Material material;
        private static Texture2D palette, glow;
        private static int layer = -1;
        private static float nextMaterialCheck, nextEyeCheck, lastShown = -10f, cockpitSince = -1f;
        private static int seenSpace = -1;
        private static bool shown, raniaMaterial, failed, colored;
        private static int errors, logs, costFrames;
        private static float costSum, costWorst, nextNanWarning;

        // ---------------------------------------------------------------- every frame

        /// <summary>Right before rendering, before the hands (a holding hand is drawn round the cup).</summary>
        public static void Update()
        {
            if (failed) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                bool setup = !CoffeeMount.Valid || root == null;
                Frame();
                float ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                if (shown && !setup)
                {
                    costSum += ms;
                    costFrames++;
                    costWorst = MathF.Max(costWorst, ms);
                }
            }
            catch (Exception e)
            {
                failed = ++errors >= 5;
                if (failed) Hide();
                Plugin.L.LogError("Cockpit coffee" + (failed ? " (off for this session)" : "") + ": " + e);
            }
        }

        private static void Frame()
        {
            float now = Time.unscaledTime;
            TrackFlights(now);
            bool on = VRConfig.CockpitCoffee.Value && CameraDriver.Mode == ViewMode.Cockpit && CameraDriver.CockpitCamera != null;
            if (!on) { cockpitSince = -1f; Hide(); return; }
            if (cockpitSince < 0f) cockpitSince = now;
            if (TransitionGuard.Active)
            {
                // build, then place under the fade (after the lever is measured), one setup per frame
                if (TransitionGuard.Settling)
                {
                    if (root == null) { if (SetupSlot.Take()) Create(); }
                    else if (!CoffeeMount.Valid && (!VRConfig.GrabLever.Value || LeverModel.Ready) && SetupSlot.Take()) CoffeeMount.Place();
                }
                Hide();
                return;
            }
            if (root == null)
            {
                if (!SetupSlot.Take()) { Hide(); return; } // one heavy setup per frame
                Create();
            }

            // re-place if the seat moved or the lever got measured later; only with the cup in its holder
            if (CoffeeMount.Valid && now >= nextEyeCheck && (where == Where.Holder || !shown))
            {
                nextEyeCheck = now + 1f;
                if (CoffeeMount.EyesMoved() || (!CoffeeMount.LeverMeasured && LeverModel.Ready)) CoffeeMount.Forget();
            }
            bool placed = CoffeeMount.Valid;
            // wait a moment for LeverGrab to measure the lever
            bool leverPending = VRConfig.GrabLever.Value && !LeverModel.Ready && now - cockpitSince < 3f;
            if (!placed && (now - cockpitSince < 1f || leverPending || !SetupSlot.Take() || !CoffeeMount.Place())) { Hide(); return; }
            if (CoffeeMount.HaveColor && !colored) { colored = true; CoffeeModel.SetHolderColor(CoffeeMount.ConsoleColor); FillPalettes(); }
            if (!CoffeeMount.HasSpot) { Hide(); return; }
            ReadCockpit();
            if (!filled) refillWhy = "the first flight of the session";
            if (refillWhy != null) Refill(refillWhy);
            if (!placed || now - lastShown > 0.5f) Restart(); // new place or back in the cockpit: in the holder, still
            FollowRania();
            KeepLayer();

            float frame = Mathf.Max(Time.unscaledDeltaTime, 1e-4f), dt = Mathf.Min(frame, MaxSteps * Step);
            bool hitch = frame > MaxSteps * Step; // a hitch measures no motion (no fake knock)
            ReadCar(frame, hitch);
            // recenter, cut or hitch: not real motion, forget velocities
            if (CameraDriver.SpaceVersion != seenSpace || CameraDriver.CutThisFrame || hitch)
            {
                seenSpace = CameraDriver.SpaceVersion;
                haveMotion = haveRot = haveFelt = false;
                trailCount = 0;
            }
            Simulate(frame);
            ReadHands();
            if (holder != null && (!holder.Valid || VRUI.Interactive)) Release(); // tracking lost or menu open
            Move(frame);
            Slosh(dt, frame, now);
            if (levelWhenTyping >= 0f && Wrist.Typing)
            {
                CoffeeLiquid.Level = levelWhenTyping;
                Log($"Coffee: the level set to {levelWhenTyping * 100f:0.0} cm while a line types out on the wrist (test).");
                levelWhenTyping = -1f;
            }
            Emptied();
            Steam(frame, now);
            Fall(frame);
            Draw();
            GripPoses(frame);
            Show();
        }

        /// <summary>Fresh cup on the first flight, and after a few seconds on foot.</summary>
        private static void TrackFlights(float now)
        {
            var mode = CameraDriver.Mode;
            if (mode == ViewMode.OnFoot) { if (onFootSince < 0f) onFootSince = now; }
            else if (mode == ViewMode.Cockpit || mode == ViewMode.Chase)
            {
                if (onFootSince >= 0f && now - onFootSince >= OnFootForRefill && filled) refillWhy = $"a new flight after {now - onFootSince:0} s on foot";
                onFootSince = -1f;
            }
        }

        private static void Refill(string why)
        {
            bool wasEmpty = filled && emptyNoted;
            CoffeeLiquid.Reset(CoffeeModel.FullLevel);
            filledAt = Time.unscaledTime;
            filled = true;
            sips = 0;
            refillWhy = null;
            emptyNoted = false;
            nextPuff = 0f; // steam at once
            if (freshNote) Wrist.Note("Coffee", FreshNote);
            Plugin.L.LogInfo($"Coffee: a full, hot cup ({why}){(wasEmpty ? ", the last one was empty" : "")}{(freshNote ? "; the wrist says so" : "")}.");
            freshNote = false;
        }

        /// <summary>Cup ran dry. The first time per session the wrist explains the refill rule.</summary>
        private static void Emptied()
        {
            if (emptyNoted || !filled || CoffeeLiquid.Level > EmptyLevel) return;
            emptyNoted = true;
            bool hint = !emptyHinted;
            if (hint)
            {
                emptyHinted = freshNote = true;
                Wrist.Note("Coffee", EmptyNote);
            }
            Log($"Coffee: the cup is empty; a full one comes with the next flight (getting back in after {OnFootForRefill:0} s on foot){(hint ? $"; the wrist says '{EmptyNote}'" : "")}.");
        }

        /// <summary>Cup back in the holder, still, history and particles cleared.</summary>
        private static void Restart()
        {
            holder = null;
            where = Where.Holder;
            cupPos = seatPos;
            cupRot = seatRot;
            cupVel = cupAccel = N.Vector3.Zero;
            cupScale = growT = 1f;
            farFrom = -1f;
            trailCount = 0;
            splashed = 0f;
            haveMotion = haveRot = haveFelt = haveCar = false;
            accumulator = 0f;
            CoffeeLiquid.Still();
            for (int i = 0; i < Puffs; i++) puffs[i].Alive = false;
            for (int i = 0; i < Drops; i++) drops[i].Alive = false;
            spilling = false;
            dropDebt = 0f;
            foreach (var h in hands) { h.Blend = 0f; h.Pose.Weight = 0f; }
        }

        // ---------------------------------------------------------------- the cockpit, the HOVA, the hands

        private static void ReadCockpit()
        {
            CameraDriver.WorldToCockpit(Vector3.zero, Quaternion.identity, out _, out var r);
            toSet = N.Quaternion.Normalize(Q(r));
            toWorld = N.Quaternion.Conjugate(toSet);
            CameraDriver.TrackingToWorld(Vector3.zero, Quaternion.identity, out _, out var tw);
            CameraDriver.WorldToCockpit(Vector3.zero, tw, out _, out var ts);
            trackToSet = N.Quaternion.Normalize(Q(ts));

            var parent = CoffeeMount.Parent;
            seatPos = V(parent.TransformPoint(CoffeeMount.SeatLocal));
            seatUp = N.Vector3.Normalize(V(parent.TransformDirection(CoffeeMount.UpLocal)));
            seatFwd = V(parent.TransformDirection(CoffeeMount.AheadLocal));
            seatFwd = N.Vector3.Normalize(seatFwd - seatUp * N.Vector3.Dot(seatFwd, seatUp));
            seatRight = N.Vector3.Cross(seatUp, seatFwd);
            seatRot = Look(seatFwd, seatUp);
            eyeRest = V(parent.TransformPoint(CoffeeMount.EyeLocal));

            CameraDriver.WorldToCockpit(CameraDriver.HeadWorldPosition, CameraDriver.HeadWorldRotation, out var hp, out var hr);
            headPos = V(hp);
            headRot = N.Quaternion.Normalize(Q(hr));
            mouth = headPos + Rotate(headRot, MouthFromEyes);
        }

        /// <summary>HOVA acceleration (world) from its smoothed rigidbody velocity. A teleport jump is ignored.</summary>
        private static void ReadCar(float dt, bool hitch)
        {
            var g = Global.Instance;
            var car = g != null ? g.playerCar : null;
            var body = car != null ? car.rBody : null;
            var v = V(body != null ? body.velocity : CameraDriver.AnchorVelocity);
            if (!haveCar || hitch || (v - carVel).Length() > 60f) { carVel = lastRawVel = v; carAccel = N.Vector3.Zero; haveCar = true; }
            else
            {
                var before = carVel;
                carVel += (v - carVel) * (1f - MathF.Exp(-dt / CarSmoothing));
                carAccel = (carVel - before) / dt;
                float a = carAccel.Length();
                if (a > MaxCarPush) carAccel *= MaxCarPush / a;
            }
            // a big push only hits at once with a real collision; the game moving the HOVA by itself is averaged
            var push = Rotate(toSet, carAccel);
            float now = Time.unscaledTime, across = (push - seatUp * N.Vector3.Dot(push, seatUp)).Length();
            bool knock = now - Haptics.LastCollision < Haptics.CollisionWindow && Haptics.LastCollisionSpeed >= KnockSpeed;
            bool jolt = across > CoffeeLiquid.Jolt;
            if (jolt && !jolting)
            {
                if (knock) knocks++; else kicks++;
                if (now >= nextJoltLog)
                {
                    nextJoltLog = now + 1f;
                    var dv = Rotate(toSet, v - lastRawVel);
                    Log($"Coffee: the HOVA's measured push jumped to {across:0} m/s2 across ({N.Vector3.Dot(push, seatFwd):0.0} ahead, {N.Vector3.Dot(push, seatRight):0.0} right, {N.Vector3.Dot(push, seatUp):0.0} up), " +
                        $"its velocity changed by {N.Vector3.Dot(dv, seatFwd):0.0} ahead, {N.Vector3.Dot(dv, seatRight):0.0} right, {N.Vector3.Dot(dv, seatUp):0.0} up m/s in {dt * 1000f:0} ms " +
                        $"({lastRawVel.Length():0.0} to {v.Length():0.0} m/s{(body != null && body.isKinematic ? ", its rigidbody kinematic" : "")}); " +
                        (knock ? $"a collision at {Haptics.LastCollisionSpeed:0.0} m/s {(now - Haptics.LastCollision) * 1000f:0} ms before: felt at once." : "no collision: felt averaged, not as a knock."));
                }
            }
            jolting = jolt;
            lastRawVel = v;
            hovaPush = CoffeeLiquid.HovaPush(push, seatUp, dt, knock);
            if (Time.unscaledTime < devPushUntil) hovaPush += devPush;
        }

        /// <summary>Hands in the set and their distance to the cup axis. Palm frame only read near the cup.</summary>
        private static void ReadHands()
        {
            foreach (var h in hands)
            {
                h.Valid = false;
                h.Gap = float.PositiveInfinity;
                if (sim.On && sim.Left == h.Left)
                {
                    h.Valid = !VRUI.Interactive;
                    h.CtrlPos = h.Grip = sim.Grip;
                    h.CtrlRot = sim.Rot;
                    h.Back = Rotate(sim.Rot, h.Left ? -N.Vector3.UnitX : N.Vector3.UnitX);
                }
                else
                {
                    var pose = h.Left ? SteamVRInput.LeftHand : SteamVRInput.RightHand;
                    if (!pose.Valid || VRUI.Interactive) { Report(h); continue; }
                    h.Valid = true;
                    h.CtrlPos = TrackingToSet(pose.Position);
                    h.CtrlRot = N.Quaternion.Normalize(trackToSet * Q(pose.Rotation));
                    var forward = Rotate(h.CtrlRot, N.Vector3.UnitZ);
                    h.Grip = h.CtrlPos - forward * TipToGrip;
                    h.Back = Rotate(h.CtrlRot, h.Left ? -N.Vector3.UnitX : N.Vector3.UnitX);
                    // near the cup: the drawn closed hand (TrackedHand palm frame)
                    if (ToAxis(h.CtrlPos) < 0.3f && TrackedHand.SkeletonFrame(h.Left, out var f))
                    {
                        h.Grip = TrackingToSet(f.GripCentre(GripRadius));
                        h.Back = Rotate(trackToSet, V(f.Back));
                    }
                }
                if (h.Valid) h.Gap = ToAxis(h.Grip);
                Report(h);
            }
        }

        /// <summary>Distance for GrabArbiter, only within reach and while the cup is free.</summary>
        private static void Report(HandState h) =>
            GrabArbiter.Report(GrabArbiter.Coffee, h.Left, shown && h.Valid && Free && h.Gap <= GripNear ? h.Gap : float.PositiveInfinity);

        /// <summary>Can be grabbed: not held, not fading.</summary>
        private static bool Free => where != Where.Held && where != Where.Fading;

        private static float ToAxis(N.Vector3 p)
        {
            var up = Rotate(cupRot, N.Vector3.UnitY);
            var a = cupPos + up * 0.01f;
            float t = Math.Clamp(N.Vector3.Dot(p - a, up), 0f, CoffeeModel.Height - 0.02f);
            return (p - (a + up * t)).Length();
        }

        private static N.Vector3 TrackingToSet(Vector3 p)
        {
            CameraDriver.TrackingToWorld(p, Quaternion.identity, out var w, out _);
            CameraDriver.WorldToCockpit(w, Quaternion.identity, out var s, out _);
            return V(s);
        }

        // ---------------------------------------------------------------- the grip

        /// <summary>After the wheel, lever and charm. A strafe press near the cup is the cup's and never strafes.</summary>
        public static void UpdateGrip()
        {
            bool live = !failed && shown;
            if (!live && holder != null) Release();
            bool simLeft = sim.On && sim.Left, simRight = sim.On && !sim.Left;
            Grip(left, simLeft ? sim.Down : SteamVRInput.StrafeLeft.Down, simLeft ? TakeSimPress() : SteamVRInput.StrafeLeft.Pressed, InputControlType.LeftBumper, live);
            Grip(right, simRight ? sim.Down : SteamVRInput.StrafeRight.Down, simRight ? TakeSimPress() : SteamVRInput.StrafeRight.Pressed, InputControlType.RightBumper, live);
        }

        private static void Grip(HandState h, bool down, bool pressed, InputControlType bumper, bool live)
        {
            if (!down)
            {
                if (holder == h) Release(true);
                h.Swallow = false;
                GrabArbiter.Let(GrabArbiter.Coffee, h.Left);
                return;
            }
            float now = Time.unscaledTime;
            bool free = Free;
            if (pressed) h.PressedAt = now;
            // grab window: pressing on the way to the cup still counts
            if (live && free && !h.Swallow && now - h.PressedAt <= GrabWindow && h.Valid && h.Gap <= GripNear && GrabArbiter.Claim(GrabArbiter.Coffee, h.Left))
                h.Swallow = true;
            if (live && free && h.Swallow && now - h.PressedAt <= GrabWindow && h.Valid && h.Gap <= GrabGap) Grab(h);
            if (h.Swallow) VirtualGamepad.Set(bumper, 0f);
        }

        private static void Grab(HandState h)
        {
            string from = where == Where.Lying ? $" where it lay ({Pose()})" : where == Where.Flying ? " in the air" : "";
            holder = h;
            where = Where.Held;
            var inv = N.Quaternion.Conjugate(h.CtrlRot);
            holdRot = N.Quaternion.Normalize(inv * cupRot);
            holdPos = Rotate(inv, cupPos - h.CtrlPos);
            // palm side of the cup, kept for the whole hold
            var up = Rotate(cupRot, N.Vector3.UnitY);
            var side = h.Back - up * N.Vector3.Dot(h.Back, up);
            if (side.LengthSquared() < 0.04f) side = h.Grip - cupPos - up * N.Vector3.Dot(h.Grip - cupPos, up);
            if (side.LengthSquared() < 1e-6f) side = -seatFwd;
            contact = Rotate(N.Quaternion.Conjugate(cupRot), N.Vector3.Normalize(side));
            contact = N.Vector3.Normalize(new N.Vector3(contact.X, 0f, contact.Z) + new N.Vector3(1e-4f, 0f, 0f));
            haveMotion = false;
            trailCount = 0;
            farFrom = -1f;
            float heat = Heat;
            Haptics.Pulse(h.Left, 0.02f, 0.3f, 150f);
            if (heat > 0.2f) Haptics.Pulse(h.Left, 0.08f, 0.05f + 0.1f * heat, 50f, 0.05f); // warm in the hand
            Log($"Coffee: taken with the {(h.Left ? "left" : "right")} hand{from} ({h.Gap * 100f:0.0} cm from the cup's axis), {CoffeeLiquid.Level * 100f:0.0} cm of coffee, heat {heat:0.00}.");
        }

        /// <summary>Over the holder: drops in. Let go elsewhere: thrown. Lost tracking, menu or no CabinShape: floats back.</summary>
        private static void Release(bool letGo = false)
        {
            if (holder == null) return;
            bool byLeft = holder.Left;
            holder = null;
            var rel = cupPos - seatPos;
            float above = N.Vector3.Dot(rel, seatUp), off = (rel - seatUp * above).Length();
            float tilt = MathF.Acos(Math.Clamp(N.Vector3.Dot(Rotate(cupRot, N.Vector3.UnitY), seatUp), -1f, 1f)) * Mathf.Rad2Deg;
            var toSeat = N.Quaternion.Conjugate(seatRot);
            moveFrom = Rotate(toSeat, rel);
            moveFromRot = N.Quaternion.Normalize(toSeat * cupRot);
            moveT = 0f;
            if (off <= DropRadius && above <= DropAbove && above >= -DropBelow && tilt <= DropTilt)
            {
                where = Where.Dropping;
                moveTime = DropTime;
                return;
            }
            if (letGo && CabinShape.Ready)
            {
                LeftHandWith(out var vel, out var spin);
                Throw(byLeft, vel, spin, vel.Length() > 0.5f ? "thrown" : "let go");
                return;
            }
            where = Where.Returning;
            float distance = rel.Length();
            moveTime = Math.Clamp(0.45f + 0.8f * distance, 0.45f, 0.9f);
            // arc up and over so it doesn't pass through the console
            float lift = MathF.Max(above, 0f) + 0.06f;
            moveVia = moveFrom * 0.5f + N.Vector3.UnitY * (lift - 0.5f * above);
            Log($"Coffee: let go {distance * 100f:0} cm from the holder ({off * 100f:0} cm aside, {above * 100f:0} cm up, tilted {tilt:0} deg); it floats back.");
        }

        // ---------------------------------------------------------------- where the cup is

        private static void Move(float dt)
        {
            if (growT < 1f && where != Where.Fading)
            {
                growT = MathF.Min(1f, growT + dt / GrowTime);
                cupScale = growT * growT * (3f - 2f * growT);
            }
            switch (where)
            {
                case Where.Holder:
                    cupPos = seatPos;
                    cupRot = seatRot;
                    break;
                case Where.Held:
                    var h = holder;
                    cupRot = N.Quaternion.Normalize(h.CtrlRot * holdRot);
                    cupPos = h.CtrlPos + Rotate(h.CtrlRot, holdPos);
                    Remember();
                    break;
                case Where.Flying:
                case Where.Lying:
                case Where.Fading:
                {
                    // bars every frame in flight; lying, only if wheel or lever is close
                    if (where == Where.Flying || (where == Where.Lying && MathF.Min(wheelGapAtRest, leverGapAtRest) < 0.3f)) PlaceBars();
                    if (where == Where.Flying) Fly(dt);
                    else if (where == Where.Lying) Rest();
                    else Fade();
                    cupPos = seatPos + Rotate(seatRot, CoffeeBody.Pos);
                    cupRot = N.Quaternion.Normalize(seatRot * CoffeeBody.Rot);
                    cupVel = Rotate(seatRot, CoffeeBody.Vel);
                    cupAccel = where == Where.Fading ? N.Vector3.Zero : Rotate(seatRot, CoffeeBody.Accel);
                    lastCupPos = cupPos;
                    haveMotion = true;
                    return;
                }
                case Where.Dropping:
                {
                    moveT = MathF.Min(1f, moveT + dt / moveTime);
                    float e = moveT * moveT; // falls the last cm
                    cupPos = seatPos + Rotate(seatRot, moveFrom * (1f - e));
                    cupRot = N.Quaternion.Normalize(seatRot * N.Quaternion.Slerp(moveFromRot, N.Quaternion.Identity, MathF.Min(1f, moveT * 1.5f)));
                    if (moveT >= 1f) Seated(true);
                    break;
                }
                case Where.Returning:
                {
                    moveT = MathF.Min(1f, moveT + dt / moveTime);
                    float e = moveT * moveT * moveT * (moveT * (6f * moveT - 15f) + 10f);
                    float u = 1f - e;
                    cupPos = seatPos + Rotate(seatRot, moveFrom * (u * u) + moveVia * (2f * u * e));
                    float r = MathF.Min(1f, moveT * 1.6f);
                    cupRot = N.Quaternion.Normalize(seatRot * N.Quaternion.Slerp(moveFromRot, N.Quaternion.Identity, r * r * (3f - 2f * r)));
                    if (moveT >= 1f) Seated(false);
                    break;
                }
            }

            // cup acceleration in the set, smoothed against tracking jitter
            if (where == Where.Holder || !haveMotion)
            {
                cupVel = cupAccel = N.Vector3.Zero;
                lastCupPos = cupPos;
                haveMotion = where != Where.Holder;
                return;
            }
            var v = (cupPos - lastCupPos) / dt;
            lastCupPos = cupPos;
            var before = cupVel;
            cupVel += (v - cupVel) * (1f - MathF.Exp(-dt / HandSmoothing));
            var a = (cupVel - before) / dt;
            cupAccel += (a - cupAccel) * (1f - MathF.Exp(-dt / HandSmoothing));
            float m = cupAccel.Length();
            if (m > MaxHandPush) cupAccel *= MaxHandPush / m;
        }

        // ---------------------------------------------------------------- the cup let go

        /// <summary>Records the held cup's middle and rotation (holder frame) for the throw.</summary>
        private static void Remember()
        {
            var toSeat = N.Quaternion.Conjugate(seatRot);
            trailRot[trailHead] = N.Quaternion.Normalize(toSeat * cupRot);
            trailPos[trailHead] = ToSeat(cupPos) + Rotate(trailRot[trailHead], new N.Vector3(0f, CoffeeBody.Middle, 0f));
            trailTime[trailHead] = Time.unscaledTime;
            trailHead = (trailHead + 1) % Trail;
            trailCount = Math.Min(trailCount + 1, Trail);
        }

        /// <summary>Release velocity and spin over the last ThrowWindow (holder frame).</summary>
        private static void LeftHandWith(out N.Vector3 vel, out N.Vector3 spin)
        {
            vel = spin = N.Vector3.Zero;
            if (trailCount < 2) return;
            int newest = (trailHead - 1 + Trail) % Trail, oldest = newest;
            for (int k = 1; k < trailCount; k++)
            {
                oldest = (newest - k + Trail) % Trail;
                if (trailTime[newest] - trailTime[oldest] >= ThrowWindow) break;
            }
            float dt = trailTime[newest] - trailTime[oldest];
            if (dt < 1e-3f) return;
            vel = (trailPos[newest] - trailPos[oldest]) / dt;
            var d = N.Quaternion.Normalize(trailRot[newest] * N.Quaternion.Conjugate(trailRot[oldest]));
            if (d.W < 0f) d = N.Quaternion.Negate(d);
            float s = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
            if (s > 1e-6f) spin = new N.Vector3(d.X, d.Y, d.Z) * (2f * MathF.Atan2(s, d.W) / (s * dt));
            if (!CoffeeLiquid.Finite(vel)) vel = N.Vector3.Zero;
            if (!CoffeeLiquid.Finite(spin)) spin = N.Vector3.Zero;
            if (vel.Length() > MaxThrow) vel *= MaxThrow / vel.Length();
            if (spin.Length() > MaxSpin) spin *= MaxSpin / spin.Length();
        }

        /// <summary>Starts a flight with this velocity and spin (holder frame).</summary>
        private static void Throw(bool byLeft, N.Vector3 vel, N.Vector3 spin, string how, bool byHand = true)
        {
            CoffeeBody.Start(ToSeat(cupPos), N.Quaternion.Conjugate(seatRot) * cupRot, vel, spin, CabinGravity());
            lastMiddle = CoffeeBody.MiddlePoint + CoffeeMount.SeatFromEyes;
            where = Where.Flying;
            flyingSince = Time.unscaledTime;
            stillFor = 0f;
            bounces = 0;
            bouncedOff = 0;
            farFrom = -1f;
            farLogged = false;
            settledPush = 0f;
            hitLogged = false;
            throwLeft = byLeft;
            throws++;
            wakes = 0;
            Log($"Coffee: {how}{(byHand ? $" with the {(byLeft ? "left" : "right")} hand" : "")} at {vel.Length():0.0} m/s ({vel.X:0.0} right, {vel.Y:0.0} up, {vel.Z:0.0} ahead), " +
                $"spinning {spin.Length():0} rad/s, {Place(CoffeeBody.Pos)}, {CoffeeLiquid.Level * 100f:0.0} cm of coffee.");
        }

        /// <summary>Lever and wheel as CoffeeBody bars, posed this frame. Unmeasured wheel (WheelGrab off) isn't solid.</summary>
        private static void PlaceBars()
        {
            CoffeeBody.BarCount = 0;
            var lever = CoffeeMount.LeverT;
            if (lever != null && LeverModel.Ready)
            {
                LeverModel.Shape(lever, out var pivotU, out var handleU, out var barU);
                var handle = ToSeat(V(handleU));
                var bar = SeatDir(V(barU)) * LeverModel.BarHalfLength;
                AddBar(ToSeat(V(pivotU)), handle, LeverModel.BarRadius, CoffeeBody.Lever);
                AddBar(handle - bar, handle + bar, LeverModel.BarRadius, CoffeeBody.Lever);
            }
            else if (lever != null && CoffeeMount.LeverBoxed)
                AddBar(CoffeeMount.LeverFoot - CoffeeMount.SeatFromEyes, CoffeeMount.LeverTop - CoffeeMount.SeatFromEyes, 0.015f, CoffeeBody.Lever);
            var wheel = CoffeeMount.WheelT;
            if (wheel != null && WheelModel.Ready)
            {
                WheelModel.Shape(wheel, out var hubU, out _, out var twelveU, out var threeU);
                var hub = ToSeat(V(hubU));
                var twelve = SeatDir(V(twelveU));
                var three = SeatDir(V(threeU));
                float rim = WheelModel.RimRadius, tube = WheelModel.TubeRadius;
                for (int k = 0; k < RimPieces; k++)
                    AddBar(hub + Round(twelve, three, k * 360f / RimPieces) * rim, hub + Round(twelve, three, (k + 1) * 360f / RimPieces) * rim, tube, CoffeeBody.Wheel);
                float inner = rim - tube, tubeDegrees = tube / inner * Mathf.Rad2Deg;
                for (int i = 0; i < WheelModel.SpokeCount; i++)
                {
                    WheelModel.Spoke(i, out float middle, out float half);
                    float side = MathF.Max(0f, half - tubeDegrees);
                    int n = Math.Clamp((int)MathF.Ceiling(2f * side / tubeDegrees) + 1, 1, 5);
                    for (int k = 0; k < n; k++)
                        AddBar(hub, hub + Round(twelve, three, middle + (n == 1 ? 0f : -side + 2f * side * k / (n - 1))) * inner, tube, CoffeeBody.Wheel);
                }
            }
        }

        /// <summary>Direction in the wheel plane, degrees from 12 o'clock toward 3.</summary>
        private static N.Vector3 Round(N.Vector3 twelve, N.Vector3 three, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad;
            return twelve * MathF.Cos(r) + three * MathF.Sin(r);
        }

        private static void AddBar(N.Vector3 a, N.Vector3 b, float radius, int tag)
        {
            if (CoffeeBody.BarCount >= CoffeeBody.MaxBars) return;
            CoffeeBody.Bars[CoffeeBody.BarCount++] = new CoffeeBody.Bar { A = a, B = b, Radius = radius, Tag = tag };
        }

        /// <summary>One frame of flight, then check: left the cabin, landed in the holder, or at rest.</summary>
        private static void Fly(float frame)
        {
            float now = Time.unscaledTime, t = MathF.Min(frame, MaxSteps * Step);
            CoffeeBody.Eyes = CoffeeMount.SeatFromEyes;
            if (!CoffeeBody.Advance(t, CabinGravity(), out bool touching, out float hit, out int what)) { Lose("its flight went out of bounds", LostGap, true); return; }
            if (hit > 0.25f) Hit(hit, now, what);

            // through a pane or out a gap: gone at once
            var middle = CoffeeBody.MiddlePoint + CoffeeMount.SeatFromEyes;
            int pane = CabinShape.Crossed(lastMiddle, middle);
            lastMiddle = middle;
            if (pane >= 0) { Lose($"it went through {CabinShape.PaneName(pane)}", LostGap, true); return; }
            if (!CabinShape.InBox(middle)) { Lose("it left the cabin", LostGap, true); return; }
            if (now - flyingSince > MaxFlight) { Lose($"it never came to rest in {MaxFlight:0} s", FarGap); return; }
            if (Caught()) return;
            float speed = CoffeeBody.Vel.Length();
            if (touching && speed < RestSpeed && CoffeeBody.Spin.Length() < RestSpin) stillFor += t;
            else stillFor = 0f;
            if (stillFor >= RestTime) Settle(now);
            // out of reach and kept sliding by the HOVA: send it back anyway
            else if (farFrom >= 0f && now - farFrom >= FarWait && touching && speed < 0.3f) Lose("out of reach", FarGap);
        }

        private static string Surface(int what) => what >= 0 ? CabinShape.PaneName(what) : what == CoffeeBody.Wheel ? "the wheel"
            : what == CoffeeBody.Lever ? "the lever" : what == CoffeeBody.Ring ? "the holder's ring" : "the cockpit";

        private static string FromEyes()
        {
            var e = CoffeeBody.MiddlePoint + CoffeeMount.SeatFromEyes;
            return $"{e.X * 100f:0} cm right, {e.Y * 100f:0} up, {e.Z * 100f:0} ahead of the eyes";
        }

        /// <summary>Impact: haptic tick (nearby hand, else the thrower), ripple and splash.</summary>
        private static void Hit(float speed, float now, int what)
        {
            if (speed > CoffeeBody.BounceFrom)
            {
                bounces++;
                // log each pane, wheel or lever once per flight
                int bit = what >= 0 ? Math.Min(what, 61) : what == CoffeeBody.Wheel ? 62 : what == CoffeeBody.Lever ? 63 : -1;
                if (bit >= 0 && (bouncedOff & (1UL << bit)) == 0)
                {
                    bouncedOff |= 1UL << bit;
                    Log($"Coffee: bounced off {Surface(what)} at {speed:0.0} m/s after {now - flyingSince:0.00} s, {FromEyes()}, in the cabin.");
                }
            }
            CoffeeLiquid.Knock(MathF.Min(0.003f, speed * 0.0012f));
            float splash = MathF.Min(CoffeeLiquid.Level, MathF.Max(0f, speed - SplashFrom) * SplashPer);
            if (splash > 0f)
            {
                CoffeeLiquid.Level -= splash;
                splashed += splash;
            }
            if (now - lastTick > 0.12f)
            {
                lastTick = now;
                var near = NearestHand(0.25f);
                Haptics.Pulse(near != null ? near.Left : throwLeft, 0.012f, Math.Clamp(0.06f + 0.12f * speed, 0.06f, 0.35f), 200f);
            }
            if (hitLogged) return;
            hitLogged = true;
            Log($"Coffee: first knock at {speed:0.0} m/s after {now - flyingSince:0.00} s on {Surface(what)}, {Place(CoffeeBody.Pos)}.");
        }

        /// <summary>Upright over the holder and coming down: lands in it.</summary>
        private static bool Caught()
        {
            float off = MathF.Sqrt(CoffeeBody.Pos.X * CoffeeBody.Pos.X + CoffeeBody.Pos.Z * CoffeeBody.Pos.Z);
            if (off > DropRadius || CoffeeBody.Pos.Y > DropAbove || CoffeeBody.Pos.Y < -DropBelow || CoffeeBody.Vel.Y > 0.3f) return false;
            if (CoffeeBody.Up.Y < MathF.Cos(DropTilt * Mathf.Deg2Rad)) return false;
            moveFrom = CoffeeBody.Pos;
            moveFromRot = CoffeeBody.Rot;
            moveT = 0f;
            moveTime = DropTime;
            where = Where.Dropping;
            Log($"Coffee: it landed in the holder after {Time.unscaledTime - flyingSince:0.0} s.");
            return true;
        }

        /// <summary>Came to rest. Out of reach, it goes back after FarWait.</summary>
        private static void Settle(float now)
        {
            where = Where.Lying;
            CoffeeBody.Stop();
            var g = CabinGravity();
            settledPush = MathF.Sqrt(g.X * g.X + g.Z * g.Z); // only a harder push wakes it
            wheelGapAtRest = CoffeeBody.Gap(CoffeeBody.Wheel);
            leverGapAtRest = CoffeeBody.Gap(CoffeeBody.Lever);
            float shoulder = ShoulderGap();
            bool far = shoulder > Reach;
            if (far && farFrom < 0f) farFrom = now;
            else if (!far) farFrom = -1f;
            if (wakes > 0 && far == farLogged) return;
            farLogged = far;
            Log($"Coffee: at rest after {now - flyingSince:0.0} s and {bounces} bounce(s){(wakes > 0 ? $", shaken loose {wakes} time(s)" : "")}, {Pose()}, {Place(CoffeeBody.Pos)} ({FromEyes()}), " +
                $"{shoulder * 100f:0} cm from the right shoulder ({(far ? $"out of reach: back to the holder in {MathF.Max(0f, FarWait - (now - farFrom)):0} s" : "within reach")}); " +
                $"{Clear("wheel", wheelGapAtRest)}, {Clear("lever", leverGapAtRest)}; {CoffeeLiquid.Level * 100f:0.0} cm of coffee left.");
        }

        private static string Clear(string what, float gap) =>
            float.IsPositiveInfinity(gap) ? $"no {what} to bump into" : gap < -0.002f ? $"INSIDE the {what} by {-gap * 100f:0.0} cm" : $"{MathF.Max(gap, 0f) * 100f:0.0} cm clear of the {what}";

        /// <summary>Lying still until a push past friction, a dive, or the wheel or lever moves it.</summary>
        private static void Rest()
        {
            var g = CabinGravity();
            float across = MathF.Sqrt(g.X * g.X + g.Z * g.Z), holds = CoffeeBody.Friction * MathF.Max(-g.Y, 0f);
            if (across < holds) settledPush = 0f; // push gone: reset the threshold
            string why = null;
            if (g.Y > -0.4f * Gravity) why = $"the HOVA dropping ({-g.Y:0.0} m/s2 of weight left)";
            else if (across > holds * WakeOver && across > settledPush + WakeAgain) why = $"a push of {across:0.0} m/s2 across, friction holding {holds:0.0}";
            else if (Moved(CoffeeBody.Gap(CoffeeBody.Wheel), wheelGapAtRest)) why = "the wheel moving";
            else if (Moved(CoffeeBody.Gap(CoffeeBody.Lever), leverGapAtRest)) why = "the lever moving";
            if (why != null)
            {
                where = Where.Flying;
                lastMiddle = CoffeeBody.MiddlePoint + CoffeeMount.SeatFromEyes;
                flyingSince = Time.unscaledTime;
                stillFor = 0f;
                if (wakes++ == 0) Log($"Coffee: shaken loose where it lay by {why}.");
                return;
            }
            if (farFrom >= 0f && Time.unscaledTime - farFrom >= FarWait) Lose("out of reach", FarGap);
        }

        /// <summary>A bar moved into the cup, or one it leaned on moved away.</summary>
        private static bool Moved(float gap, float atRest) => gap < -0.003f || (atRest < 0.01f && MathF.Abs(gap - atRest) > 0.003f);

        /// <summary>Fades out (or vanishes), then grows back in the holder.</summary>
        private static void Lose(string why, float gap, bool vanish = false)
        {
            where = Where.Fading;
            fadeAt = Time.unscaledTime - (vanish ? FadeTime : 0f);
            if (vanish) cupScale = 0f;
            fadeGap = gap;
            farFrom = -1f;
            CoffeeBody.Stop();
            Log($"Coffee: {why} ({Place(CoffeeBody.Pos)}, {FromEyes()}, {Pose()}); {(vanish ? "gone at once, back in the holder in a moment" : "it fades back to the holder")}.");
        }

        private static void Fade()
        {
            float t = Time.unscaledTime - fadeAt;
            if (t < FadeTime) { cupScale = 1f - t / FadeTime; return; }
            cupScale = 0f;
            if (t < FadeTime + fadeGap) return;
            where = Where.Holder;
            CoffeeBody.Pos = N.Vector3.Zero;
            CoffeeBody.Rot = N.Quaternion.Identity;
            growT = 0f;
            CoffeeLiquid.Still();
            Log("Coffee: back in the holder (faded back).");
        }

        private static float ShoulderGap() =>
            (CoffeeBody.Point(new N.Vector3(0f, CoffeeModel.GripHeight, 0f)) - (CoffeeMount.Shoulder - CoffeeMount.SeatFromEyes)).Length();

        private static string Pose()
        {
            var up = where == Where.Flying || where == Where.Lying || where == Where.Fading ? CoffeeBody.Up : SeatDir(Rotate(cupRot, N.Vector3.UnitY));
            float tilt = MathF.Acos(Math.Clamp(up.Y, -1f, 1f)) * Mathf.Rad2Deg;
            return tilt < 12f ? "upright" : tilt < 65f ? $"tilted {tilt:0} deg" : tilt < 115f ? $"on its side ({tilt:0} deg)" : $"upside down ({tilt:0} deg)";
        }

        private static string Place(N.Vector3 p) => $"{p.X * 100f:0} cm right, {p.Y * 100f:0} up, {p.Z * 100f:0} ahead of the holder";

        private static void Seated(bool dropped)
        {
            where = Where.Holder;
            CoffeeLiquid.Knock(dropped ? 0.0015f : 0.0006f);
            if (dropped)
            {
                var near = NearestHand(0.2f);
                if (near != null) Haptics.Pulse(near.Left, 0.012f, 0.25f, 220f);
            }
            Log($"Coffee: back in the holder ({(dropped ? "dropped in" : "floated back")}).");
        }

        private static HandState NearestHand(float within)
        {
            HandState best = null;
            float d = within;
            foreach (var h in hands)
                if (h.Valid && (h.CtrlPos - cupPos).Length() < d) { d = (h.CtrlPos - cupPos).Length(); best = h; }
            return best;
        }

        // ---------------------------------------------------------------- the coffee

        private static float Heat => MathF.Exp(-(Time.unscaledTime - filledAt) / CoolTime);

        /// <summary>Slosh at 360 Hz under felt gravity. Over the rim at the mouth is a sip, anywhere else a spill.</summary>
        private static void Slosh(float dt, float frame, float now)
        {
            var gSet = Rotate(toSet, new N.Vector3(0f, -Gravity, 0f));
            var car = Rotate(toSet, carAccel);
            if (!CoffeeLiquid.Finite(cupAccel)) cupAccel = N.Vector3.Zero;
            var felt = gSet - hovaPush - cupAccel * (where == Where.Returning ? 0.3f : 1f);
            var local = Rotate(N.Quaternion.Conjugate(cupRot), felt);
            if (!CoffeeLiquid.Finite(local)) local = new N.Vector3(0f, -Gravity, 0f);
            if (!haveFelt) { feltLast = local; haveFelt = true; }

            // the coffee doesn't turn with the cup about its axis (world, HOVA turns included)
            var worldRot = N.Quaternion.Normalize(toWorld * cupRot);
            if (haveRot)
            {
                var d = worldRot * N.Quaternion.Conjugate(lastWorldRot);
                if (d.W < 0f) d = N.Quaternion.Negate(d);
                var axis = Rotate(worldRot, N.Vector3.UnitY);
                CoffeeLiquid.Twist(2f * MathF.Atan2(N.Vector3.Dot(new N.Vector3(d.X, d.Y, d.Z), axis), d.W));
            }
            lastWorldRot = worldRot;
            haveRot = true;

            bool atMouth = where == Where.Held && (PourPoint() - mouth).Length() <= SipReach;
            bool canSpill = where == Where.Held || where == Where.Holder || where == Where.Flying || where == Where.Lying;
            float lost = 0f;
            accumulator += dt;
            int steps = 0;
            while (accumulator >= Step && steps < MaxSteps)
            {
                float f = Math.Clamp(1f - (accumulator - Step) / dt, 0f, 1f);
                lost += CoffeeLiquid.Step(Step, N.Vector3.Lerp(feltLast, local, f), canSpill && !atMouth);
                accumulator -= Step;
                steps++;
            }
            if (steps == MaxSteps) accumulator = 0f;
            feltLast = local;
            lost += splashed;
            splashed = 0f;
            if (!CoffeeLiquid.Healthy)
            {
                if (now >= nextNanWarning) Plugin.L.LogWarning("Coffee: its state went out of bounds; the surface is still again.");
                nextNanWarning = now + 10f;
                CoffeeLiquid.Heal();
                lost = 0f;
            }

            float fwd = N.Vector3.Dot(car, seatFwd), side = N.Vector3.Dot(car, seatRight), up = N.Vector3.Dot(car, seatUp);
            if (MathF.Abs(fwd) > MathF.Abs(peakFwd)) peakFwd = fwd;
            if (MathF.Abs(side) > MathF.Abs(peakSide)) peakSide = side;
            if (MathF.Abs(up) > MathF.Abs(peakUp)) peakUp = up;
            var across = hovaPush - seatUp * N.Vector3.Dot(hovaPush, seatUp);
            peakFelt = MathF.Max(peakFelt, across.Length());
            float over = CoffeeLiquid.Over();
            if (where == Where.Holder) // flight slosh only, not the hand's
            {
                peakTilt = MathF.Max(peakTilt, CoffeeLiquid.Tilt.Length());
                peakOver = MathF.Max(peakOver, over);
            }

            if (atMouth && CoffeeLiquid.Level > 0f && over > -SipLip && now - lastSip >= SipEvery) Sip(now);
            Spilled(lost, now, new N.Vector3(side, up, fwd));
        }

        /// <summary>Rim point on the high side of the surface (set space).</summary>
        private static N.Vector3 PourPoint()
        {
            var high = CoffeeLiquid.HighSide;
            if (high == N.Vector2.Zero) high = new N.Vector2(0f, 1f);
            float r = CoffeeModel.InnerRadius(CoffeeModel.Height) + 0.5f * CoffeeModel.Voxel;
            return cupPos + Rotate(cupRot, new N.Vector3(high.X * r, CoffeeModel.Height, high.Y * r));
        }

        private static void Sip(float now)
        {
            float before = CoffeeLiquid.Level;
            CoffeeLiquid.Level = MathF.Max(0f, before - SipStep);
            if (CoffeeLiquid.Level < 0.0005f) CoffeeLiquid.Level = 0f;
            CoffeeLiquid.Knock(0.0012f);
            sips++;
            lastSip = now;
            float heat = Heat;
            if (holder != null)
            {
                // warm swallow, then a smaller one
                Haptics.Pulse(holder.Left, 0.06f, 0.08f + 0.14f * heat, 50f);
                Haptics.Pulse(holder.Left, 0.05f, 0.06f + 0.1f * heat, 45f, 0.14f);
            }
            string said = "";
            if (CoffeeLiquid.Level <= 0f)
            {
                said = now - lastLine >= 2.5f ? Say(emptyLines, now) : "";
                Log($"Coffee: sip {sips}, the last ({before * 100f:0.0} cm to empty){(said.Length > 0 ? $", Rania: '{said}'" : ", quietly")}.");
                return;
            }
            if (sips == 1) said = Say(firstSipLines, now);
            else if (sips % 3 == 0 && now - lastLine >= LineGap && rng.NextDouble() < 0.6) said = Say(sipLines, now);
            Log($"Coffee: sip {sips} ({before * 100f:0.0} to {CoffeeLiquid.Level * 100f:0.0} cm, heat {heat:0.00}){(said.Length > 0 ? $", Rania: '{said}'" : "")}.");
        }

        /// <summary>Spawns drops while spilling; at the end of a big spill, Rania reacts.</summary>
        private static void Spilled(float lost, float now, N.Vector3 push)
        {
            if (lost > 0f)
            {
                spilled += lost;
                if (!spilling)
                {
                    spilling = true;
                    spillLost = spillPeak = 0f;
                    dropDebt = DropPerLevel; // first drop at once
                    // tick in the holding hand, or the right hand if near
                    if (holder != null) Haptics.Pulse(holder.Left, 0.012f, 0.12f, 180f);
                    else if (right.Valid && (right.CtrlPos - cupPos).Length() < NearHand) Haptics.Pulse(false, 0.01f, 0.08f, 180f);
                }
                spillLost += lost;
                lastSpillAt = now;
                if (CoffeeLiquid.Over() > spillPeak) { spillPeak = CoffeeLiquid.Over(); spillPush = push; }
                dropDebt += lost;
                for (int n = 0; dropDebt >= DropPerLevel && n < 3; n++)
                {
                    dropDebt -= DropPerLevel;
                    SpawnDrop();
                }
                return;
            }
            if (!spilling || now - lastSpillAt < 0.3f) return;
            spilling = false;
            spills++;
            string said = "";
            if (spillLost >= SpillLine && now - lastSpillLine >= SpillLineGap && now - lastLine >= 2f)
            {
                said = FailReaction(now);
                if (said.Length > 0) lastSpillLine = now;
            }
            string at = holder != null ? "in the hand" : where == Where.Holder ? "in the holder" : where == Where.Flying ? "in flight" : where == Where.Lying ? "where it lies" : "on the way";
            Log($"Coffee: spilled {spillLost * 1000f:0.0} mm ({at}, " +
                $"{spillPeak * 1000f:0} mm over the rim at worst, the HOVA pushing {spillPush.Z:0.0} ahead, {spillPush.X:0.0} right, {spillPush.Y:0.0} up m/s2); " +
                $"{CoffeeLiquid.Level * 100f:0.0} cm left{(said.Length > 0 ? $"; Rania: '{said}'" : "")}.");
        }

        // ---------------------------------------------------------------- steam and drops

        private static float SteamStrength()
        {
            float heat = Heat, h = Math.Clamp((heat - 0.12f) / 0.43f, 0f, 1f);
            float full = Math.Clamp((CoffeeLiquid.Level - 0.01f) / 0.035f, 0f, 1f);
            return h * h * (3f - 2f * h) * full;
        }

        private static float Rand(float a, float b) => a + (b - a) * (float)rng.NextDouble();

        // steam and drops live in the holder's frame, which turns and banks with the HOVA
        private static N.Vector3 ToSeat(N.Vector3 setPoint) => Rotate(N.Quaternion.Conjugate(seatRot), setPoint - seatPos);
        private static N.Vector3 SeatDir(N.Vector3 setDir) => Rotate(N.Quaternion.Conjugate(seatRot), setDir);

        /// <summary>Felt gravity in the cabin, holder frame.</summary>
        private static N.Vector3 CabinGravity() => SeatDir(Rotate(toSet, new N.Vector3(0f, -Gravity, 0f)) - hovaPush);

        private static void Steam(float dt, float now)
        {
            float strength = SteamStrength();
            float ceiling = ToSeat(eyeRest).Y - SteamCeiling;
            var up = SeatDir(Rotate(cupRot, N.Vector3.UnitY));
            var cup = ToSeat(cupPos);
            var surface = cup + up * (CoffeeModel.Floor + CoffeeLiquid.Level);
            bool steady = (where == Where.Holder || where == Where.Held || where == Where.Lying) && up.Y > 0.8f && cupScale > 0.9f;
            if (strength > 0.02f && steady && surface.Y < ceiling - 0.1f && CoffeeLiquid.Level > 0.003f && now >= nextPuff)
            {
                nextPuff = now + (0.75f - 0.5f * strength) * Rand(0.8f, 1.25f);
                for (int i = 0; i < Puffs; i++)
                {
                    if (puffs[i].Alive) continue;
                    puffs[i] = new Bit
                    {
                        Alive = true, Pos = surface + new N.Vector3(Rand(-0.008f, 0.008f), 0.003f, Rand(-0.008f, 0.008f)), Vel = N.Vector3.Zero,
                        Age = 0f, Life = Rand(1.8f, 2.6f), Size = Rand(0.003f, 0.0048f) * (0.6f + 0.4f * strength), Phase = Rand(0f, 6.28f),
                    };
                    break;
                }
            }

            // rises against felt gravity, leans a bit with the push and drifts back with speed
            var g = CabinGravity();
            var rise = -N.Vector3.Normalize(new N.Vector3(g.X * 0.4f, MathF.Min(g.Y, -1f), g.Z * 0.4f));
            var speed = SeatDir(Rotate(toSet, carVel));
            var draft = new N.Vector3(-speed.X, 0f, -speed.Z) * 0.0005f;
            if (draft.Length() > 0.004f) draft *= 0.004f / draft.Length();
            highestPuff = float.NaN;
            for (int i = 0; i < Puffs; i++)
            {
                ref var p = ref puffs[i];
                if (!p.Alive) continue;
                p.Age += dt;
                if (p.Pos.Y > ceiling) p.Age = MathF.Max(p.Age, p.Life - 0.12f); // keep it out of the view
                if (p.Age >= p.Life) { p.Alive = false; continue; }
                var wobble = new N.Vector3(MathF.Sin(p.Age * 4.1f + p.Phase) * 0.004f, 0f, MathF.Cos(p.Age * 3.3f + p.Phase) * 0.003f);
                p.Vel = rise * (0.04f * (1f - 0.35f * p.Age / p.Life)) + draft + wobble;
                p.Pos += p.Vel * dt;
                float rel = p.Pos.Y - ceiling;
                if (float.IsNaN(highestPuff) || rel > highestPuff) highestPuff = rel;
                widestPuff = MathF.Max(widestPuff, new N.Vector2(p.Pos.X - cup.X, p.Pos.Z - cup.Z).Length());
            }
        }

        private static void SpawnDrop()
        {
            for (int i = 0; i < Drops; i++)
            {
                if (drops[i].Alive) continue;
                var high = CoffeeLiquid.HighSide;
                var outward = SeatDir(Rotate(cupRot, new N.Vector3(high.X, 0f, high.Y)));
                var up = SeatDir(Rotate(cupRot, N.Vector3.UnitY));
                float push = Math.Clamp(CoffeeLiquid.Over() / 0.01f, 0.2f, 1f);
                drops[i] = new Bit
                {
                    Alive = true, Landed = false,
                    Pos = ToSeat(PourPoint()) + outward * 0.003f + new N.Vector3(Rand(-0.003f, 0.003f), Rand(-0.002f, 0.002f), Rand(-0.003f, 0.003f)),
                    Vel = SeatDir(cupVel) + outward * (0.15f + 0.3f * push) + up * Rand(0.02f, 0.12f) + new N.Vector3(Rand(-0.04f, 0.04f), Rand(-0.02f, 0.02f), Rand(-0.04f, 0.04f)),
                    Age = 0f, Life = Rand(2f, 3f), Size = Rand(0.0022f, 0.0034f),
                };
                return;
            }
        }

        /// <summary>Drops fall, land on CabinShape (or a flat guess without it) and fade.</summary>
        private static void Fall(float dt)
        {
            var g = CabinGravity();
            var eyes = CoffeeMount.SeatFromEyes;
            bool cabin = CabinShape.Ready;
            for (int i = 0; i < Drops; i++)
            {
                ref var d = ref drops[i];
                if (!d.Alive) continue;
                if (d.Landed)
                {
                    d.Age += dt;
                    if (d.Age >= d.Life) d.Alive = false;
                    continue;
                }
                float wasY = d.Pos.Y;
                d.Vel += g * dt;
                d.Pos += d.Vel * dt;
                if (d.Pos.Y < (cabin ? -1.2f : -0.6f) || !CoffeeLiquid.Finite(d.Pos)) { d.Alive = false; continue; }
                float floor;
                if (cabin)
                {
                    // ground under last frame's height (the holder ring is low enough to skip)
                    float ground = CabinShape.Ground(CabinShape.Column(d.Pos.X + eyes.X, d.Pos.Z + eyes.Z), wasY + eyes.Y);
                    floor = float.IsNaN(ground) ? float.NegativeInfinity : ground - eyes.Y;
                }
                else floor = new N.Vector2(d.Pos.X, d.Pos.Z).Length() < 0.25f ? 0f : -0.12f; // console near the holder, else lower
                if (d.Pos.Y > floor) continue;
                d.Pos.Y = floor + 0.0006f;
                d.Vel = N.Vector3.Zero;
                d.Landed = true;
                d.Age = 0f;
            }
        }

        // ---------------------------------------------------------------- drawing

        private static void Create()
        {
            root = new GameObject("CloudpunkVR_Coffee");
            UnityEngine.Object.DontDestroyOnLoad(root);
            palette = PaletteTexture("CloudpunkVR_CoffeePalette");
            glow = PaletteTexture("CloudpunkVR_CoffeeGlow");
            FillPalettes();
            material = new Material(Shader.Find("Standard")) { name = "CloudpunkVR_Coffee", mainTexture = palette, hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.25f);
            SetGlow(material);

            holderT = Part("Holder", CoffeeModel.BuildHolder(), root.transform);
            cupT = new GameObject("CloudpunkVR_CoffeeCupFrame").transform;
            cupT.SetParent(root.transform, false);
            var body = Part("Cup", CoffeeModel.BuildCup(), cupT);
            surface = CoffeeModel.BuildSurface();
            var liquid = Part("Liquid", surface.Mesh, cupT);
            liquidRenderer = liquid.GetComponent<MeshRenderer>();
            liquidVerts = new Il2CppStructArray<Vector3>(surface.VertexX.Length);
            drawnTops = new float[surface.Columns];
            for (int i = 0; i < drawnTops.Length; i++) drawnTops[i] = -1f;
            bitVerts = new Il2CppStructArray<Vector3>((Puffs + Drops) * 24);
            bitsT = Part("Bits", CoffeeModel.BuildBits(Puffs + Drops, b => b < Puffs
                ? CoffeeModel.Uv(b % 3 == 0 ? CoffeeModel.SteamDim : CoffeeModel.Steam, b % CoffeeModel.Variants)
                : CoffeeModel.Uv(b % 2 == 0 ? CoffeeModel.Coffee : CoffeeModel.Crema, b % CoffeeModel.Variants)), root.transform);
            bitsMesh = bitsT.GetComponent<MeshFilter>().sharedMesh;
            renderers = new[] { holderT.GetComponent<MeshRenderer>(), body.GetComponent<MeshRenderer>(), liquidRenderer, bitsT.GetComponent<MeshRenderer>() };
            root.SetActive(false);
            ReadVoices();
        }

        private static Transform Part(string name, Mesh mesh, Transform parent)
        {
            var go = new GameObject("CloudpunkVR_Coffee" + name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = true;
            mr.allowOcclusionWhenDynamic = false; // baked occlusion would hide it
            return go.transform;
        }

        private static void Draw()
        {
            holderT.SetPositionAndRotation(U(seatPos), UQ(seatRot));
            cupT.SetPositionAndRotation(U(cupPos), UQ(cupRot));
            if (cupScale != drawnScale)
            {
                drawnScale = cupScale;
                float k = MathF.Max(cupScale, 0.001f);
                cupT.localScale = new Vector3 { x = k, y = k, z = k };
            }
            DrawCoffee();
            DrawBits();
        }

        /// <summary>Column tops to the surface. Upload only if a top moved over 0.05 mm.</summary>
        private static void DrawCoffee()
        {
            bool any = CoffeeLiquid.Level > 0.0003f;
            if (liquidRenderer.enabled != any) liquidRenderer.enabled = any;
            if (!any) return;
            bool changed = false;
            var s = surface;
            for (int c = 0; c < s.Columns; c++)
            {
                float top = s.Top(c, CoffeeLiquid.SurfaceAt(s.X[c], s.Z[c]));
                float was = drawnTops[c];
                if (float.IsNaN(top) != float.IsNaN(was) || MathF.Abs(top - was) > 0.00005f) { drawnTops[c] = top; changed = true; }
            }
            if (!changed) return;
            for (int v = 0; v < s.VertexColumn.Length; v++)
            {
                int c = s.VertexColumn[v], col = c >= 0 ? c : -c - 1;
                float top = drawnTops[col];
                // empty column: fold to a point inside the wall
                liquidVerts[v] = float.IsNaN(top)
                    ? new Vector3 { x = s.X[col], y = s.Bottom[col] - 0.5f * CoffeeModel.Voxel, z = s.Z[col] }
                    : new Vector3 { x = s.VertexX[v], y = c >= 0 ? top : s.Bottom[col], z = s.VertexZ[v] };
            }
            s.Mesh.vertices = liquidVerts;
            s.Mesh.bounds = CoffeeBounds;
        }

        private static void DrawBits()
        {
            bool any = false, changed = false;
            for (int i = 0; i < Puffs + Drops; i++)
            {
                ref var b = ref (i < Puffs ? ref puffs[i] : ref drops[i - Puffs]);
                if (!b.Alive)
                {
                    if (b.Drawn) { WriteBox(i, N.Vector3.Zero, N.Vector3.Zero); b.Drawn = false; changed = true; }
                    continue;
                }
                any = changed = true;
                b.Drawn = true;
                var local = b.Pos; // already holder frame
                float s;
                N.Vector3 half;
                if (i < Puffs)
                {
                    float t = b.Age / b.Life;
                    s = b.Size * MathF.Min(1f, b.Age / 0.25f) * (1f - t * t);
                    half = new N.Vector3(s, s, s);
                }
                else if (b.Landed)
                {
                    // landed: flat splash that shrinks
                    float t = 1f - b.Age / b.Life;
                    s = b.Size * 1.5f * t;
                    half = new N.Vector3(s, 0.0006f * t, s);
                }
                else
                {
                    s = b.Size;
                    half = new N.Vector3(s * 0.8f, s, s * 0.8f);
                }
                WriteBox(i, local, half);
            }
            if (changed)
            {
                bitsMesh.vertices = bitVerts;
                bitsMesh.bounds = BitsBounds; // big, so it's never culled
            }
            if (any || changed) bitsT.SetPositionAndRotation(U(seatPos), UQ(seatRot));
        }

        private static readonly Bounds BitsBounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f));
        private static readonly Bounds CoffeeBounds = new Bounds(new Vector3(0f, CoffeeModel.Height * 0.5f, 0f), new Vector3(0.1f, CoffeeModel.Height + 0.02f, 0.1f));

        private static void WriteBox(int index, N.Vector3 centre, N.Vector3 half)
        {
            var corners = CharmModel.FaceCorners;
            for (int f = 0; f < 6; f++)
            {
                var face = corners[f];
                for (int k = 0; k < 4; k++)
                {
                    int bits = face[k];
                    bitVerts[index * 24 + f * 4 + k] = new Vector3
                    {
                        x = centre.X + ((bits & 1) != 0 ? half.X : -half.X),
                        y = centre.Y + ((bits & 2) != 0 ? half.Y : -half.Y),
                        z = centre.Z + ((bits & 4) != 0 ? half.Z : -half.Z),
                    };
                }
            }
        }

        private static void GripPoses(float dt)
        {
            foreach (var h in hands)
            {
                bool holds = holder == h && where == Where.Held;
                h.Blend = Mathf.MoveTowards(h.Blend, holds ? 1f : 0f, dt / (holds ? GrabBlend : ReleaseBlend));
                if (h.Blend <= 0f) { h.Pose.Weight = 0f; continue; }
                if (holds)
                {
                    // letting go keeps the last pose while the hand opens
                    var up = Rotate(cupRot, N.Vector3.UnitY);
                    var axis = cupPos + up * CoffeeModel.GripHeight;
                    CameraDriver.CockpitToWorld(U(axis), Quaternion.identity, out var point, out _);
                    h.Pose.Point = point;
                    h.Pose.Up = U(Rotate(toWorld, up));
                    h.Pose.Out = U(Rotate(toWorld, Rotate(cupRot, contact)));
                    h.Pose.Radius = GripRadius;
                }
                h.Pose.Weight = h.Blend;
            }
        }

        /// <summary>Grip pose for VoxelHands, weight 0 when not holding.</summary>
        public static HandGrip Grip(bool isLeft) => (isLeft ? left : right).Pose;

        private static void Show()
        {
            shown = true;
            lastShown = Time.unscaledTime;
            if (!root.activeSelf) root.SetActive(true);
        }

        private static void Hide()
        {
            shown = false;
            GrabArbiter.Clear(GrabArbiter.Coffee);
            if (holder != null || where != Where.Holder) { holder = null; where = Where.Holder; }
            cupScale = growT = 1f; // back in the holder, full size
            foreach (var h in hands) { h.Blend = 0f; h.Pose.Weight = 0f; }
            if (root != null && root.activeSelf) root.SetActive(false);
        }

        /// <summary>A layer the cockpit camera draws (same rule as the hands and the charm).</summary>
        private static void KeepLayer()
        {
            var cam = CameraDriver.CockpitCamera;
            int mask = cam.cullingMask, want = 28;
            if ((mask & (1 << want)) == 0)
                for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) { want = i; break; }
            if (want == layer) return;
            layer = want;
            for (int i = 0; i < renderers.Length; i++) renderers[i].gameObject.layer = layer;
        }

        // ---------------------------------------------------------------- colours and material

        private static Texture2D PaletteTexture(string name) => new Texture2D(CoffeeModel.PaletteSize, CoffeeModel.Variants, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.DontUnloadUnusedAsset,
        };

        private static void FillPalettes()
        {
            if (palette == null) return;
            for (int v = 0; v < CoffeeModel.Variants; v++)
                for (int i = 0; i < CoffeeModel.PaletteSize; i++)
                {
                    palette.SetPixel(i, v, CoffeeModel.Texel(i, v, false));
                    glow.SetPixel(i, v, CoffeeModel.Texel(i, v, true));
                }
            palette.Apply(false);
            glow.Apply(false);
        }

        private static void SetGlow(Material m)
        {
            bool texture = false;
            foreach (var prop in new[] { "_EmissionMap", "_EmissionTex", "_EmissiveMap", "_EmissiveTex" })
                if (m.HasProperty(prop)) { m.SetTexture(prop, glow); texture = true; }
            foreach (var prop in new[] { "_EmissionColor", "_EmissiveColor" })
                if (m.HasProperty(prop)) m.SetColor(prop, texture ? Color.white : new Color(0.15f, 0.15f, 0.15f, 1f));
            foreach (var prop in new[] { "_MetallicGlossMap", "_MetallicMap", "_MetallicTex", "_SpecGlossMap" })
                if (m.HasProperty(prop)) m.SetTexture(prop, Texture2D.blackTexture);
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        }

        /// <summary>Copies Rania's hand material with our palette, as for the hands and the charm.</summary>
        private static void FollowRania()
        {
            if (raniaMaterial || Time.unscaledTime < nextMaterialCheck) return;
            nextMaterialCheck = Time.unscaledTime + 3f;
            var player = Global.Instance != null ? Global.Instance.player : null;
            if (player == null) return;
            var all = player.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                var hand = all[i];
                if (hand == null || hand.name != "rania_hand" || hand.sharedMaterial == null) continue;
                var m = new Material(hand.sharedMaterial)
                {
                    name = "CloudpunkVR_Coffee",
                    mainTexture = palette,
                    mainTextureScale = Vector2.one,
                    mainTextureOffset = Vector2.zero,
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                };
                SetGlow(m);
                material = m;
                foreach (var r in renderers) r.sharedMaterial = m;
                raniaMaterial = true;
                Plugin.L.LogInfo($"Coffee: drawn with Rania's material ({m.shader.name}).");
                return;
            }
        }

        // ---------------------------------------------------------------- Rania's words (the game's own clips)

        private static AudioClip[] firstSipLines = new AudioClip[0], sipLines = new AudioClip[0], emptyLines = new AudioClip[0];
        private static float voiceVolume = 0.8f;

        /// <summary>Finds Rania's clips by name; played as UI one-shots at the game's drink-line volume.</summary>
        private static void ReadVoices()
        {
            var g = Global.Instance;
            if (g == null) return;
            try
            {
                var first = new List<AudioClip>();
                var later = new List<AudioClip>();
                var all = Resources.FindObjectsOfTypeAll<AudioClip>();
                for (int i = 0; i < all.Length; i++)
                {
                    var c = all[i];
                    if (c == null) continue;
                    string n = c.name;
                    if (n.StartsWith("Rania_ah thats better", StringComparison.OrdinalIgnoreCase)) first.Add(c);
                    else if (n.StartsWith("Rania_mhm", StringComparison.OrdinalIgnoreCase)) later.Add(c);
                }
                firstSipLines = first.ToArray();
                sipLines = later.ToArray();
                var drink = g.soundDrinkCooldown;
                var range = drink.volumeRangePlayer;
                float volume = 0.5f * (range.x + range.y);
                if (volume > 0.05f && volume <= 1.5f) voiceVolume = volume;
                emptyLines = Clips(drink.clips);
                var fail = Clips(g.soundFailReactions.clips);
                Plugin.L.LogInfo($"Coffee: Rania's lines: first sip {Names(firstSipLines)}, later sips {Names(sipLines)}, empty cup {Names(emptyLines)} (the game's drink cooldown), " +
                                 $"big spill the game's fail reaction {Names(fail)}; volume {voiceVolume:0.00}.");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("Coffee: Rania's lines not found (" + e.Message + "); the coffee stays quiet.");
            }
        }

        private static AudioClip[] Clips(Il2CppReferenceArray<AudioClip> clips)
        {
            var list = new List<AudioClip>();
            for (int i = 0; clips != null && i < clips.Length; i++) if (clips[i] != null) list.Add(clips[i]);
            return list.ToArray();
        }

        private static string Names(AudioClip[] clips)
        {
            if (clips.Length == 0) return "none";
            var parts = new List<string>();
            foreach (var c in clips) parts.Add($"{c.name} {c.length:0.0} s");
            return "[" + string.Join(", ", parts) + "]";
        }

        private static string Say(AudioClip[] clips, float now)
        {
            var g = Global.Instance;
            if (clips.Length == 0 || g == null) return "";
            var clip = clips[rng.Next(clips.Length)];
            if (clip == null) return "";
            g.PlayUIOneShot(clip, voiceVolume);
            lastLine = now;
            return clip.name;
        }

        private static string FailReaction(float now)
        {
            var g = Global.Instance;
            if (g == null) return "";
            g.PlayFailReaction();
            lastLine = now;
            return "the game's fail reaction";
        }

        private static void Log(string line)
        {
            if (logs++ < 200) Plugin.L.LogInfo(line);
        }

        // ---------------------------------------------------------------- developer command "coffee"

        // scripted hand for the dev command, goes through the real grip, hold and sip paths
        private sealed class SimHand
        {
            public bool On, Left, Down, Press;
            public N.Vector3 Grip;
            public N.Quaternion Rot = N.Quaternion.Identity;
        }
        private static readonly SimHand sim = new SimHand();

        private enum Leg { Reach, Press, Face, Mouth, Drink, Away, Side, Tip, Upright, Over, Down, Let, Throw }
        private static readonly List<Leg> legs = new List<Leg>();
        private static bool legOn;
        private static Leg leg;
        private static float legT, legTime, drinkTip, throwSpeed;
        private static N.Vector3 throwWay = N.Vector3.UnitZ; // holder frame
        private static N.Vector3 legFrom;
        private static N.Quaternion legFromRot = N.Quaternion.Identity;
        private static int sipsAtLeg;

        public static bool Simulated => sim.On;

        private static bool TakeSimPress()
        {
            bool p = sim.Press;
            sim.Press = false;
            return p;
        }

        /// <summary>Scripted hand's grip and SteamVR hand rotation (fingers forward, index up), tracking space.</summary>
        public static bool SimulatedHand(bool isLeft, out Vector3 grip, out Quaternion rotation)
        {
            grip = Vector3.zero;
            rotation = Quaternion.identity;
            if (!sim.On || sim.Left != isLeft || !shown) return false;
            CameraDriver.CockpitToTracking(U(sim.Grip), UQ(sim.Rot), out grip, out rotation);
            return true;
        }

        /// <summary>Advances the scripted hand one frame.</summary>
        private static void Simulate(float dt)
        {
            if (!sim.On) return;
            if (!legOn && legs.Count > 0)
            {
                leg = legs[0];
                legs.RemoveAt(0);
                legOn = true;
                legT = 0f;
                legFrom = cupPos;
                legFromRot = cupRot;
                sipsAtLeg = sips;
                legTime = leg switch
                {
                    Leg.Reach => 0.4f, Leg.Press => 0.3f, Leg.Face => 0.9f, Leg.Mouth => 0.6f, Leg.Drink => 2.2f, Leg.Away => 0.6f,
                    Leg.Side => 0.8f, Leg.Tip => 1.9f, Leg.Upright => 0.5f, Leg.Over => 1.0f, Leg.Down => 0.35f, Leg.Throw => 0.15f, _ => 0.2f,
                };
                if (leg == Leg.Press) { sim.Press = sim.Down = true; }
                if (leg == Leg.Let) sim.Down = false;
            }
            var targetPos = cupPos;
            var targetRot = cupRot;
            if (legOn)
            {
                legT = MathF.Min(legTime, legT + dt);
                float t = legT / legTime, e = t * t * (3f - 2f * t);
                var upright = Look(seatFwd, seatUp);
                var towardFace = -Flat(Rotate(headRot, N.Vector3.UnitZ), seatFwd);
                switch (leg)
                {
                    case Leg.Face: Toward(FacePose(), upright, e, out targetPos, out targetRot); break;
                    case Leg.Away: Toward(FacePose(), upright, e, out targetPos, out targetRot); break;
                    case Leg.Mouth: { Lip(25f, towardFace, out var p, out var r); Toward(p, r, e, out targetPos, out targetRot); break; }
                    case Leg.Drink:
                    {
                        // tip further with the lip at the mouth until a sip, hold a moment
                        if (sips == sipsAtLeg) drinkTip = 25f + 85f * MathF.Min(1f, legT / 1.8f);
                        else if (legT < legTime - 0.35f) legT = legTime - 0.35f;
                        Lip(drinkTip, towardFace, out targetPos, out targetRot);
                        break;
                    }
                    case Leg.Side: Toward(SidePose(), upright, e, out targetPos, out targetRot); break;
                    case Leg.Tip:
                    {
                        float tip = 85f * MathF.Min(1f, legT / 0.6f);
                        var centre = SidePose() + seatUp * 0.04f;
                        targetRot = N.Quaternion.Normalize(N.Quaternion.CreateFromAxisAngle(N.Vector3.Normalize(N.Vector3.Cross(seatUp, seatRight)), tip * Mathf.Deg2Rad) * upright);
                        targetPos = centre - Rotate(targetRot, new N.Vector3(0f, 0.04f, 0f));
                        break;
                    }
                    case Leg.Upright: Toward(SidePose(), upright, e, out targetPos, out targetRot); break;
                    case Leg.Over: Toward(seatPos + seatUp * 0.05f, seatRot, e, out targetPos, out targetRot); break;
                    case Leg.Down: Toward(seatPos + seatUp * 0.006f, seatRot, e, out targetPos, out targetRot); break;
                    case Leg.Throw:
                    {
                        // accelerate to throw speed, release at the end
                        targetPos = legFrom + Rotate(seatRot, throwWay) * (0.5f * throwSpeed * legT * legT / legTime);
                        targetRot = legFromRot;
                        if (legT >= legTime) sim.Down = false;
                        break;
                    }
                }
                if (legT >= legTime) legOn = false;
            }
            if (holder != null && holder.Left == sim.Left)
            {
                // controller pose that puts the held cup at the target
                var ctrlRot = N.Quaternion.Normalize(targetRot * N.Quaternion.Conjugate(holdRot));
                sim.Rot = ctrlRot;
                sim.Grip = targetPos - Rotate(ctrlRot, holdPos);
            }
            else if (legOn && (leg == Leg.Reach || leg == Leg.Press))
            {
                // reaching: closed hand on the axis, back of the hand toward the shoulder
                var up = Rotate(cupRot, N.Vector3.UnitY);
                var shoulder = eyeRest + seatRight * (sim.Left ? -0.17f : 0.17f) - seatUp * 0.2f - seatFwd * 0.1f;
                var back = Flat(shoulder - cupPos, -seatFwd);
                back -= up * N.Vector3.Dot(back, up);
                if (back.LengthSquared() < 1e-6f) back = seatUp - up * N.Vector3.Dot(seatUp, up); // cup lying toward the shoulder
                back = N.Vector3.Normalize(back);
                var fwd = sim.Left ? N.Vector3.Cross(up, back) : N.Vector3.Cross(back, up);
                sim.Rot = Look(fwd, up);
                sim.Grip = cupPos + up * CoffeeModel.GripHeight;
            }
        }

        private static void Toward(N.Vector3 pos, N.Quaternion rot, float e, out N.Vector3 p, out N.Quaternion r)
        {
            p = N.Vector3.Lerp(legFrom, pos, e);
            r = N.Quaternion.Slerp(legFromRot, rot, e);
        }

        private static N.Vector3 FacePose()
        {
            var ahead = Flat(Rotate(headRot, N.Vector3.UnitZ), seatFwd);
            var side = N.Vector3.Cross(seatUp, ahead);
            return headPos + ahead * 0.22f + side * 0.04f - seatUp * 0.26f;
        }

        private static N.Vector3 SidePose()
        {
            var ahead = Flat(Rotate(headRot, N.Vector3.UnitZ), seatFwd);
            return headPos + ahead * 0.30f + seatRight * 0.14f - seatUp * 0.34f;
        }

        /// <summary>Cup tipped toward the face, near lip just in front of the mouth.</summary>
        private static void Lip(float degrees, N.Vector3 towardFace, out N.Vector3 pos, out N.Quaternion rot)
        {
            var upright = Look(seatFwd, seatUp);
            var axis = N.Vector3.Normalize(N.Vector3.Cross(seatUp, towardFace));
            rot = N.Quaternion.Normalize(N.Quaternion.CreateFromAxisAngle(axis, degrees * Mathf.Deg2Rad) * upright);
            var lipLocal = Rotate(N.Quaternion.Conjugate(upright), towardFace) * (CoffeeModel.InnerRadius(CoffeeModel.Height) + 0.5f * CoffeeModel.Voxel);
            lipLocal.Y = CoffeeModel.Height;
            pos = mouth - towardFace * 0.01f - Rotate(rot, lipLocal);
        }

        private static N.Vector3 Flat(N.Vector3 d, N.Vector3 fallback)
        {
            d -= seatUp * N.Vector3.Dot(d, seatUp);
            return d.LengthSquared() > 1e-6f ? N.Vector3.Normalize(d) : fallback;
        }

        private static void Script(bool needCup, params Leg[] then)
        {
            bool holding = holder != null && sim.On;
            if (!sim.On) { sim.On = true; sim.Left = false; sim.Down = false; legs.Clear(); legOn = false; }
            if (needCup && !holding && !legs.Contains(Leg.Press)) { legs.Add(Leg.Reach); legs.Add(Leg.Press); }
            legs.AddRange(then);
        }

#if DEV
        /// <summary>
        /// coffee [info] | grab [left|right] | sip [n] | pour | drop | hold | release | stop
        /// | throw [dir] [m/s] | toss vx vy vz | fling left|right|ahead|lever|wheel [m/s] | fling x y z vx vy vz | out
        /// | slosh g [ahead|side|up] [s] | spill | level cm [typing] | refill | hint | cabin [x y z] | place | at x y z | tried
        /// </summary>
        public static string Dev(string[] a)
        {
            string what = a.Length > 1 ? a[1].ToLowerInvariant() : "info";
            float Arg(int i, float fallback) => a.Length > i ? float.Parse(a[i], CultureInfo.InvariantCulture) : fallback;
            switch (what)
            {
                case "grab":
                    if (holder != null) break;
                    sim.On = true;
                    sim.Left = a.Length > 2 && a[2] == "left";
                    sim.Down = false;
                    legs.Clear();
                    legOn = false;
                    legs.Add(Leg.Reach);
                    legs.Add(Leg.Press);
                    break;
                case "sip":
                {
                    Script(true);
                    int n = Math.Max(1, (int)Arg(2, 1f));
                    for (int i = 0; i < n; i++) { legs.Add(Leg.Face); legs.Add(Leg.Mouth); legs.Add(Leg.Drink); }
                    legs.Add(Leg.Away);
                    break;
                }
                case "hold": Script(true, Leg.Face); break;
                case "pour": Script(true, Leg.Side, Leg.Tip, Leg.Upright); break;
                case "drop": Script(true, Leg.Over, Leg.Down, Leg.Let); break;
                case "release": if (sim.On) { legs.Clear(); legOn = false; legs.Add(Leg.Let); } break;
                case "stop":
                    sim.On = false;
                    legs.Clear();
                    legOn = false;
                    if (holder != null) Release();
                    break;
                case "slosh":
                {
                    string axis = a.Length > 3 ? a[3].ToLowerInvariant() : "ahead";
                    var dir = axis == "side" ? seatRight : axis == "up" ? seatUp : -seatFwd; // ahead = braking
                    devPush = dir * (Arg(2, 0.3f) * Gravity);
                    devPushUntil = Time.unscaledTime + Math.Clamp(Arg(4, 0.15f), 0.05f, 10f);
                    break;
                }
                case "spill":
                {
                    var back = Rotate(N.Quaternion.Conjugate(cupRot), -seatFwd);
                    var lean = new N.Vector2(back.X, back.Z);
                    if (lean.LengthSquared() > 1e-6f) CoffeeLiquid.Tilt = N.Vector2.Normalize(lean) * 0.95f;
                    break;
                }
                case "refill": refillWhy = "asked for"; break;
                case "hint": emptyHinted = false; break; // show the empty-cup note again
                case "level":
                {
                    float level = Math.Clamp(Arg(2, 0f) / 100f, 0f, CoffeeModel.Inside);
                    if (a.Length > 3 && a[3].ToLowerInvariant() == "typing") levelWhenTyping = level;
                    else CoffeeLiquid.Level = level;
                    break;
                }
                case "throw":
                {
                    if (!CabinShape.Ready) return "no cabin shape to throw the cup into; " + Describe();
                    string way = a.Length > 2 ? a[2].ToLowerInvariant() : "right";
                    var w = way switch
                    {
                        "left" => new N.Vector3(-1f, 0.35f, 0f), "ahead" => new N.Vector3(0f, 0.35f, 1f), "back" => new N.Vector3(0f, 0.35f, -1f),
                        "up" => N.Vector3.UnitY, "down" => -N.Vector3.UnitY, _ => new N.Vector3(1f, 0.35f, 0f),
                    };
                    throwWay = N.Vector3.Normalize(w);
                    throwSpeed = Math.Clamp(Arg(3, 1.5f), 0.1f, MaxThrow);
                    Script(true, Leg.Face, Leg.Throw);
                    break;
                }
                case "toss":
                {
                    if (!CabinShape.Ready) return "no cabin shape to throw the cup into; " + Describe();
                    var v = new N.Vector3(Arg(2, 0f), Arg(3, 1f), Arg(4, 1f));
                    bool byLeft = holder != null ? holder.Left : false;
                    if (where == Where.Fading) break;
                    if (holder != null)
                    {
                        holder = null;
                        sim.On = false;
                        legs.Clear();
                        legOn = false;
                    }
                    else if (where != Where.Flying && where != Where.Lying)
                    {
                        cupPos = seatPos + seatUp * 0.03f; // lift out of the holder first
                        cupRot = seatRot;
                    }
                    Throw(byLeft, v, N.Vector3.Zero, "tossed (test)");
                    break;
                }
                case "fling":
                {
                    if (!CabinShape.Ready) return "no cabin shape to throw the cup into; " + Describe();
                    if (where == Where.Fading) break;
                    string aim = Aim(a, out var from, out var vel);
                    if (aim.StartsWith("no ", StringComparison.Ordinal)) return aim + "; " + Describe();
                    if (holder != null)
                    {
                        holder = null;
                        sim.On = false;
                        legs.Clear();
                        legOn = false;
                    }
                    cupRot = seatRot;
                    cupPos = seatPos + Rotate(seatRot, from - CoffeeMount.SeatFromEyes - new N.Vector3(0f, CoffeeBody.Middle, 0f));
                    Throw(false, vel, N.Vector3.Zero, $"flung at {aim} from {from.X * 100f:0} cm right, {from.Y * 100f:0} up, {from.Z * 100f:0} ahead of the eyes (test)", false);
                    break;
                }
                case "out":
                    if (where != Where.Flying && where != Where.Lying) return "the cup is not flying or lying in the cabin; " + Describe();
                    CoffeeBody.Pos = new N.Vector3(0f, -3f, 0f); // far below: next step finds it out of the cabin
                    where = Where.Flying;
                    break;
                case "cabin":
                {
                    var eyes = CoffeeMount.SeatFromEyes;
                    var at = new N.Vector3(Arg(2, eyes.X), Arg(3, eyes.Y), Arg(4, eyes.Z));
                    return $"cabin: {CabinShape.Describe()}; solid spans under the holder (height from the eyes): {CabinShape.At(eyes.X, eyes.Z)}; " +
                           $"at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}): {CabinShape.At(at.X, at.Z)}, " +
                           $"{(CabinShape.Solid(CabinShape.Column(at.X, at.Z), at.Y) ? "solid" : "open")} there, {(at - CoffeeMount.Shoulder).Length() * 100f:0} cm from the right shoulder";
                }
                case "place":
                    CoffeeMount.Manual = null;
                    CoffeeMount.Forget();
                    return "searching again (\"coffee tried\" lists the spots once placed)";
                case "at":
                    CoffeeMount.Manual = new Vector3(Arg(2, 0.43f), Arg(3, -0.5f), Arg(4, 0.22f));
                    CoffeeMount.Forget();
                    break;
                case "tried":
                    return CoffeeMount.Tried;
            }
            return Describe();
        }
#endif

        /// <summary>"coffee fling": start point (eye frame) and velocity for a target. Returns the target, or why not.</summary>
        private static string Aim(string[] a, out N.Vector3 from, out N.Vector3 vel)
        {
            from = vel = N.Vector3.Zero;
            string at = a.Length > 2 ? a[2].ToLowerInvariant() : "ahead";
            float Speed(float fallback) => a.Length > 3 && float.TryParse(a[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0.1f, MaxThrow) : fallback;
            var eyes = CoffeeMount.SeatFromEyes;
            var vi = Global.Instance != null ? Global.Instance.vehicleInterior : null;
            switch (at)
            {
                case "left":
                case "right":
                case "ahead":
                {
                    string name = at == "ahead" ? "windscreen" : at + " window";
                    int i = CabinShape.FindPane(name);
                    if (i < 0) return "no " + name + " found";
                    var pane = CabinShape.GetPane(i);
                    var target = pane.Centre;
                    if (at != "ahead")
                    {
                        float half = MathF.Abs(pane.U.Y) * pane.HalfU + MathF.Abs(pane.V.Y) * pane.HalfV;
                        target.Y = Math.Clamp(CoffeeMount.Shoulder.Y, pane.Centre.Y - half + 0.08f, pane.Centre.Y + half - 0.08f);
                    }
                    var inward = new N.Vector3(-pane.Normal.X, 0f, -pane.Normal.Z);
                    if (inward.LengthSquared() < 1e-4f) return "no way across to " + CabinShape.PaneName(i);
                    inward = N.Vector3.Normalize(inward);
                    // find a start with room and a clear path, sliding along the pane if something's in the way
                    float[] along = { 0f, 0.1f, -0.1f, 0.2f, -0.2f, 0.3f, -0.3f, 0.4f, -0.4f };
                    string moved = null;
                    foreach (float m in along)
                    {
                        var t = target + pane.U * m;
                        if (!Room(t, inward, out var f) || !Clear(f, t)) continue;
                        target = t;
                        from = f;
                        moved = m == 0f ? "" : $", {m * 100f:0} cm along it for a clear way";
                        break;
                    }
                    if (moved == null) return "no clear way to " + CabinShape.PaneName(i);
                    vel = Toward(from, target, Speed(at == "ahead" ? 5f : 3.5f));
                    return CabinShape.PaneName(i) + moved;
                }
                case "lever":
                {
                    N.Vector3 handle;
                    if (vi != null && vi.ascensionLever != null && LeverModel.Ready)
                    {
                        LeverModel.Shape(vi.ascensionLever, out _, out var h, out _);
                        handle = ToSeat(V(h)) + eyes;
                    }
                    else if (CoffeeMount.LeverBoxed) handle = CoffeeMount.LeverTop;
                    else return "no lever measured";
                    from = handle + new N.Vector3(0f, 0.15f, 0f);
                    vel = new N.Vector3(0f, -Speed(0.5f), 0f);
                    return "the lever's handle";
                }
                case "wheel":
                {
                    if (vi == null || vi.steeringWheel == null || !WheelModel.Ready) return "no wheel measured";
                    WheelModel.Shape(vi.steeringWheel, out var hub, out var axis, out var twelve, out _);
                    var top = ToSeat(V(hub)) + eyes + SeatDir(V(twelve)) * WheelModel.RimRadius;
                    var toDriver = SeatDir(V(axis));
                    if (!Room(top, toDriver, out from)) return "no room in front of the wheel";
                    vel = Toward(from, top, Speed(3f));
                    return "the wheel's rim";
                }
                default:
                    if (a.Length < 8) return "no target: fling left|right|ahead|lever|wheel [m/s], or fling x y z vx vy vz";
                    from = new N.Vector3(float.Parse(a[2], CultureInfo.InvariantCulture), float.Parse(a[3], CultureInfo.InvariantCulture), float.Parse(a[4], CultureInfo.InvariantCulture));
                    vel = new N.Vector3(float.Parse(a[5], CultureInfo.InvariantCulture), float.Parse(a[6], CultureInfo.InvariantCulture), float.Parse(a[7], CultureInfo.InvariantCulture));
                    return "a point";
            }
        }

        /// <summary>A start point back from the target where the whole cup is in open air.</summary>
        private static bool Room(N.Vector3 target, N.Vector3 back, out N.Vector3 from)
        {
            float[] away = { 0.35f, 0.3f, 0.4f, 0.25f, 0.45f, 0.2f }, rise = { 0f, 0.05f, 0.1f, -0.05f };
            foreach (float d in away)
                foreach (float y in rise)
                {
                    from = target + back * d + new N.Vector3(0f, y, 0f);
                    bool open = true;
                    for (int k = 0; k < 7 && open; k++)
                    {
                        var p = from + new N.Vector3(k == 1 ? 0.04f : k == 2 ? -0.04f : 0f, k == 5 ? -CoffeeBody.Middle : k == 6 ? CoffeeModel.Height - CoffeeBody.Middle : 0f,
                                                     k == 3 ? 0.04f : k == 4 ? -0.04f : 0f);
                        open = !CabinShape.Solid(CabinShape.Column(p.X, p.Z), p.Y);
                    }
                    if (open) return true;
                }
            from = default;
            return false;
        }

        /// <summary>Clear straight path for the whole cup, ignoring the last 6 cm.</summary>
        private static bool Clear(N.Vector3 from, N.Vector3 to)
        {
            var d = to - from;
            int steps = Math.Max(1, (int)(d.Length() / 0.02f));
            for (int i = 1; i <= steps; i++)
            {
                var m = from + d * ((float)i / steps);
                if ((to - m).Length() < 0.06f) break;
                for (int k = 0; k < 7; k++)
                {
                    var q = m + new N.Vector3(k == 1 ? 0.04f : k == 2 ? -0.04f : 0f, k == 5 ? -CoffeeBody.Middle : k == 6 ? CoffeeModel.Height - CoffeeBody.Middle : 0f,
                                              k == 3 ? 0.04f : k == 4 ? -0.04f : 0f);
                    if (CabinShape.Solid(CabinShape.Column(q.X, q.Z), q.Y)) return false;
                }
            }
            return true;
        }

        /// <summary>Launch velocity to reach a point at about this speed, gravity included.</summary>
        private static N.Vector3 Toward(N.Vector3 from, N.Vector3 to, float speed)
        {
            var d = to - from;
            float t = MathF.Max(d.Length() / speed, 0.02f);
            var v = d / t;
            v.Y += 0.5f * Gravity * t;
            return v;
        }

        private static string Describe()
        {
            if (!shown) return $"not shown (setting {VRConfig.CockpitCoffee.Value}, view {CameraDriver.Mode}{(failed ? ", off after errors" : "")}); {CoffeeMount.Describe()}";
            float deg = Mathf.Rad2Deg;
            string place = where switch
            {
                Where.Held => $"held in the {(holder.Left ? "left" : "right")} hand, {(cupPos - mouth).Length() * 100f:0} cm from the mouth, tilted {MathF.Acos(Math.Clamp(N.Vector3.Dot(Rotate(cupRot, N.Vector3.UnitY), seatUp), -1f, 1f)) * deg:0} deg",
                Where.Dropping => "dropping into the holder",
                Where.Returning => $"floating back to the holder ({moveT * 100f:0}%)",
                Where.Flying => $"flying for {Time.unscaledTime - flyingSince:0.0} s at {CoffeeBody.Vel.Length():0.0} m/s, {bounces} bounce(s), shaken loose {wakes} time(s), {Pose()}, {Place(CoffeeBody.Pos)} ({FromEyes()})",
                Where.Lying => $"lying {Pose()}, {Place(CoffeeBody.Pos)} ({FromEyes()}), {ShoulderGap() * 100f:0} cm from the right shoulder{(farFrom >= 0f ? " (out of reach)" : "")}, shaken loose {wakes} time(s)",
                Where.Fading => cupScale > 0f ? "fading away" : "gone, about to come back in the holder",
                _ => growT < 1f ? "growing back in the holder" : "in the holder",
            };
            string Hand(HandState h) => !h.Valid ? "hidden" : float.IsPositiveInfinity(h.Gap) ? $"{(h.CtrlPos - cupPos).Length() * 100f:0} cm away" : $"{h.Gap * 100f:0.0} cm from the axis";
            int puffsAlive = 0, dropsAlive = 0;
            for (int i = 0; i < Puffs; i++) if (puffs[i].Alive) puffsAlive++;
            for (int i = 0; i < Drops; i++) if (drops[i].Alive) dropsAlive++;
            string steam = (puffsAlive > 0 ? $"{puffsAlive} puffs, the highest {-highestPuff * 100f:0} cm under its ceiling ({SteamCeiling * 100f:0} cm under the resting eyes)" : "no puffs")
                           + $", the widest {widestPuff * 100f:0.0} cm from the cup's axis since the last look";
            string rim = float.IsNegativeInfinity(peakOver) ? "not in the holder since the last look"
                : peakOver > 0f ? $"{peakOver * 1000f:0} mm over the rim at most" : $"{-peakOver * 1000f:0} mm under the rim at the closest";
            string line = $"{place}; level {CoffeeLiquid.Level * 100f:0.0} of {CoffeeModel.Inside * 100f:0.0} cm, heat {Heat:0.00}, steam {SteamStrength():0.00} ({steam}); " +
                          $"slosh {CoffeeLiquid.Tilt.Length() * deg:0} deg now, peak in the holder {peakTilt * deg:0} deg ({rim}), period {CoffeeLiquid.Period:0.00} s; " +
                          $"HOVA push now {N.Vector3.Dot(Rotate(toSet, carAccel), seatFwd):0.0} ahead {N.Vector3.Dot(Rotate(toSet, carAccel), seatRight):0.0} right {N.Vector3.Dot(Rotate(toSet, carAccel), seatUp):0.0} up, " +
                          $"peaks {peakFwd:0.0} ahead {peakSide:0.0} right {peakUp:0.0} up m/s2, felt in the cup across at most {peakFelt:0.0}; sips {sips}, spilled {spilled * 1000f:0.0} mm in {spills} spill(s), {dropsAlive} drops about; " +
                          $"{CoffeeMount.Describe()}; hands: left {Hand(left)}, right {Hand(right)}; reach: left {GrabArbiter.Describe(true)}, right {GrabArbiter.Describe(false)}; " +
                          $"strafe kept from the game: left {left.Swallow}, right {right.Swallow}; test hand {(sim.On ? (sim.Left ? "left" : "right") + (legOn ? $" ({leg})" : "") : "off")}; " +
                          $"thrown {throws} time(s); cabin: {CabinShape.Describe(false)}; pushes past {CoffeeLiquid.Jolt:0} m/s2: {knocks} with a collision (felt at once), {kicks} without (felt averaged); " +
                          $"layer {layer}, {(raniaMaterial ? "Rania's material" : "standard material")}; " +
                          $"cost {(costFrames > 0 ? costSum / costFrames : 0f):0.000} ms per frame, worst {costWorst:0.00} ms over {costFrames} frames";
            peakTilt = peakFwd = peakSide = peakUp = peakFelt = widestPuff = 0f;
            peakOver = float.NegativeInfinity;
            costSum = costWorst = 0f;
            costFrames = 0;
            return line;
        }

        // ---------------------------------------------------------------- math (System.Numerics, Unity's goes through the interop)

        private static N.Vector3 V(Vector3 v) => new N.Vector3(v.x, v.y, v.z);
        private static Vector3 U(N.Vector3 v) => new Vector3 { x = v.X, y = v.Y, z = v.Z };
        private static N.Quaternion Q(Quaternion q) => new N.Quaternion(q.x, q.y, q.z, q.w);
        private static Quaternion UQ(N.Quaternion q) => new Quaternion { x = q.X, y = q.Y, z = q.Z, w = q.W };
        private static N.Vector3 Rotate(N.Quaternion q, N.Vector3 v) => N.Vector3.Transform(v, q);

        /// <summary>Unity's LookRotation: +z along forward, +y toward up.</summary>
        private static N.Quaternion Look(N.Vector3 forward, N.Vector3 up)
        {
            var z = N.Vector3.Normalize(forward);
            var x = N.Vector3.Cross(up, z);
            if (x.LengthSquared() < 1e-8f) x = N.Vector3.Cross(N.Vector3.UnitY, z);
            x = N.Vector3.Normalize(x);
            var y = N.Vector3.Cross(z, x);
            float trace = x.X + y.Y + z.Z;
            N.Quaternion q;
            if (trace > 0f)
            {
                float s = MathF.Sqrt(trace + 1f) * 2f;
                q = new N.Quaternion((y.Z - z.Y) / s, (z.X - x.Z) / s, (x.Y - y.X) / s, 0.25f * s);
            }
            else if (x.X > y.Y && x.X > z.Z)
            {
                float s = MathF.Sqrt(1f + x.X - y.Y - z.Z) * 2f;
                q = new N.Quaternion(0.25f * s, (y.X + x.Y) / s, (z.X + x.Z) / s, (y.Z - z.Y) / s);
            }
            else if (y.Y > z.Z)
            {
                float s = MathF.Sqrt(1f + y.Y - x.X - z.Z) * 2f;
                q = new N.Quaternion((y.X + x.Y) / s, 0.25f * s, (z.Y + y.Z) / s, (z.X - x.Z) / s);
            }
            else
            {
                float s = MathF.Sqrt(1f + z.Z - x.X - y.Y) * 2f;
                q = new N.Quaternion((z.X + x.Z) / s, (z.Y + y.Z) / s, 0.25f * s, (x.Y - y.X) / s);
            }
            return N.Quaternion.Normalize(q);
        }
    }
}
