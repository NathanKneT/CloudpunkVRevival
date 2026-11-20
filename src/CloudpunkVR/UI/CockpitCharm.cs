using System;
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
    /// Camus's voxel head on a chain from the cockpit ceiling, for the hands to play with.
    /// Own physics at 360 Hz in world orientation, so the HOVA's acceleration and turns swing it. Strafe button grabs it.
    /// </summary>
    internal static class CockpitCharm
    {
        // ---------------------------------------------------------------- physics, in SI units

        private const float Step = 1f / 360f;          // 4 steps a frame at 90 Hz, 3 at 120 Hz
        private const int MaxSteps = 24;
        private const float Gravity = 9.81f;
        private const float RingMass = 0.006f, HeadMass = 0.02f;
        private const float WRing = 1f / RingMass, WHead = 1f / HeadMass;
        private const float Gyration = 0.0185f;        // head's radius of gyration about the chain
        private const float WTwist = 1f / (HeadMass * Gyration * Gyration);
        private const float ChainStretch = 0.006f;     // stretch under the charm's weight
        private const float ChainCompliance = ChainStretch / ((RingMass + HeadMass) * Gravity);
        private const float ChainGive = 0.1f;          // max stretch, share of length
        private const float ChainDamping = 7f;         // 1/s along the chain
        private const float AirDrag = 0.45f;           // 1/s: a swing halves in ~2 s
        private const float JointDamping = 4f;         // 1/s, head about the ring
        private const float TwistPeriod = 1.6f, TwistDampingRatio = 0.12f;
        private const float MaxSpin = 30f;             // rad/s
        private const float MaxSpeed = 3f;             // m/s, a hard flick
        private const float HoldSlack = 0.03f;         // pulled past the chain or a wall by this much, it slips out
        private const float Friction = 0.6f;
        private const float MaxCarPush = 7f;           // m/s/s, so a crash knocks it but doesn't fling it
        private const float CarSmoothing = 0.04f;      // s: car velocity only changes on physics steps
        private const float RingRadius = 0.004f, ChainRadius = 0.003f;
        private const float RingBelowMount = 0.002f, HeadBelowMount = 0.03f; // ceiling limits
        private const float HeadDepth = 0.03f, RingDepth = 0.005f; // centre to front surface
        private const float GrabGap = 0.03f;           // grab distance to the head
        private const float GrabWindow = 0.25f;        // s after the press to reach it
        private const float GripNear = 0.12f;          // a press this close is the charm's, not a strafe
        private const float Reach = 0.12f;             // pieces farther than this are skipped
        private const float ControllerRadius = 0.035f; // controller as a ball when hands aren't drawn

        // state relative to the mount, in world orientation (the cabin turns under it)
        private static N.Vector3 ring, head, ringVel, headVel;
        private static float twist, spin;              // about the chain, from facing the driver; unwrapped
        private static N.Vector3 ringLast, headLast;   // previous step, for interpolation
        private static float twistLast, accumulator;
        private static N.Vector3 push;                 // car acceleration this frame

        // this frame: world to set rotation, mount in the set, HOVA up and facing in the world
        private static N.Quaternion toSet = N.Quaternion.Identity, toWorld = N.Quaternion.Identity;
        private static N.Vector3 mountSet, upW, faceW, aheadW, lastMountSet, lastUpW, lastFaceW;
        private static N.Vector3 carVel;
        private static bool haveFrame, haveCar;

        // head frame: hang axis (centre of mass to ring), box, rest pose
        private static N.Vector3 hangAxis, boxCenter, boxHalf;
        private static N.Quaternion bodyFrameInverse;
        private static float linkLength;

        private sealed class HandState
        {
            public bool Left;
            public Transform Root;
            public Transform[] Pieces = new Transform[0];
            public N.Vector3[] LocalCenter, LocalHalf, Pos, PrevPos, Half, Vel, AngVel;
            public N.Quaternion[] Rot, PrevRot;
            public bool Active, HadPrev, ControllerValid, Touching;
            public bool JustLetGo; // no push until the fingers have left it
            public bool Swallow;   // this strafe press is the charm's until released
            public float Gap = float.MaxValue, Approach, LastTouch = -10f, LastTick = -10f, PressedAt = -10f;
            public N.Vector3 CtrlPos;
            public N.Quaternion CtrlRot = N.Quaternion.Identity;
        }

        private static readonly HandState left = new HandState { Left = true }, right = new HandState();
        private static readonly HandState[] hands = { left, right };
        private static readonly N.Vector3[] spheres = new N.Vector3[64];
        private static HandState holder;
        private static N.Vector3 holdPos, heldVel;
        private static N.Quaternion holdRot = N.Quaternion.Identity;
        private static float heldSpin;

        // Drawing.
        private static GameObject root;
        private static Transform headT, chainT, mountT;
        private static MeshRenderer[] renderers;
        private static Mesh chainMesh;
        private static Il2CppStructArray<Vector3> chainVerts, chainNormals;
        private static int beads;
        private static float builtFor = -1f;
        private static Material material;
        private static Texture2D palette, glow;
        private static int layer = -1;
        private static float nextMaterialCheck, nextEyeCheck, nextNanWarning, lastShown = -10f, cockpitSince = -1f;
        private static int seenSpace = -1;
        private static bool shown, raniaMaterial, failed;
        private static int errors;

        private const float BeadSize = 0.0024f, BeadPitch = 0.0034f, CoreSize = 0.0009f;

        // ---------------------------------------------------------------- every frame

        /// <summary>Right before rendering, after the hands are placed.</summary>
        public static void Update()
        {
            if (failed) return;
            try { Frame(); }
            catch (Exception e)
            {
                failed = ++errors >= 5;
                if (failed) Hide();
                Plugin.L.LogError("Cockpit charm" + (failed ? " (off for this session)" : "") + ": " + e);
            }
        }

        private static void Frame()
        {
            bool on = VRConfig.CockpitCharm.Value && CameraDriver.Mode == ViewMode.Cockpit && CameraDriver.CockpitCamera != null;
            if (!on) { cockpitSince = -1f; Hide(); return; }
            if (cockpitSince < 0f) cockpitSince = Time.unscaledTime;
            if (TransitionGuard.Active)
            {
                // build and place under the fade, one setup per frame (SetupSlot)
                if (TransitionGuard.Settling && (root == null || !CharmMount.Valid) && SetupSlot.Take())
                {
                    if (root == null) Create();
                    else CharmMount.Place(linkLength + ChainStretch);
                }
                Hide();
                return;
            }
            if (root == null) Create();

            // place once the view settles (the game moves its camera in), again if the seat moved
            if (CharmMount.Valid && Time.unscaledTime >= nextEyeCheck)
            {
                nextEyeCheck = Time.unscaledTime + 1f;
                if (CharmMount.EyesMoved()) CharmMount.Forget();
            }
            bool placed = CharmMount.Valid;
            if (!placed && (Time.unscaledTime - cockpitSince < 1f || !SetupSlot.Take() || !CharmMount.Place(linkLength + ChainStretch))) { Hide(); return; }
            if (!placed || Time.unscaledTime - lastShown > 0.5f) Restart(); // new place or back in the cockpit: hang still
            if (builtFor != CharmMount.ChainLength) BuildChain();
            FollowRania();
            KeepLayer();

            // real frame time for velocities, capped for stepping. A hitch measures nothing (no fake knock).
            float frame = Mathf.Max(Time.unscaledDeltaTime, 1e-4f), dt = Mathf.Min(frame, MaxSteps * Step);
            if (frame > MaxSteps * Step) { haveCar = false; left.HadPrev = right.HadPrev = false; }
            if (!ReadCockpit()) { Hide(); return; }
            ReadCar(frame);
            // a recenter jumps the hands without a cut: not a swat, drop a held charm still
            if (CameraDriver.SpaceVersion != seenSpace)
            {
                seenSpace = CameraDriver.SpaceVersion;
                left.HadPrev = right.HadPrev = false;
                if (holder != null)
                {
                    heldVel = N.Vector3.Zero;
                    heldSpin = 0f;
                    holder.PressedAt = -10f; // this press does not grab it again
                    Release();
                }
            }
            ReadHands(frame);
            foreach (var h in hands) // distance for GrabArbiter; held means taken
                GrabArbiter.Report(GrabArbiter.Charm, h.Left, holder == null && h.Active && h.ControllerValid && h.Gap <= GripNear ? h.Gap : float.PositiveInfinity);
            if (holder != null && (!holder.ControllerValid || VRUI.Interactive)) Release();
            if (holder != null) Hold(frame);
            else Simulate(dt);
            if (!Finite())
            {
                // NaN guard: a NaN charm would vanish for good
                if (Time.unscaledTime >= nextNanWarning) Plugin.L.LogWarning("Charm: its state went out of bounds; it hangs still again.");
                nextNanWarning = Time.unscaledTime + 10f;
                Restart();
            }
            Draw();
            Feel();
            Show();
        }

        private static void Restart()
        {
            float chain = CharmMount.ChainLength + ChainStretch;
            ring = new N.Vector3(0f, -chain, 0f);
            head = new N.Vector3(0f, -chain - linkLength, 0f);
            ringVel = headVel = N.Vector3.Zero;
            twist = spin = 0f;
            ringLast = ring;
            headLast = head;
            twistLast = 0f;
            accumulator = 0f;
            haveFrame = haveCar = false;
            left.HadPrev = right.HadPrev = false;
            holder = null;
        }

        // ---------------------------------------------------------------- the cockpit and the HOVA

        /// <summary>This frame's reference. The charm keeps its heading as the HOVA turns; a view cut carries it along.</summary>
        private static bool ReadCockpit()
        {
            CameraDriver.WorldToCockpit(Vector3.zero, Quaternion.identity, out _, out var r);
            var parent = CharmMount.Parent;
            var turn = Q(r);
            var mount = V(parent.TransformPoint(CharmMount.PivotLocal));
            if (!Finite(turn) || !Finite(mount) || turn.LengthSquared() < 0.5f) return false;
            toSet = N.Quaternion.Normalize(turn);
            toWorld = N.Quaternion.Conjugate(toSet);
            mountSet = mount;
            upW = Rotate(toWorld, V(parent.TransformDirection(CharmMount.UpLocal)));
            faceW = Rotate(toWorld, V(parent.TransformDirection(CharmMount.FaceLocal)));
            aheadW = Rotate(toWorld, V(parent.TransformDirection(CharmMount.AheadLocal)));
            obstacle = CharmMount.HasObstacle;
            if (obstacle)
            {
                obstacleRot = N.Quaternion.Normalize(toWorld * Q(CharmMount.ObstacleRotation));
                obstacleInv = N.Quaternion.Conjugate(obstacleRot);
                obstacleCentre = Rotate(toWorld, V(CharmMount.ObstacleCenter) - mount);
                obstacleHalf = V(CharmMount.ObstacleHalf);
            }
            if (!haveFrame) { lastMountSet = mountSet; lastUpW = upW; lastFaceW = faceW; haveFrame = true; }

            // set moved: the charm stays, the chain follows
            var moved = Rotate(toWorld, mountSet - lastMountSet);
            bool cut = CameraDriver.CutThisFrame || moved.Length() > 0.2f
                       || N.Vector3.Dot(upW, lastUpW) < 0.85f || N.Vector3.Dot(faceW, lastFaceW) < 0.85f;
            if (cut)
            {
                var carry = N.Quaternion.Normalize(Look(faceW, upW) * N.Quaternion.Conjugate(Look(lastFaceW, lastUpW)));
                ring = Rotate(carry, ring); head = Rotate(carry, head);
                ringVel = Rotate(carry, ringVel); headVel = Rotate(carry, headVel);
                ringLast = ring; headLast = head; twistLast = twist;
                haveCar = false;
                left.HadPrev = right.HadPrev = false;
            }
            else
            {
                ring -= moved; head -= moved; ringLast -= moved; headLast -= moved;
                var u = Axis(ring, head);
                float turned = SignedAngle(Rotate(Base(u, upW, faceW), N.Vector3.UnitZ), Rotate(Base(u, lastUpW, lastFaceW), N.Vector3.UnitZ), u);
                twist += turned;
                twistLast += turned;
            }
            lastMountSet = mountSet; lastUpW = upW; lastFaceW = faceW;
            return true;
        }

        /// <summary>HOVA acceleration from its smoothed rigidbody velocity. A teleport jump is ignored.</summary>
        private static void ReadCar(float dt)
        {
            var car = Global.Instance != null ? Global.Instance.playerCar : null;
            var body = car != null ? car.rBody : null;
            var v = V(body != null ? body.velocity : CameraDriver.AnchorVelocity);
            if (!haveCar || (v - carVel).Length() > 60f) { carVel = v; haveCar = true; push = N.Vector3.Zero; return; }
            var before = carVel;
            carVel += (v - carVel) * (1f - MathF.Exp(-dt / CarSmoothing));
            push = (carVel - before) / dt;
            float a = push.Length();
            if (a > MaxCarPush) push *= MaxCarPush / a;
        }

        // ---------------------------------------------------------------- the hands

        /// <summary>Hand pieces in the charm's frame, with velocities. No drawn hands: the controller is a ball.</summary>
        private static void ReadHands(float dt)
        {
            foreach (var h in hands)
            {
                var pose = h.Left ? SteamVRInput.LeftHand : SteamVRInput.RightHand;
                h.ControllerValid = pose.Valid && !VRUI.Interactive;
                if (h.ControllerValid)
                {
                    CameraDriver.TrackingToWorld(pose.Position, pose.Rotation, out var wp, out var wr);
                    CameraDriver.WorldToCockpit(wp, wr, out var sp, out var sr);
                    h.CtrlPos = Rotate(toWorld, V(sp) - mountSet);
                    h.CtrlRot = toWorld * Q(sr);
                }

                h.Active = false;
                h.Gap = float.MaxValue;
                var shownHand = VRUI.Interactive ? null : VoxelHands.Shown(h.Left); // menu open: hands leave the cockpit
                if (shownHand == null && !h.ControllerValid) { h.HadPrev = false; continue; }
                if (shownHand != h.Root || h.Pos == null) Collect(h, shownHand);
                bool bad = false;
                for (int i = 0; i < h.Pieces.Length; i++)
                {
                    h.PrevPos[i] = h.Pos[i];
                    h.PrevRot[i] = h.Rot[i];
                    var t = h.Pieces[i];
                    if (t == null)
                    {
                        h.Pos[i] = h.CtrlPos;
                        h.Rot[i] = h.CtrlRot;
                        h.Half[i] = new N.Vector3(ControllerRadius);
                    }
                    else
                    {
                        var rot = Q(t.rotation);
                        var scale = V(t.lossyScale);
                        var centre = V(t.position) + Rotate(rot, h.LocalCenter[i] * scale);
                        h.Pos[i] = Rotate(toWorld, centre - mountSet);
                        h.Rot[i] = toWorld * rot;
                        h.Half[i] = h.LocalHalf[i] * N.Vector3.Abs(scale);
                    }
                    if (!h.HadPrev) { h.PrevPos[i] = h.Pos[i]; h.PrevRot[i] = h.Rot[i]; }
                    if (!Finite(h.Pos[i]) || !Finite(h.Rot[i]) || !Finite(h.Half[i])) { bad = true; break; }
                    h.Vel[i] = (h.Pos[i] - h.PrevPos[i]) / dt;
                    h.AngVel[i] = AngularVelocity(h.PrevRot[i], h.Rot[i], dt);
                }
                if (bad) { h.HadPrev = false; continue; } // no pose yet: touches nothing
                h.HadPrev = true;
                h.Active = true;
                h.Gap = GapTo(h);
                if (h.JustLetGo && h.Gap > 0.005f) h.JustLetGo = false;
            }
        }

        /// <summary>Hand pieces and their mesh bounds, read once. rootT null: controller only.</summary>
        private static void Collect(HandState h, Transform rootT)
        {
            int n = rootT != null ? rootT.childCount : 1;
            h.Root = rootT;
            h.Pieces = new Transform[n];
            h.LocalCenter = new N.Vector3[n]; h.LocalHalf = new N.Vector3[n];
            h.Pos = new N.Vector3[n]; h.PrevPos = new N.Vector3[n]; h.Half = new N.Vector3[n];
            h.Vel = new N.Vector3[n]; h.AngVel = new N.Vector3[n];
            h.Rot = new N.Quaternion[n]; h.PrevRot = new N.Quaternion[n];
            for (int i = 0; rootT != null && i < n; i++)
            {
                var t = rootT.GetChild(i);
                var f = t.GetComponent<MeshFilter>();
                h.Pieces[i] = t;
                if (f == null || f.sharedMesh == null) continue; // nothing drawn: never touches
                var b = f.sharedMesh.bounds;
                h.LocalCenter[i] = V(b.center);
                h.LocalHalf[i] = V(b.extents);
            }
            h.HadPrev = false;
        }

        /// <summary>Hand surface to head box distance, for the grip.</summary>
        private static float GapTo(HandState h)
        {
            var q = MeshPose(Axis(ring, head), twist);
            var qi = N.Quaternion.Conjugate(q);
            var centre = head + Rotate(q, boxCenter);
            float gap = float.MaxValue;
            for (int i = 0; i < h.Pieces.Length; i++)
            {
                if (!Near(h, i)) continue;
                int n = Spheres(h.Pos[i], h.Rot[i], h.Half[i], out float radius);
                for (int k = 0; k < n; k++)
                {
                    var local = Rotate(qi, spheres[k] - centre);
                    gap = MathF.Min(gap, (local - N.Vector3.Clamp(local, -boxHalf, boxHalf)).Length() - radius);
                }
            }
            return gap;
        }

        private static bool Near(HandState h, int i)
        {
            var p = h.Pos[i];
            float along = Math.Clamp(N.Vector3.Dot(p, head) / MathF.Max(head.LengthSquared(), 1e-6f), 0f, 1f);
            return (p - head * along).Length() < Reach + h.Half[i].Length();
        }

        /// <summary>A piece's box as spheres the size of its thinnest side (finger: a row, palm: a grid). Cheap to test.</summary>
        private static int Spheres(N.Vector3 pos, N.Quaternion rot, N.Vector3 half, out float radius)
        {
            float r = radius = MathF.Min(half.X, MathF.Min(half.Y, half.Z));
            if (r < 1e-4f) return 0;
            int nx = Along(half.X, r), ny = Along(half.Y, r), nz = Along(half.Z, r), n = 0;
            for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
            for (int z = 0; z < nz && n < spheres.Length; z++)
            {
                var local = new N.Vector3(
                    nx == 1 ? 0f : (half.X - r) * (2f * x / (nx - 1) - 1f),
                    ny == 1 ? 0f : (half.Y - r) * (2f * y / (ny - 1) - 1f),
                    nz == 1 ? 0f : (half.Z - r) * (2f * z / (nz - 1) - 1f));
                spheres[n++] = pos + Rotate(rot, local);
            }
            return n;
        }

        /// <summary>Spheres of radius r to cover half-length e, at most 1.6 r apart.</summary>
        private static int Along(float e, float r) => 1 + (int)MathF.Ceiling(2f * (e - r) / (1.6f * r));

        // ---------------------------------------------------------------- the simulation

        private static void Simulate(float dt)
        {
            accumulator += dt;
            int steps = 0;
            while (accumulator >= Step && steps < MaxSteps)
            {
                float f = 1f - (accumulator - Step) / dt; // step end within the frame, to lerp the hands
                ringLast = ring; headLast = head; twistLast = twist;
                Substep(Step, Mathf.Clamp01(f));
                accumulator -= Step;
                steps++;
            }
            if (steps == MaxSteps) accumulator = 0f;
        }

        /// <summary>One position-based step: forces, predict, constraints.</summary>
        private static void Substep(float h, float f)
        {
            var g = new N.Vector3(0f, -Gravity, 0f) - push;
            ringVel += g * h;
            headVel += g * h;
            float drag = MathF.Exp(-AirDrag * h);
            ringVel *= drag;
            headVel *= drag;

            // joint friction, momentum kept
            var link = Axis(ring, head);
            var rel = headVel - ringVel;
            var across = (rel - link * N.Vector3.Dot(rel, link)) * (1f - MathF.Exp(-JointDamping * h));
            headVel -= across * (WHead / (WRing + WHead));
            ringVel += across * (WRing / (WRing + WHead));

            // damp the bob along a taut chain
            float len = ring.Length();
            if (len > CharmMount.ChainLength && len > 1e-5f)
            {
                var n = ring / len;
                float along = (N.Vector3.Dot(ringVel, n) * RingMass + N.Vector3.Dot(headVel, n) * HeadMass) / (RingMass + HeadMass);
                var d = n * (along * (1f - MathF.Exp(-ChainDamping * h)));
                ringVel -= d;
                headVel -= d;
            }

            float w0 = 2f * MathF.PI / TwistPeriod;
            spin += (-w0 * w0 * twist - 2f * TwistDampingRatio * w0 * spin) * h;

            var r = ring + ringVel * h;
            var c = head + headVel * h;
            float tw = twist + spin * h;

            Link(ref r, ref c);
            Chain(ref r, h);
            Link(ref r, ref c);
            Walls(ref r, ref c);
            foreach (var hand in hands)
                if (hand.Active && !hand.JustLetGo) Touch(hand, f, h, ref r, ref c, ref tw);
            Tether(ref r, ref c);
            Walls(ref r, ref c); // again, so a hand can't push it through

            // speed cap: a hand appearing inside the head shouldn't launch it
            ringVel = Capped((r - ring) / h);
            headVel = Capped((c - head) / h);
            spin = Math.Clamp((tw - twist) / h, -MaxSpin, MaxSpin);
            ring = r;
            head = c;
            twist = tw;
        }

        private static void Link(ref N.Vector3 r, ref N.Vector3 c)
        {
            var d = c - r;
            float len = d.Length();
            if (len < 1e-6f) return;
            float k = (len - linkLength) / (WRing + WHead);
            d /= len;
            r += d * (k * WRing);
            c -= d * (k * WHead);
        }

        /// <summary>Chain from mount to ring: XPBD elastic when pulled, slack when pushed, hard stop past its give.</summary>
        private static void Chain(ref N.Vector3 r, float h)
        {
            float len = r.Length();
            float over = len - CharmMount.ChainLength;
            if (over <= 0f || len < 1e-6f) return;
            float alpha = ChainCompliance / (h * h);
            r -= r / len * (WRing * over / (WRing + alpha));
            len = r.Length();
            if (len > LongestChain) r *= LongestChain / len;
        }

        private static N.Vector3 Capped(N.Vector3 v)
        {
            float speed = v.Length();
            return speed > MaxSpeed ? v * (MaxSpeed / speed) : v;
        }

        private static float LongestChain => CharmMount.ChainLength * (1f + ChainGive) + ChainStretch;

        /// <summary>Pushed past the chain's give: pull ring and head back together.</summary>
        private static void Tether(ref N.Vector3 r, ref N.Vector3 c)
        {
            float len = r.Length();
            if (len <= LongestChain) return;
            var back = r * (1f - LongestChain / len);
            r -= back;
            c -= back;
        }

        /// <summary>Ceiling, what CharmMount found ahead, and the mirror.</summary>
        private static void Walls(ref N.Vector3 r, ref N.Vector3 c)
        {
            r -= upW * MathF.Max(0f, N.Vector3.Dot(r, upW) + RingBelowMount);
            c -= upW * MathF.Max(0f, N.Vector3.Dot(c, upW) + HeadBelowMount);
            r -= aheadW * MathF.Max(0f, N.Vector3.Dot(r, aheadW) - RingLimit);
            c -= aheadW * MathF.Max(0f, N.Vector3.Dot(c, aheadW) - HeadLimit);
            if (!obstacle) return;
            r = OutOfObstacle(r, RingRadius, false);
            c = OutOfObstacle(c, HeadDepth, true);
        }

        // rear mirror (CharmMount.SetObstacle), in simulation space
        private static bool obstacle;
        private static N.Vector3 obstacleCentre, obstacleHalf;
        private static N.Quaternion obstacleRot = N.Quaternion.Identity, obstacleInv = N.Quaternion.Identity;
        private static int obstacleBumps;
        private static float lastObstacleContact = -10f;

        /// <summary>Pushes a point out of the obstacle box (grown by radius) through the nearest face.</summary>
        private static N.Vector3 OutOfObstacle(N.Vector3 p, float radius, bool count)
        {
            var l = Rotate(obstacleInv, p - obstacleCentre);
            var h = obstacleHalf + new N.Vector3(radius);
            float dx = h.X - MathF.Abs(l.X), dy = h.Y - MathF.Abs(l.Y), dz = h.Z - MathF.Abs(l.Z);
            if (dx <= 0f || dy <= 0f || dz <= 0f) return p;
            if (count)
            {
                if (Time.unscaledTime - lastObstacleContact > 0.3f) obstacleBumps++; // new knock, not resting
                lastObstacleContact = Time.unscaledTime;
            }
            if (dx <= dy && dx <= dz) l.X = MathF.CopySign(h.X, l.X);
            else if (dy <= dz) l.Y = MathF.CopySign(h.Y, l.Y);
            else l.Z = MathF.CopySign(h.Z, l.Z);
            return obstacleCentre + Rotate(obstacleRot, l);
        }

        private static float RingLimit => MathF.Max(CharmMount.RingRoom - RingDepth, 0.005f);
        private static float HeadLimit => MathF.Max(CharmMount.HeadRoom - HeadDepth, 0.005f);

        /// <summary>
        /// One hand against the charm this step: contacts push the head out at the contact point, then friction drags it.
        /// A piece's contacts are averaged, or a flat finger would slowly turn the head. Ring and chain are pushed like beads.
        /// </summary>
        private static void Touch(HandState hand, float f, float h, ref N.Vector3 r, ref N.Vector3 c, ref float tw)
        {
            for (int i = 0; i < hand.Pieces.Length; i++)
            {
                if (!Near(hand, i)) continue;
                var pos = N.Vector3.Lerp(hand.PrevPos[i], hand.Pos[i], f);
                int n = Spheres(pos, N.Quaternion.Slerp(hand.PrevRot[i], hand.Rot[i], f), hand.Half[i], out float radius);
                var q = MeshPose(Axis(r, c), tw);
                var centre = c + Rotate(q, boxCenter);
                N.Vector3 sumR = N.Vector3.Zero, sumC = N.Vector3.Zero;
                float sumT = 0f;
                int contacts = 0;
                for (int k = 0; k < n; k++)
                {
                    var s = spheres[k];
                    var sVel = hand.Vel[i] + N.Vector3.Cross(hand.AngVel[i], s - pos);
                    if (!HeadContact(s, radius, q, centre, out var point, out var normal, out float depth)) continue;
                    Note(hand, N.Vector3.Dot(FreeVelocity(point, r, c) - sVel, normal));

                    // out of the hand, then friction along it
                    Shift(r, c, point, -normal, depth, out var dR, out var dC, out float dT);
                    var slide = PointVelocity(point, r + dR, c + dC, tw + dT, h) - sVel;
                    slide -= normal * N.Vector3.Dot(slide, normal);
                    float speed = slide.Length();
                    if (speed > 1e-4f)
                    {
                        Shift(r + dR, c + dC, point, -slide / speed, MathF.Min(speed * h, Friction * depth), out var fR, out var fC, out float fT);
                        dR += fR; dC += fC; dT += fT;
                    }
                    sumR += dR; sumC += dC; sumT += dT;
                    contacts++;
                }
                if (contacts > 0)
                {
                    r += sumR / contacts;
                    c += sumC / contacts;
                    tw += sumT / contacts;
                }
                for (int k = 0; k < n; k++)
                {
                    var s = spheres[k];
                    var sVel = hand.Vel[i] + N.Vector3.Cross(hand.AngVel[i], s - pos);
                    BeadContact(hand, s, sVel, radius, ref r, 1f, RingRadius);
                    if (r.Length() > CharmMount.ChainLength * 0.9f) BeadContact(hand, s, sVel, radius, ref r, 0.5f, ChainRadius);
                }
            }
        }

        /// <summary>Sphere vs head box: contact point, outward normal, depth.</summary>
        private static bool HeadContact(N.Vector3 s, float radius, N.Quaternion q, N.Vector3 centre, out N.Vector3 point, out N.Vector3 normal, out float depth)
        {
            var local = Rotate(N.Quaternion.Conjugate(q), s - centre);
            var k = N.Vector3.Clamp(local, -boxHalf, boxHalf);
            var d = local - k;
            float dist = d.Length();
            point = normal = N.Vector3.Zero;
            depth = radius - dist;
            if (depth <= 0f) return false;
            N.Vector3 n;
            if (dist > 1e-6f) n = d / dist;
            else
            {
                // centre inside the box: out through the nearest face
                var room = boxHalf - N.Vector3.Abs(local);
                int axis = room.X < room.Y ? (room.X < room.Z ? 0 : 2) : (room.Y < room.Z ? 1 : 2);
                float side = (axis == 0 ? local.X : axis == 1 ? local.Y : local.Z) >= 0f ? 1f : -1f;
                n = axis == 0 ? new N.Vector3(side, 0f, 0f) : axis == 1 ? new N.Vector3(0f, side, 0f) : new N.Vector3(0f, 0f, side);
                depth = radius + (axis == 0 ? room.X : axis == 1 ? room.Y : room.Z);
                k = axis == 0 ? new N.Vector3(side * boxHalf.X, local.Y, local.Z)
                  : axis == 1 ? new N.Vector3(local.X, side * boxHalf.Y, local.Z)
                  : new N.Vector3(local.X, local.Y, side * boxHalf.Z);
            }
            normal = Rotate(q, n);
            point = centre + Rotate(q, k);
            return true;
        }

        /// <summary>The ring (at=1) or mid-chain (at=0.5, moves the ring twice as far).</summary>
        private static void BeadContact(HandState hand, N.Vector3 s, N.Vector3 sVel, float radius, ref N.Vector3 r, float at, float beadRadius)
        {
            var d = r * at - s;
            float dist = d.Length(), reach = radius + beadRadius;
            if (dist >= reach || dist < 1e-6f) return;
            var n = d / dist;
            Note(hand, N.Vector3.Dot(sVel - (r - ring) / Step * at, n));
            r += n * ((reach - dist) / at);
        }

        /// <summary>Moves a point of the charm by amount along dir, split by inverse mass over head, ring and twist.</summary>
        private static void Shift(N.Vector3 r, N.Vector3 c, N.Vector3 point, N.Vector3 dir, float amount, out N.Vector3 dR, out N.Vector3 dC, out float dT)
        {
            dR = dC = N.Vector3.Zero;
            dT = 0f;
            var axis = r - c;
            float len = axis.Length();
            if (len < 1e-6f) return;
            var u = axis / len;
            var rel = point - c;
            float along = N.Vector3.Dot(rel, u);
            float t = along / len;
            float lever = N.Vector3.Dot(N.Vector3.Cross(rel - u * along, dir), u);
            float w = WHead * (1f - t) * (1f - t) + WRing * t * t + WTwist * lever * lever;
            if (w < 1e-6f) return;
            float lambda = amount / w;
            dC = dir * (lambda * WHead * (1f - t));
            dR = dir * (lambda * WRing * t);
            dT = lambda * WTwist * lever;
        }

        /// <summary>A point's velocity so far in this step.</summary>
        private static N.Vector3 PointVelocity(N.Vector3 point, N.Vector3 r, N.Vector3 c, float tw, float h)
        {
            var u = Axis(r, c);
            var rel = point - c;
            float along = N.Vector3.Dot(rel, u);
            float t = along / MathF.Max((r - c).Length(), 1e-6f);
            return ((c - head) * (1f - t) + (r - ring) * t + N.Vector3.Cross(u, rel - u * along) * (tw - twist)) / h;
        }

        /// <summary>A point's velocity at the start of the step.</summary>
        private static N.Vector3 FreeVelocity(N.Vector3 point, N.Vector3 r, N.Vector3 c)
        {
            var u = Axis(r, c);
            var rel = point - c;
            float along = N.Vector3.Dot(rel, u);
            float t = along / MathF.Max((r - c).Length(), 1e-6f);
            return headVel * (1f - t) + ringVel * t + N.Vector3.Cross(u, rel - u * along) * spin;
        }

        /// <summary>Records a contact and its closing speed, for the haptic tick.</summary>
        private static void Note(HandState hand, float closing)
        {
            hand.Touching = true;
            hand.Approach = MathF.Max(hand.Approach, closing);
        }

        // ---------------------------------------------------------------- the grip

        /// <summary>After the game's buttons are set. A strafe press near the charm is the charm's and never strafes.</summary>
        public static void UpdateGrip()
        {
            bool live = !failed && shown;
            if (!live) Release();
            // wheel and lever read the buttons first (GrabArbiter)
            Grip(left, SteamVRInput.StrafeLeft, InputControlType.LeftBumper, live);
            Grip(right, SteamVRInput.StrafeRight, InputControlType.RightBumper, live);
        }

        private static void Grip(HandState h, SteamVRInput.Button button, InputControlType bumper, bool live)
        {
            if (!button.Down)
            {
                if (holder == h) Release();
                h.Swallow = false;
                GrabArbiter.Let(GrabArbiter.Charm, h.Left);
                return;
            }
            float now = Time.unscaledTime;
            if (button.Pressed) h.PressedAt = now;
            if (live && !h.Swallow && now - h.PressedAt <= GrabWindow && h.ControllerValid && h.Active && h.Gap <= GripNear
                && GrabArbiter.Claim(GrabArbiter.Charm, h.Left))
                h.Swallow = true;
            if (live && holder == null && h.Swallow && now - h.PressedAt <= GrabWindow && h.ControllerValid && h.Active && h.Gap <= GrabGap)
                Grab(h);
            if (h.Swallow) VirtualGamepad.Set(bumper, 0f);
        }

        private static void Grab(HandState h)
        {
            holder = h;
            var inv = N.Quaternion.Conjugate(h.CtrlRot);
            holdRot = inv * Orientation(Axis(ring, head), twist);
            holdPos = Rotate(inv, head - h.CtrlPos);
            heldVel = N.Vector3.Zero;
            heldSpin = 0f;
            Haptics.Pulse(h.Left, 0.02f, 0.35f, 160f);
        }

        /// <summary>Head follows the holding hand, within the chain's reach.</summary>
        private static void Hold(float dt)
        {
            var h = holder;
            var rot = h.CtrlRot * holdRot;              // head frame: axis up, face forward
            var c = h.CtrlPos + Rotate(h.CtrlRot, holdPos);
            var u = Rotate(rot, N.Vector3.UnitY);
            var r = c + u * linkLength;

            // twist for this orientation, nearest to the last (unwrapped)
            float tw = SignedAngle(Rotate(Base(u, upW, faceW), N.Vector3.UnitZ), Rotate(rot, N.Vector3.UnitZ), u);
            tw += 2f * MathF.PI * MathF.Round((twist - tw) / (2f * MathF.PI));

            // chain and walls hold it back; slips out once the hand is well past
            var wr = r; var wc = c;
            Tether(ref wr, ref wc);
            Walls(ref wr, ref wc);
            float past = MathF.Max((wr - r).Length(), (wc - c).Length());

            float k = 1f - MathF.Exp(-dt / 0.03f);
            heldVel += ((wc - head) / dt - heldVel) * k;
            heldSpin += ((tw - twist) / dt - heldSpin) * k;
            ring = ringLast = wr;
            ringVel = headVel = heldVel;
            head = headLast = wc;
            twist = twistLast = tw;
            accumulator = 0f;

            if (past > HoldSlack)
            {
                Haptics.Pulse(h.Left, 0.03f, 0.45f, 90f);
                h.PressedAt = -10f; // this press does not grab it again
                Release();
            }
        }

        private static void Release()
        {
            if (holder == null) return;
            ringVel = headVel = heldVel;
            spin = Math.Clamp(heldSpin, -MaxSpin, MaxSpin);
            holder.JustLetGo = true;
            holder = null;
        }

        // ---------------------------------------------------------------- feedback

        /// <summary>Haptic tick on a new touch or a hard swing back, scaled by speed. Resting or holding: nothing.</summary>
        private static void Feel()
        {
            float now = Time.unscaledTime;
            foreach (var h in hands)
            {
                if (h.Touching && h != holder)
                {
                    bool fresh = now - h.LastTouch > 0.12f;
                    if ((fresh && h.Approach > 0.05f) || (h.Approach > 0.4f && now - h.LastTick > 0.15f))
                    {
                        float a = Mathf.Clamp01(h.Approach / 1.2f);
                        Haptics.Pulse(h.Left, 0.008f + 0.022f * a, 0.1f + 0.5f * a, 230f - 80f * a);
                        h.LastTick = now;
                    }
                    h.LastTouch = now;
                }
                h.Touching = false;
                h.Approach = 0f;
            }
        }

        // ---------------------------------------------------------------- drawing

        private static void Create()
        {
            root = new GameObject("CloudpunkVR_Charm");
            UnityEngine.Object.DontDestroyOnLoad(root);
            palette = PaletteTexture("CloudpunkVR_CharmPalette", false);
            glow = PaletteTexture("CloudpunkVR_CharmGlow", true);
            material = new Material(Shader.Find("Standard")) { name = "CloudpunkVR_Charm", mainTexture = palette, hideFlags = HideFlags.DontUnloadUnusedAsset };
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.15f);
            SetGlow(material);

            var shape = CharmModel.BuildHead();
            linkLength = shape.Ring.magnitude;
            hangAxis = N.Vector3.Normalize(V(shape.Ring));
            boxCenter = V(shape.BoxCenter);
            boxHalf = V(shape.BoxHalf);
            // head frame: up along the hang axis, forward to the face
            var face = N.Vector3.UnitZ - hangAxis * N.Vector3.Dot(N.Vector3.UnitZ, hangAxis);
            bodyFrameInverse = N.Quaternion.Conjugate(Look(N.Vector3.Normalize(face), hangAxis));

            chainMesh = new Mesh { name = "CloudpunkVR_CharmChain" };
            chainMesh.MarkDynamic();
            headT = Part("Head", shape.Mesh);
            chainT = Part("Chain", chainMesh);
            mountT = Part("Mount", CharmModel.BuildMount());
            renderers = new[] { headT.GetComponent<MeshRenderer>(), chainT.GetComponent<MeshRenderer>(), mountT.GetComponent<MeshRenderer>() };
            root.SetActive(false);
        }

        private static Transform Part(string name, Mesh mesh)
        {
            var go = new GameObject("CloudpunkVR_Charm" + name);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = true;
            mr.allowOcclusionWhenDynamic = false; // baked occlusion would hide it
            return go.transform;
        }

        /// <summary>Chain mesh: beads and core links, one box each. Triangles and UVs built once per length.</summary>
        private static void BuildChain()
        {
            builtFor = CharmMount.ChainLength;
            beads = Math.Max(4, (int)MathF.Round(builtFor / BeadPitch));
            int boxes = 2 * beads - 1, verts = boxes * 24;
            chainVerts = new Il2CppStructArray<Vector3>(verts);
            chainNormals = new Il2CppStructArray<Vector3>(verts);
            var uvs = new Vector2[verts];
            var tris = new int[boxes * 36];
            for (int b = 0; b < boxes; b++)
            {
                var uv = CharmModel.Uv(b % 2 == 0 ? CharmModel.Gunmetal : CharmModel.Steel, b / 2 % CharmModel.Variants);
                for (int f = 0; f < 6; f++)
                {
                    int v = b * 24 + f * 4, t = b * 36 + f * 6;
                    for (int k = 0; k < 4; k++) uvs[v + k] = uv;
                    tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                    tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
                }
            }
            chainMesh.Clear();
            chainMesh.vertices = chainVerts;
            chainMesh.normals = chainNormals;
            chainMesh.uv = uvs;
            chainMesh.triangles = tris;
        }

        private static void Draw()
        {
            float a = holder != null ? 1f : accumulator / Step;
            var r = N.Vector3.Lerp(ringLast, ring, a);
            var c = N.Vector3.Lerp(headLast, head, a);
            float tw = twistLast + (twist - twistLast) * a;
            headT.SetPositionAndRotation(U(mountSet + Rotate(toSet, c)), UQ(toSet * MeshPose(Axis(r, c), tw)));
            mountT.SetPositionAndRotation(U(mountSet), UQ(toSet * Look(faceW, upW)));
            chainT.SetPositionAndRotation(U(mountSet), Quaternion.identity);
            DrawChain(r, tw);
        }

        /// <summary>Beads from mount to ring: straight when taut, sagging when slack, rolled along with the twist.</summary>
        private static void DrawChain(N.Vector3 r, float tw)
        {
            float chord = r.Length();
            var dir = chord > 1e-5f ? r / chord : -upW;
            var control = r * 0.5f;
            float slack = CharmMount.ChainLength - chord;
            if (slack > 1e-4f)
            {
                float sag = MathF.Min(MathF.Sqrt(3f * MathF.Max(chord, 0.01f) * slack / 8f), CharmMount.ChainLength * 0.5f);
                var down = new N.Vector3(0f, -1f, 0f);
                var side = down - dir * N.Vector3.Dot(down, dir);
                if (side.LengthSquared() < 1e-6f) side = faceW;
                control += N.Vector3.Normalize(side) * (2f * sag);
            }
            N.Vector3 At(float s) => control * (2f * s * (1f - s)) + r * (s * s);
            N.Vector3 Tangent(float s) => N.Vector3.Normalize(control * (2f * (1f - s)) + (r - control) * (2f * s) + dir * 1e-4f);

            int box = 0;
            var prev = N.Vector3.Zero;
            for (int i = 1; i <= beads; i++)
            {
                float s = (float)i / beads;
                var p = At(s);
                var mid = (prev + p) * 0.5f;
                float link = (p - prev).Length();
                WriteBox(box++, mid, Tangent(s - 0.5f / beads), tw * (s - 0.5f / beads), new N.Vector3(CoreSize, CoreSize, link) * 0.5f);
                if (i < beads) WriteBox(box++, p, Tangent(s), tw * s, new N.Vector3(BeadSize, BeadSize, BeadSize) * 0.5f);
                prev = p;
            }
            chainMesh.vertices = chainVerts;
            chainMesh.normals = chainNormals;
            chainMesh.RecalculateBounds();
        }

        /// <summary>Writes one chain box, long side along the chain, rolled by roll (set axes).</summary>
        private static void WriteBox(int index, N.Vector3 centre, N.Vector3 along, float roll, N.Vector3 half)
        {
            var reference = faceW - along * N.Vector3.Dot(faceW, along);
            if (reference.LengthSquared() < 1e-6f) reference = N.Vector3.Cross(upW, along);
            var x = Rotate(N.Quaternion.CreateFromAxisAngle(along, roll), N.Vector3.Normalize(reference));
            var y = N.Vector3.Cross(along, x);
            var ax = Rotate(toSet, x); var ay = Rotate(toSet, y); var az = Rotate(toSet, along);
            var o = Rotate(toSet, centre);
            for (int f = 0; f < 6; f++)
            {
                var corners = CharmModel.FaceCorners[f];
                var axis = f < 2 ? ax : f < 4 ? ay : az;
                var normal = U(f % 2 == 0 ? axis : -axis);
                for (int k = 0; k < 4; k++)
                {
                    int bits = corners[k];
                    var p = o + ax * ((bits & 1) != 0 ? half.X : -half.X) + ay * ((bits & 2) != 0 ? half.Y : -half.Y) + az * ((bits & 4) != 0 ? half.Z : -half.Z);
                    int v = index * 24 + f * 4 + k;
                    chainVerts[v] = U(p);
                    chainNormals[v] = normal;
                }
            }
        }

        private static void Show()
        {
            shown = true;
            lastShown = Time.unscaledTime;
            if (!root.activeSelf) root.SetActive(true);
        }

        private static void Hide()
        {
            shown = false;
            GrabArbiter.Clear(GrabArbiter.Charm);
            Release();
            if (root != null && root.activeSelf) root.SetActive(false);
        }

        /// <summary>A layer the cockpit camera draws (same rule as the hands).</summary>
        private static void KeepLayer()
        {
            var cam = CameraDriver.CockpitCamera;
            int mask = cam.cullingMask, want = 28;
            if ((mask & (1 << want)) == 0)
                for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) { want = i; break; }
            if (want == layer) return;
            layer = want;
            headT.gameObject.layer = chainT.gameObject.layer = mountT.gameObject.layer = layer;
        }

        // ---------------------------------------------------------------- colors and material

        private static Texture2D PaletteTexture(string name, bool glowing)
        {
            var tex = new Texture2D(CharmModel.PaletteSize, CharmModel.Variants, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            for (int v = 0; v < CharmModel.Variants; v++)
                for (int i = 0; i < CharmModel.PaletteSize; i++)
                    tex.SetPixel(i, v, CharmModel.Texel(i, v, glowing));
            tex.Apply(false);
            return tex;
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

        /// <summary>Copies Rania's hand material with our palette, so cockpit lighting and grading match.</summary>
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
                    name = "CloudpunkVR_Charm",
                    mainTexture = palette,
                    mainTextureScale = Vector2.one,
                    mainTextureOffset = Vector2.zero,
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                };
                SetGlow(m);
                material = m;
                foreach (var r in renderers) r.sharedMaterial = m;
                raniaMaterial = true;
                Plugin.L.LogInfo($"Charm: drawn with Rania's material ({m.shader.name}).");
                return;
            }
        }

        // ---------------------------------------------------------------- developer command "charm"

#if DEV
        /// <summary>charm [info] | poke [m/s] [mirror] | swing [deg] | spin [rad/s] | tree | place | at right up ahead (m from the eyes)</summary>
        public static string Dev(string[] a)
        {
            string what = a.Length > 1 ? a[1].ToLowerInvariant() : "info";
            float Arg(int i, float fallback) => a.Length > i ? float.Parse(a[i], CultureInfo.InvariantCulture) : fallback;
            if (!shown && (what == "poke" || what == "swing" || what == "spin")) return Describe(); // nothing to push yet
            switch (what)
            {
                case "poke": // sideways, or toward the mirror
                {
                    var dir = N.Vector3.Cross(upW, faceW);
                    if (a.Length > 3 && a[3].ToLowerInvariant() == "mirror" && obstacle)
                    {
                        var to = obstacleCentre - head;
                        to -= upW * N.Vector3.Dot(to, upW);
                        if (to.LengthSquared() > 1e-6f) dir = N.Vector3.Normalize(to);
                    }
                    var side = dir * Arg(2, 0.8f);
                    ringVel += side;
                    headVel += side;
                    spin += 6f;
                    break;
                }
                case "swing":
                {
                    var axis = N.Vector3.Normalize(N.Vector3.Cross(upW, faceW));
                    var turn = N.Quaternion.CreateFromAxisAngle(axis, Arg(2, 35f) * Mathf.Deg2Rad);
                    ring = ringLast = Rotate(turn, ring);
                    head = headLast = Rotate(turn, head);
                    ringVel = headVel = N.Vector3.Zero;
                    break;
                }
                case "spin":
                    spin = Arg(2, 20f);
                    break;
                case "tree":
                    CharmMount.LogTree();
                    break;
                case "place":
                    CharmMount.Manual = null;
                    CharmMount.Forget();
                    break;
                case "at":
                    CharmMount.Manual = new Vector3(Arg(2, 0.2f), Arg(3, 0.3f), Arg(4, 0.4f));
                    CharmMount.Forget();
                    break;
            }
            return Describe();
        }
#endif

        private static string Describe()
        {
            if (!shown) return $"not shown (setting {VRConfig.CockpitCharm.Value}, view {CameraDriver.Mode}{(failed ? ", off after errors" : "")}); " + CharmMount.Describe();
            float swing = MathF.Acos(Math.Clamp(-N.Vector3.Dot(N.Vector3.Normalize(head), upW), -1f, 1f)) * Mathf.Rad2Deg;
            string Hand(HandState h) => h.Active ? $"{(h.Left ? "left" : "right")} {(h.Gap < 1f ? $"{h.Gap * 100f:0.0} cm away" : "far")}" : $"{(h.Left ? "left" : "right")} hidden";
            return $"{CharmMount.Describe()}; swing {swing:0} deg, twist {twist / (2f * MathF.PI):0.00} turns, spin {spin:0.0} rad/s, " +
                   $"speed {headVel.Length():0.00} m/s, chain {ring.Length() * 100f:0.0} cm, car push {push.Length():0.0} m/s2; " +
                   $"hands {Hand(left)}, {Hand(right)}; held {(holder == null ? "no" : holder.Left ? "left" : "right")}; " +
                   $"strafe buttons kept from the game: left {left.Swallow}, right {right.Swallow}; " +
                   $"layer {layer}, {(raniaMaterial ? "Rania's material" : "standard material")}; " +
                   (obstacle ? $"the rear-view mirror beside him, knocked {obstacleBumps} time(s)" : "nothing hanging beside him");
        }

        // ---------------------------------------------------------------- math (System.Numerics, Unity's goes through the interop)

        private static N.Vector3 V(Vector3 v) => new N.Vector3(v.x, v.y, v.z);
        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Finite(N.Vector3 v) => Finite(v.X) && Finite(v.Y) && Finite(v.Z);
        private static bool Finite(N.Quaternion q) => Finite(q.X) && Finite(q.Y) && Finite(q.Z) && Finite(q.W);
        private static bool Finite() => Finite(ring) && Finite(head) && Finite(ringVel) && Finite(headVel) && Finite(twist) && Finite(spin);
        private static Vector3 U(N.Vector3 v) => new Vector3 { x = v.X, y = v.Y, z = v.Z };
        private static N.Quaternion Q(Quaternion q) => new N.Quaternion(q.x, q.y, q.z, q.w);
        private static Quaternion UQ(N.Quaternion q) => new Quaternion { x = q.X, y = q.Y, z = q.Z, w = q.W };
        private static N.Vector3 Rotate(N.Quaternion q, N.Vector3 v) => N.Vector3.Transform(v, q);

        /// <summary>Unit axis from head centre to ring.</summary>
        private static N.Vector3 Axis(N.Vector3 r, N.Vector3 c)
        {
            var d = r - c;
            float len = d.Length();
            return len > 1e-6f ? d / len : upW;
        }

        /// <summary>Signed angle from a to b about axis (both across it), radians.</summary>
        private static float SignedAngle(N.Vector3 a, N.Vector3 b, N.Vector3 axis) =>
            MathF.Atan2(N.Vector3.Dot(N.Vector3.Cross(a, b), axis), N.Vector3.Dot(a, b));

        /// <summary>Unity's LookRotation: +z along forward, +y toward up.</summary>
        private static N.Quaternion Look(N.Vector3 forward, N.Vector3 up)
        {
            var z = N.Vector3.Normalize(forward);
            var x = N.Vector3.Normalize(N.Vector3.Cross(up, z));
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

        /// <summary>The shortest rotation taking a onto b (unit vectors).</summary>
        private static N.Quaternion FromTo(N.Vector3 a, N.Vector3 b)
        {
            float d = N.Vector3.Dot(a, b);
            if (d < -0.9999f)
            {
                var side = N.Vector3.Cross(a, N.Vector3.UnitX);
                if (side.LengthSquared() < 1e-6f) side = N.Vector3.Cross(a, N.Vector3.UnitY);
                return N.Quaternion.CreateFromAxisAngle(N.Vector3.Normalize(side), MathF.PI);
            }
            var c = N.Vector3.Cross(a, b);
            return N.Quaternion.Normalize(new N.Quaternion(c.X, c.Y, c.Z, 1f + d));
        }

        /// <summary>Untwisted frame for an axis: the rest pose swung the shortest way onto it.</summary>
        private static N.Quaternion Base(N.Vector3 axis, N.Vector3 up, N.Vector3 face) => FromTo(up, axis) * Look(face, up);

        /// <summary>Mesh rotation for an axis and twist (tipped nose-down as it hangs).</summary>
        private static N.Quaternion MeshPose(N.Vector3 axis, float tw) => Orientation(axis, tw) * bodyFrameInverse;

        private static N.Quaternion Orientation(N.Vector3 axis, float tw) =>
            N.Quaternion.CreateFromAxisAngle(axis, tw) * Base(axis, upW, faceW);

        private static N.Vector3 AngularVelocity(N.Quaternion from, N.Quaternion to, float dt)
        {
            var d = to * N.Quaternion.Conjugate(from);
            if (d.W < 0f) d = N.Quaternion.Negate(d);
            var v = new N.Vector3(d.X, d.Y, d.Z);
            float s = v.Length();
            if (s < 1e-6f) return N.Vector3.Zero;
            return v / s * (2f * MathF.Atan2(s, d.W) / dt);
        }
    }
}
