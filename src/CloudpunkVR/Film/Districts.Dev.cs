#if DEV
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CloudpunkVR.Film
{
    /// <summary>
    /// Dev commands to film in any district through the game's own passage (Lift):
    ///   goto list             build scenes, loaded passages, this district's exits
    ///   goto &lt;scene&gt; [exit]   borrows a loaded passage for one trip (PlayerCar.EnterLift, like `lift call`), puts it
    ///                         back after. Bench runs only, or `force`: the routine ends in Global.Autosave, and arriving
    ///                         fires sector discoveries (achievements) and, into Level1, the lift tutorial trigger
    ///   goto stop [force]     stop following a trip (once its exit is settled; force: now)
    ///   spots                 spots-&lt;scene&gt;.json in the shots folder: parking, passages, exits, spawns, sectors, the
    ///                         artists' Cinemachine cameras (also as free cam keys) and traffic lanes, world space
    ///   park &lt;index|name&gt;     the HOVA into one of those parking volumes
    ///
    /// PlayerCar.LiftRoutine copies the passage's destination (build index) and exitID, loads the scene
    /// (SceneLoader.LoadNewScene empties Global.liftExitDictionary first), then does Global.FindLiftExit(exitID), a
    /// plain dictionary lookup. Each LiftExit registers itself in Awake; both ends of a passage share the id ("11" is
    /// Clearwater-Downtown). A missing id throws inside the routine and strands the HOVA, so the exit is settled as
    /// soon as the new scene's exits are in, a few frames before the routine looks.
    /// </summary>
    internal static class Districts
    {
        // id for a borrowed passage with no exit asked (the game's ids are numbers)
        private const string AnyExit = "goto-any";
        private const float LoadTimeout = 90f, ArrivalTimeout = 60f; // logged only, a trip ends with the game's routine

        // Scenes with LiftExit points (build 8245592). Title, apartment and story scenes have none: the HOVA would be stuck.
        private static readonly HashSet<string> PassageScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Level-1_CORA", "Level-1_Ventz", "Level0_Left", "Level0_Middle", "Level0_Right", "Level1", "Level2_Midtown", "Level3", "Level4",
        };

        private static string ShotsDir => Path.Combine(BepInEx.Paths.ConfigPath, "CloudpunkVR-shots");

        private sealed class Trip
        {
            public Lift Lift;
            public Collider Trigger;           // the passage's trigger, off during the trip
            public int OldDestination, Target, From;
            public string OldExit, Asked, Key; // Key: the id the routine looks up
            public bool Retargeted;
            public float StartedAt, InTargetAt = -1f, NextSettle, RetryAt;
            public int EmptyFrames, Exceptions;
            public string Exit, Alias;         // settled exit; key we added to the game's dictionary
            public bool Failed, LateNoted, ExceptionNoted;
            public bool Released;              // `goto stop` before the exit was settled: settle it, then drop quietly

            /// <summary>The routine still needs an exit: don't drop the trip.</summary>
            public bool ExitPending => Exit == null && !Failed;
        }

        private const int ExceptionLimit = 30; // in a row

        private static Trip trip;

        // ---- goto

        /// <summary>`goto &lt;word&gt;` is this command (list, stop, a scene), not Rania's walk to a thing.</summary>
        public static bool Handles(string word) =>
            word.Equals("list", StringComparison.OrdinalIgnoreCase) || word.Equals("stop", StringComparison.OrdinalIgnoreCase) || SceneIndex(word) >= 0;

        public static string Goto(string[] a)
        {
            if (a.Length < 2 || a[1].Equals("list", StringComparison.OrdinalIgnoreCase)) return List();
            if (a[1].Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                // while the routine still needs its exit, only release the trip: it ends once the exit is settled
                if (trip == null) return "no trip under way.";
                string where = SceneName(trip.Target);
                bool now = a.Length > 2 && a[2].Equals("force", StringComparison.OrdinalIgnoreCase);
                if (!now && trip.ExitPending && RoutineMayRun())
                {
                    trip.Released = true;
                    return $"the trip to {where} is released: its exit is still settled when {where} loads (the game's passage needs it), then it ends quietly. " +
                           "`goto stop force` drops it now.";
                }
                Finish(now ? "dropped by hand" : "ended by hand");
                return $"the trip to {where} is no longer followed.";
            }
            int target = SceneIndex(a[1]);
            if (target < 0) return $"no scene '{a[1]}' in the build ({SceneNames()}).";
            string name = SceneName(target);
            string asked = null;
            bool force = false;
            for (int i = 2; i < a.Length; i++)
            {
                if (a[i].Equals("force", StringComparison.OrdinalIgnoreCase)) force = true;
                else if (asked == null) asked = a[i];
            }
            if (trip != null) return $"a trip to {SceneName(trip.Target)} is under way ({Time.unscaledTime - trip.StartedAt:0} s, the game's passage still running; `goto stop` ends it, `goto stop force` at once).";

            var g = Global.Instance;
            var car = g != null ? g.playerCar : null;
            if (car == null || !Global.IsInVehicle) return "fly the HOVA first (a passage takes the HOVA, not Rania).";
            int here = SceneManager.GetActiveScene().buildIndex;
            if (target == here) return $"already in {name}: `park <index|name>` moves the HOVA within it (`spots` lists the parking volumes).";
            var lifts = LoadedLifts(car.transform.position);
            if (!ComesOutIn(target, lifts))
                return $"no passage comes out in {name}: the game's passage would stop there with the HOVA in it. Districts with passages: {PassageSceneList(lifts)}.";
            if (SceneLoader.IsLoading()) return "the game is loading a scene.";
            if (car.IsInRoutine()) return "the HOVA is in one of the game's routines (parking, taking off, a passage): again once it's free.";
            if (lifts.Count == 0) return "no district passage in the loaded scenes.";
            // outside the bench it's for real: autosave at the end of the passage, story triggers, achievements
            if (!Bench.Complete && !force)
                return $"{(Bench.Active ? "the bench's achievement guard is incomplete (see `bench`)" : "not a bench run")}: the game saves at the end of the passage " +
                       $"(your save would wake up in {name}) and achievements aren't all blocked. `goto {name}{(asked != null ? " " + asked : "")} force` goes anyway.";

            // a passage that already leads there, else the nearest one retargeted
            Lift lift = lifts[0];
            for (int i = 0; i < lifts.Count; i++)
                if (lifts[i].destination == target) { lift = lifts[i]; break; }
            var t = new Trip
            {
                Lift = lift, OldDestination = lift.destination, OldExit = lift.exitID, Target = target, From = here, Asked = asked,
                StartedAt = Time.unscaledTime,
            };
            t.Key = asked ?? (lift.destination == target ? lift.exitID : AnyExit);
            t.Retargeted = lift.destination != target || lift.exitID != t.Key;
            if (t.Retargeted)
            {
                lift.destination = target;
                lift.exitID = t.Key;
            }
            // the routine pulls the HOVA through the passage's trigger, which would call EnterLift again
            var trigger = lift.GetComponent<Collider>();
            if (trigger != null && trigger.enabled) { trigger.enabled = false; t.Trigger = trigger; }
            trip = t;

            string how = t.Retargeted
                ? $"passage '{lift.name}' (to {SceneName(t.OldDestination)} exit '{t.OldExit}') pointed at {name} exit '{t.Key}' for this trip"
                : $"passage '{lift.name}' already leads to {name} exit '{t.Key}'";
            Plugin.L.LogInfo($"goto: {how}, {Vector3.Distance(Entry(lift), car.transform.position):0} m from the HOVA; PlayerCar.EnterLift called, its trigger off until the trip is over.");
            if (!Bench.Complete)
                Plugin.L.LogWarning("goto: forced without the bench's full achievement guard: the game autosaves at the end of the passage, and a district entered ahead of the story can unlock achievements on this Steam account.");
            try { car.EnterLift(lift); }
            catch (Exception e) { Finish("EnterLift failed: " + e.Message); return "no trip."; }
            if (!car.IsInRoutine()) { Finish("the game did not start the passage"); return "no trip."; }
            return $"on the way from {SceneName(here)} to {name}{(asked != null ? " exit '" + asked + "'" : "")}; the arrival is logged once the HOVA is out of the passage.";
        }

        /// <summary>Every frame: follow the trip, settle its exit, log the arrival.</summary>
        public static void Tick()
        {
            var t = trip;
            if (t == null || Time.unscaledTime < t.RetryAt) return;
            try
            {
                Follow(t);
                t.Exceptions = 0;
            }
            catch (Exception e)
            {
                // Kept while the routine may run. With the exit still pending it's never dropped (the HOVA would be
                // stranded), only retried twice a second after too many failures.
                if (RoutineMayRun())
                {
                    bool keep = t.ExitPending || ++t.Exceptions < ExceptionLimit;
                    if (t.ExitPending && ++t.Exceptions >= ExceptionLimit) t.RetryAt = Time.unscaledTime + 0.5f;
                    if (keep)
                    {
                        if (!t.ExceptionNoted) { t.ExceptionNoted = true; Plugin.L.LogWarning($"goto: exception while following the trip ({e.Message}); kept while the game's passage runs."); }
                        return;
                    }
                }
                Finish("exception: " + e.Message);
            }
        }

        /// <summary>The HOVA's routine runs, or can't be read.</summary>
        private static bool RoutineMayRun()
        {
            try
            {
                var g = Global.Instance;
                return g != null && g.playerCar != null && g.playerCar.IsInRoutine();
            }
            catch (Exception) { return true; }
        }

        /// <summary>
        /// One frame of a trip. Ends only once the game's routine is over: it reads the passage and exit list late, and
        /// time can stand still (time scale 0, long first load), so timeouts are only logged while it runs.
        /// </summary>
        private static void Follow(Trip t)
        {
            float now = Time.unscaledTime;
            var g = Global.Instance;
            var car = g != null ? g.playerCar : null;
            if (car == null) { Finish("the HOVA is gone"); return; }
            bool running = RoutineMayRun();
            if (t.InTargetAt < 0f)
            {
                if (!Loaded(t.Target))
                {
                    if (!running) { Finish($"the game's passage ended without {SceneName(t.Target)}"); return; }
                    Late(t, now - t.StartedAt > LoadTimeout, $"no {SceneName(t.Target)} yet, {LoadTimeout:0} s after the start");
                    return;
                }
                t.InTargetAt = now;
            }
            if (t.Exit == null && !t.Failed && now >= t.NextSettle) SettleExit(t, g, now);
            if (t.Released && !t.ExitPending) { Finish(null); return; }
            bool loading;
            try { loading = SceneLoader.IsLoading(); }
            catch (Exception) { loading = false; }
            if (running || loading)
            {
                Late(t, now - t.InTargetAt > ArrivalTimeout,
                     t.Exit == null ? $"no exit settled {ArrivalTimeout:0} s after {SceneName(t.Target)} loaded" : $"the HOVA still in the passage {ArrivalTimeout:0} s after {SceneName(t.Target)} loaded");
                return;
            }
            if (t.Exit == null) { Finish($"the game's passage ended before an exit of {SceneName(t.Target)} was settled"); return; }
            Vector3 p = car.transform.position;
            string sector = "";
            try { sector = SectorAt(p, Sectors()); }
            catch (Exception) { /* no sector */ }
            Plugin.L.LogInfo($"goto: arrived in {SceneName(t.Target)}{(sector.Length > 0 ? " (" + sector + ")" : "")} at exit '{t.Exit}' after {now - t.StartedAt:0.0} s: " +
                             $"HOVA at {V(p)}, heading {car.transform.eulerAngles.y:0} degrees.");
            Finish(null);
        }

        /// <summary>Timeout while the routine still runs: logged once, trip still followed.</summary>
        private static void Late(Trip t, bool late, string what)
        {
            if (!late || t.LateNoted || t.Released) return;
            t.LateNoted = true;
            Plugin.L.LogWarning($"goto: {what}; the game's passage routine still runs, so the trip is still followed and the passage and the exit list stay as set " +
                                "(a slow load needs nothing; a HOVA stuck in the passage for good: quit to the title, `goto stop force` forgets the trip).");
        }

        /// <summary>Loaded and awake, active or not.</summary>
        private static bool Loaded(int buildIndex)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (s.buildIndex == buildIndex && s.isLoaded) return true;
            }
            return false;
        }

        /// <summary>Pick the asked exit (or the passage's own), else the first; alias it under the trip's id if needed.</summary>
        private static void SettleExit(Trip t, Global g, float now)
        {
            var exits = ExitsIn(t.Target);
            if (exits.Count == 0)
            {
                // exits register as the scene wakes up: complain after 5 frames, then search twice a second
                if (++t.EmptyFrames == 5)
                    Plugin.L.LogError($"goto FAILED: no passage exit came up in {SceneName(t.Target)}; unless one comes up within frames, the game's passage stops with the HOVA in it (quit to the title or restart the run; `goto stop force` forgets the trip).");
                if (t.EmptyFrames >= 5) t.NextSettle = now + 0.5f;
                if (now - t.InTargetAt > ArrivalTimeout) { t.Failed = true; Plugin.L.LogWarning($"goto: no longer looking for an exit of {SceneName(t.Target)}."); }
                return;
            }
            if (t.EmptyFrames >= 5) Plugin.L.LogInfo($"goto: the exits of {SceneName(t.Target)} came up after {t.EmptyFrames} frames.");
            string want = t.Asked ?? (t.Key != AnyExit ? t.Key : null);
            LiftExit chosen = null;
            for (int i = 0; i < exits.Count && want != null; i++)
                if (exits[i].id == want) { chosen = exits[i]; break; }
            string ids = string.Join(", ", exits.ConvertAll(e => "'" + e.id + "'"));
            string note;
            if (chosen != null) note = $"exit '{want}' found in {SceneName(t.Target)}";
            else
            {
                chosen = exits[0];
                note = want != null && t.Asked != null ? $"no exit '{want}' in {SceneName(t.Target)} (its exits: {ids}): arriving at '{chosen.id}' instead"
                     : want != null ? $"the passage's own exit '{want}' is not in {SceneName(t.Target)} (its exits: {ids}): arriving at '{chosen.id}'"
                     : $"arriving at exit '{chosen.id}' of {SceneName(t.Target)} (its exits: {ids})";
            }
            t.Exit = chosen.id;
            if (t.Key != chosen.id)
            {
                try
                {
                    g.liftExitDictionary[t.Key] = chosen;
                    t.Alias = t.Key;
                    note += $", entered in the game's exit list under the passage's id '{t.Key}' for this trip";
                }
                catch (Exception e)
                {
                    t.Failed = true;
                    Plugin.L.LogError($"goto FAILED: {note}, but the game's exit list refused it ({e.Message}); the passage will stop with the HOVA in it.");
                    return;
                }
            }
            if (chosen.exitPoint != null) note += $"; out at {V(chosen.exitPoint.position)}";
            Plugin.L.LogInfo($"goto: {note}.");
        }

        /// <summary>
        /// Trip over: alias out of the exit list, passage and trigger back. Left alone while the routine still runs
        /// (it reads them later; the next load empties the list anyway).
        /// </summary>
        private static void Finish(string problem)
        {
            var t = trip;
            trip = null;
            if (t == null) return;
            try { CleanUp(t, problem); }
            catch (Exception e) { Plugin.L.LogWarning("goto: clean-up failed: " + e.Message); }
        }

        private static void CleanUp(Trip t, string problem)
        {
            if (problem != null) Plugin.L.LogWarning($"goto: stopped: {problem}.");
            if (RoutineMayRun()) // or unknown
            {
                Plugin.L.LogInfo("goto: the game's passage routine still runs: the passage and the game's exit list are left as set for it.");
                return;
            }
            var parts = new List<string>();
            if (t.Alias != null)
            {
                try
                {
                    var g = Global.Instance;
                    if (g != null && g.liftExitDictionary != null && g.liftExitDictionary.Remove(t.Alias)) parts.Add($"'{t.Alias}' out of the game's exit list");
                }
                catch (Exception e) { parts.Add($"'{t.Alias}' left in the game's exit list ({e.Message}; the next load empties it)"); }
            }
            if (t.Lift != null) // null: gone with its district
            {
                if (t.Retargeted)
                {
                    t.Lift.destination = t.OldDestination;
                    t.Lift.exitID = t.OldExit;
                    parts.Add($"passage '{t.Lift.name}' put back (to {SceneName(t.OldDestination)} exit '{t.OldExit}')");
                }
                if (t.Trigger != null) { t.Trigger.enabled = true; parts.Add("its trigger on again"); }
            }
            else if (t.Retargeted) parts.Add($"the borrowed passage went with {SceneName(t.From)}, nothing to put back");
            if (parts.Count > 0) Plugin.L.LogInfo("goto: " + string.Join("; ", parts) + ".");
        }

        private static string List()
        {
            var sb = new StringBuilder();
            int here = SceneManager.GetActiveScene().buildIndex;
            var g = Global.Instance;
            var car = g != null ? g.playerCar : null;
            Vector3 from = car != null ? car.transform.position : VR.CameraDriver.HeadWorldPosition;
            var lifts = LoadedLifts(from);
            int count = SceneCount();
            sb.Append($"list: {count} scenes in the build:");
            for (int i = 0; i < count; i++) sb.Append($" {i} {SceneName(i)}{(i == here ? " (here)" : "")}{(i + 1 < count ? "," : ";")}");
            sb.Append($" a passage comes out in {PassageSceneList(lifts)}. Now in {SceneName(here)}");
            if (car != null) { string sector = SectorAt(car.transform.position, Sectors()); if (sector.Length > 0) sb.Append($", sector '{sector}'"); }
            sb.Append('.');
            sb.Append($"\n  passages loaded, nearest first ({lifts.Count}):");
            for (int i = 0; i < lifts.Count; i++)
            {
                var l = lifts[i];
                sb.Append($"\n    {i}: '{l.name}' {Vector3.Distance(Entry(l), from):0} m away, to {SceneName(l.destination)} ({l.destination}) exit '{l.exitID}', direction {l.direction}, trigger at {V(Entry(l))}");
            }
            var exits = ExitsIn(here);
            sb.Append($"\n  exits of {SceneName(here)} (`goto {SceneName(here)} <exit>` comes out at one from elsewhere) ({exits.Count}):");
            for (int i = 0; i < exits.Count; i++)
            {
                var e = exits[i];
                sb.Append($"\n    '{e.id}' at {V(e.transform.position)}{(e.exitPoint != null ? ", out to " + V(e.exitPoint.position) : "")}");
            }
            return sb.ToString();
        }

        // ---- spots

        public static string Spots()
        {
            var scene = SceneManager.GetActiveScene();
            string name = scene.name;
            var c = CultureInfo.InvariantCulture;
            var g = Global.Instance;
            var car = g != null ? g.playerCar : null;
            var sectors = Sectors();
            var parking = ParkingVolumes();
            var lifts = LoadedLifts(Vector3.zero);
            lifts.Sort((x, y) => CompareIds(x.exitID ?? "", y.exitID ?? ""));
            var exits = ExitsIn(scene.buildIndex);
            var spawns = SpawnPoints();

            var sb = new StringBuilder("{\n");
            sb.Append($"  \"scene\": \"{Escape(name)}\",\n");
            sb.Append($"  \"buildIndex\": {I(scene.buildIndex)},\n");
            string here = car != null ? SectorAt(car.transform.position, sectors) : "";
            if (here.Length == 0) here = GameSectorName();
            sb.Append($"  \"district\": \"{Escape(here)}\",\n");
            sb.Append($"  \"written\": \"{DateTime.Now.ToString("yyyy-MM-dd'T'HH:mm:ss", c)}\",\n");
            if (car != null) sb.Append($"  \"hova\": {{ \"position\": {J(car.transform.position)}, \"yaw\": {F(car.transform.eulerAngles.y)} }},\n");

            sb.Append("  \"sectors\": [");
            for (int i = 0; i < sectors.Count; i++)
            {
                var (label, key, bounds) = sectors[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append($"    {{ \"name\": \"{Escape(label)}\", \"key\": \"{Escape(key)}\", \"center\": {J(bounds.center)}, \"size\": {J(bounds.size)} }}");
            }
            sb.Append(sectors.Count > 0 ? "\n  ],\n" : "],\n");

            int activeParking = 0;
            sb.Append("  \"parking\": [");
            for (int i = 0; i < parking.Count; i++)
            {
                var s = parking[i];
                if (s.Active) activeParking++;
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append($"    {{ \"index\": {I(i)}, \"name\": \"{Escape(s.Volume.name)}\", \"path\": \"{Escape(s.Path)}\", \"active\": {(s.Active ? "true" : "false")}, " +
                          $"\"center\": {J(s.Center)}, \"size\": {J(s.Size)}, \"sector\": \"{Escape(SectorAt(s.Center, sectors))}\" }}");
            }
            sb.Append(parking.Count > 0 ? "\n  ],\n" : "],\n");

            sb.Append("  \"passages\": [");
            for (int i = 0; i < lifts.Count; i++)
            {
                var l = lifts[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append($"    {{ \"name\": \"{Escape(l.name)}\", \"to\": \"{Escape(SceneName(l.destination))}\", \"toIndex\": {I(l.destination)}, \"exit\": \"{Escape(l.exitID)}\", " +
                          $"\"direction\": {I(l.direction)}, \"trigger\": {J(Entry(l))}{(l.endPoint != null ? ", \"end\": " + J(l.endPoint.position) : "")} }}");
            }
            sb.Append(lifts.Count > 0 ? "\n  ],\n" : "],\n");

            sb.Append("  \"exits\": [");
            for (int i = 0; i < exits.Count; i++)
            {
                var e = exits[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append($"    {{ \"id\": \"{Escape(e.id)}\", \"name\": \"{Escape(e.name)}\", \"position\": {J(e.transform.position)}" +
                          $"{(e.exitPoint != null ? ", \"exitPoint\": " + J(e.exitPoint.position) : "")} }}");
            }
            sb.Append(exits.Count > 0 ? "\n  ],\n" : "],\n");

            sb.Append("  \"spawnPoints\": [");
            for (int i = 0; i < spawns.Count; i++)
            {
                var (p, path, active) = spawns[i];
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append($"    {{ \"name\": \"{Escape(p.name)}\", \"path\": \"{Escape(path)}\", \"active\": {(active ? "true" : "false")}, \"levelEntry\": {(p.registerAsLevelEntry ? "true" : "false")}, " +
                          $"\"position\": {J(p.transform.position)}, \"yaw\": {F(p.transform.eulerAngles.y)} }}");
            }
            sb.Append(spawns.Count > 0 ? "\n  ],\n" : "],\n");

            // the artists' cameras, also as a free cam keyframe
            var cameras = ArtistCameras();
            sb.Append("  \"cameras\": [");
            for (int i = 0; i < cameras.Count; i++)
            {
                var (cam, path, active) = cameras[i];
                var lens = cam.m_Lens;
                var t = cam.transform;
                var pose = CameraPose(t.position, t.rotation, lens);
                var follow = cam.m_Follow;
                var lookAt = cam.m_LookAt;
                // an aiming camera only turns while live, its transform is where it was left; aimedKey looks at the aim point
                string aimed = "", aim = AimStage(cam);
                if (lookAt != null && aim.Length > 0)
                {
                    Vector3 to = AimPoint(cam, lookAt) - t.position;
                    if (to.sqrMagnitude > 1e-4f && Vector3.Cross(to.normalized, Vector3.up).sqrMagnitude > 1e-6f)
                        aimed = $", \"aim\": \"{aim}\", \"aimedKey\": {KeyJson(CameraPose(t.position, Quaternion.LookRotation(to, Vector3.up), lens))}";
                }
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append($"    {{ \"name\": \"{Escape(cam.name)}\", \"path\": \"{Escape(path)}\", \"active\": {(active ? "true" : "false")}, \"priority\": {I(cam.m_Priority)}, " +
                          $"\"position\": {J(t.position)}, \"yaw\": {F(pose.Yaw)}, \"pitch\": {F(pose.Pitch)}, \"roll\": {F(pose.Roll)}, " +
                          $"\"fov\": {F(lens.FieldOfView)}, \"near\": {F(lens.NearClipPlane)}, \"far\": {F(lens.FarClipPlane)}, \"dutch\": {F(lens.Dutch)}" +
                          (follow != null ? $", \"follow\": \"{Escape(follow.name)}\"" : "") + (lookAt != null ? $", \"lookAt\": \"{Escape(lookAt.name)}\"" : "") +
                          $", \"key\": {KeyJson(pose)}{aimed} }}");
            }
            sb.Append(cameras.Count > 0 ? "\n  ],\n" : "],\n");

            var lanes = TrafficLanes();
            int lanePoints = 0;
            sb.Append("  \"traffic\": [");
            for (int i = 0; i < lanes.Count; i++)
            {
                var l = lanes[i];
                lanePoints += l.Points.Count;
                sb.Append(i == 0 ? "\n" : ",\n");
                sb.Append($"    {{ \"name\": \"{Escape(l.Name)}\", \"path\": \"{Escape(l.Path)}\", \"active\": {(l.Active ? "true" : "false")}, " +
                          $"\"connection\": {(l.Connection ? "true" : "false")}, \"closed\": {(l.Closed ? "true" : "false")}, \"length\": {F(l.Length)}, " +
                          $"\"from\": \"{Escape(l.Source)}\", \"points\": [");
                for (int k = 0; k < l.Points.Count; k++) sb.Append(k > 0 ? ", " : "").Append(J(l.Points[k]));
                sb.Append("] }");
            }
            sb.Append(lanes.Count > 0 ? "\n  ]\n" : "]\n");
            sb.Append("}\n");

            string file = Path.Combine(ShotsDir, $"spots-{name}.json");
            try
            {
                Directory.CreateDirectory(ShotsDir);
                File.WriteAllText(file, sb.ToString());
            }
            catch (Exception e) { return $"{name}: not written ({e.Message})."; }
            var names = new List<string>();
            for (int i = 0; i < sectors.Count; i++) if (!names.Contains(sectors[i].label)) names.Add(sectors[i].label);
            return $"{name}{(names.Count > 0 ? " (" + string.Join(", ", names) + ")" : "")}: {parking.Count} parking volumes ({activeParking} on), {lifts.Count} passages, " +
                   $"{exits.Count} exits, {spawns.Count} spawn points, {sectors.Count} sectors, {cameras.Count} cameras, {lanes.Count} traffic lanes ({lanePoints} points) -> {file}";
        }

        // ---- park <index|name>

        /// <summary>The HOVA into the parking volume at that `spots` index, or matching the name.</summary>
        public static void Park(string what)
        {
            var g = Global.Instance;
            var car = g != null ? g.playerCar : null;
            if (car == null) { Plugin.L.LogWarning("park: no player car"); return; }
            var spots = ParkingVolumes();
            if (spots.Count == 0) { Plugin.L.LogWarning("park: no parking volume loaded"); return; }
            int pick = -1, matches = 0;
            if (int.TryParse(what, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                if (n < 0 || n >= spots.Count) { Plugin.L.LogWarning($"park: no parking volume #{n} in {SceneManager.GetActiveScene().name} (0 to {spots.Count - 1}, see `spots`)"); return; }
                pick = n;
            }
            else
            {
                // names repeat ("Park Volume (3)"), so the path matches too: active first, exact name first
                int best = -1;
                for (int i = 0; i < spots.Count; i++)
                {
                    bool exact = spots[i].Volume.name.Equals(what, StringComparison.OrdinalIgnoreCase);
                    if (!exact && spots[i].Path.IndexOf(what, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    matches++;
                    int score = (spots[i].Active ? 2 : 0) + (exact ? 1 : 0);
                    if (score > best) { best = score; pick = i; }
                }
                if (pick < 0) { Plugin.L.LogWarning($"park: no parking volume named like '{what}' in {SceneManager.GetActiveScene().name} (see `spots`)"); return; }
            }
            var s = spots[pick];
            if (!s.Active) { Plugin.L.LogWarning($"park: #{pick} '{s.Path}' is switched off in this game (the story turns it on): pick another"); return; }
            Vector3 p = s.Volume.volumeCollider != null ? s.Volume.volumeCollider.bounds.center : s.Center;
            float d = Vector3.Distance(car.transform.position, p);
            DevCommands.MoveCar(car, p);
            Plugin.L.LogInfo($"park: car moved to #{pick} '{s.Path}' at {V(p)} ({d:0} m away){(matches > 1 ? $"; {matches} volumes match '{what}', the index picks another" : "")}.");
        }

        // ---- What the loaded scenes hold

        private struct Spot
        {
            public ParkVolume Volume;
            public string Path;
            public bool Active;
            public Vector3 Center, Size;
        }

        /// <summary>All parking volumes, on or off, in a stable order so an index means the same one every run.</summary>
        private static List<Spot> ParkingVolumes()
        {
            var list = new List<Spot>();
            var all = Resources.FindObjectsOfTypeAll<ParkVolume>();
            for (int i = 0; i < all.Length; i++)
            {
                var v = all[i];
                if (v == null || !v.gameObject.scene.IsValid()) continue;
                var s = new Spot { Volume = v, Path = Diagnostics.Path(v.transform), Active = v.isActiveAndEnabled && v.volumeCollider != null };
                var col = v.volumeCollider;
                var box = col != null ? col.TryCast<BoxCollider>() : null;
                if (box != null)
                {
                    // from the box: a disabled collider has empty bounds
                    s.Center = box.transform.TransformPoint(box.center);
                    s.Size = Vector3.Scale(box.size, box.transform.lossyScale);
                }
                else if (col != null && col.enabled && col.gameObject.activeInHierarchy) { s.Center = col.bounds.center; s.Size = col.bounds.size; }
                else s.Center = v.transform.position;
                list.Add(s);
            }
            list.Sort((x, y) =>
            {
                int c = string.CompareOrdinal(x.Path, y.Path);
                if (c != 0) return c;
                c = x.Center.x.CompareTo(y.Center.x);
                return c != 0 ? c : x.Center.z.CompareTo(y.Center.z);
            });
            return list;
        }

        /// <summary>Active passages, nearest first.</summary>
        private static List<Lift> LoadedLifts(Vector3 from)
        {
            var lifts = new List<Lift>();
            var all = UnityEngine.Object.FindObjectsOfType<Lift>();
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].isActiveAndEnabled) lifts.Add(all[i]);
            lifts.Sort((x, y) => (Entry(x) - from).sqrMagnitude.CompareTo((Entry(y) - from).sqrMagnitude));
            return lifts;
        }

        private static Vector3 Entry(Lift l)
        {
            var c = l.GetComponent<Collider>();
            return c != null && c.enabled ? c.bounds.center : l.transform.position;
        }

        /// <summary>A scene's active passage exits, by id.</summary>
        private static List<LiftExit> ExitsIn(int buildIndex)
        {
            var exits = new List<LiftExit>();
            var all = UnityEngine.Object.FindObjectsOfType<LiftExit>();
            for (int i = 0; i < all.Length; i++)
            {
                var e = all[i];
                if (e != null && e.gameObject.scene.buildIndex == buildIndex && e.id != null) exits.Add(e);
            }
            exits.Sort((x, y) => CompareIds(x.id, y.id));
            return exits;
        }

        private static int CompareIds(string x, string y)
        {
            bool nx = int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ix);
            bool ny = int.TryParse(y, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iy);
            if (nx && ny) return ix.CompareTo(iy);
            return nx != ny ? (nx ? -1 : 1) : string.CompareOrdinal(x, y);
        }

        private static List<(CharacterSpawnPoint point, string path, bool active)> SpawnPoints()
        {
            var list = new List<(CharacterSpawnPoint, string, bool)>();
            var all = Resources.FindObjectsOfTypeAll<CharacterSpawnPoint>();
            for (int i = 0; i < all.Length; i++)
            {
                var p = all[i];
                if (p != null && p.gameObject.scene.IsValid()) list.Add((p, Diagnostics.Path(p.transform), p.isActiveAndEnabled));
            }
            list.Sort((x, y) => string.CompareOrdinal(x.Item2, y.Item2));
            return list;
        }

        /// <summary>Pose as a free cam key: roll includes Cinemachine's Dutch; rounded and clamped like a saved shot.</summary>
        private static ShotPose CameraPose(Vector3 position, Quaternion rotation, Cinemachine.LensSettings lens)
        {
            var pose = ShotPose.From(position, rotation, lens.FieldOfView);
            pose.Roll = ShotPose.Angle(pose.Roll + lens.Dutch);
            pose = pose.Rounded();
            pose.Pitch = Mathf.Clamp(pose.Pitch, -89.9f, 89.9f);
            pose.Fov = Mathf.Clamp(pose.Fov, 5f, 120f);
            return pose;
        }

        /// <summary>The Aim stage if it aims at the LookAt target (Composer, GroupComposer, HardLookAt), else "".</summary>
        private static string AimStage(Cinemachine.CinemachineVirtualCamera cam)
        {
            try
            {
                var c = cam.GetCinemachineComponent(Cinemachine.CinemachineCore.Stage.Aim);
                if (c == null) return "";
                if (c.TryCast<Cinemachine.CinemachineGroupComposer>() != null) return "group composer";
                if (c.TryCast<Cinemachine.CinemachineComposer>() != null) return "composer";
                if (c.TryCast<Cinemachine.CinemachineHardLookAt>() != null) return "hard look at";
            }
            catch (Exception) { /* no aimed key */ }
            return "";
        }

        /// <summary>The target plus a composer's tracked offset (screen framing and look-ahead ignored).</summary>
        private static Vector3 AimPoint(Cinemachine.CinemachineVirtualCamera cam, Transform lookAt)
        {
            Vector3 p = lookAt.position;
            try
            {
                var c = cam.GetCinemachineComponent(Cinemachine.CinemachineCore.Stage.Aim);
                var composer = c != null ? c.TryCast<Cinemachine.CinemachineComposer>() : null;
                if (composer != null) p += lookAt.rotation * composer.m_TrackedObjectOffset;
            }
            catch (Exception) { /* the target itself */ }
            return p;
        }

        /// <summary>A keyframe as a path file has it.</summary>
        private static string KeyJson(ShotPose p) =>
            $"{{ \"x\": {K(p.Position.x)}, \"y\": {K(p.Position.y)}, \"z\": {K(p.Position.z)}, \"yaw\": {K(p.Yaw)}, \"pitch\": {K(p.Pitch)}, \"roll\": {K(p.Roll)}, \"fov\": {K(p.Fov)} }}";

        /// <summary>The district's Cinemachine cameras, on or off (not the HOVA's).</summary>
        private static List<(Cinemachine.CinemachineVirtualCamera cam, string path, bool active)> ArtistCameras()
        {
            var list = new List<(Cinemachine.CinemachineVirtualCamera, string, bool)>();
            var all = Resources.FindObjectsOfTypeAll<Cinemachine.CinemachineVirtualCamera>();
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null || !InDistrict(c.gameObject)) continue;
                list.Add((c, Diagnostics.Path(c.transform), c.isActiveAndEnabled));
            }
            list.Sort((x, y) => string.CompareOrdinal(x.Item2, y.Item2));
            return list;
        }

        private sealed class Lane
        {
            public string Name, Path, Source;
            public bool Active, Connection, Closed;
            public float Length;
            public List<Vector3> Points;
        }

        private const float LaneStep = 10f; // m, closer where it turns

        /// <summary>
        /// Traffic lanes (VehicleTrack) as world polylines. Cars step straight between the baked samples
        /// (GetSampledPositionSimple), so those, thinned; unbaked lanes give their Catmull-Rom control points.
        /// </summary>
        private static List<Lane> TrafficLanes()
        {
            var list = new List<Lane>();
            var all = Resources.FindObjectsOfTypeAll<VehicleTrack>();
            var raw = new List<Vector3>();
            for (int i = 0; i < all.Length; i++)
            {
                var track = all[i];
                if (track == null || !InDistrict(track.gameObject)) continue;
                var lane = new Lane { Name = track.name, Path = Diagnostics.Path(track.transform), Active = track.isActiveAndEnabled, Connection = track.isConnection, Length = track.length };
                raw.Clear();
                try
                {
                    var spline = track.spline;
                    var samples = track.bakedSamples;
                    if (samples != null && samples.Length > 1)
                    {
                        lane.Source = "baked";
                        for (int k = 0; k < samples.Length; k++) raw.Add(samples[k].position);
                    }
                    else if (spline != null && spline.points != null)
                    {
                        lane.Source = "spline points";
                        var points = spline.points;
                        for (int k = 0; k < points.Count; k++)
                            if (points[k] != null) raw.Add(spline.TransformPoint(points[k].position));
                    }
                    else lane.Source = "none";
                    lane.Closed = spline != null ? spline.isLooping : raw.Count > 2 && (raw[0] - raw[raw.Count - 1]).sqrMagnitude < 1f;
                }
                catch (Exception e) { lane.Source = "unreadable: " + e.Message; }
                lane.Points = Thin(raw);
                list.Add(lane);
            }
            list.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));
            return list;
        }

        /// <summary>A point every LaneStep m and at turns over 8 degrees; ends kept.</summary>
        private static List<Vector3> Thin(List<Vector3> raw)
        {
            var kept = new List<Vector3>();
            if (raw.Count == 0) return kept;
            kept.Add(raw[0]);
            Vector3 heading = Vector3.zero;
            for (int i = 1; i < raw.Count - 1; i++)
            {
                Vector3 d = raw[i] - kept[kept.Count - 1];
                float m = d.magnitude;
                bool turns = m >= 2f && heading != Vector3.zero && Vector3.Angle(heading, d) > 8f;
                if (m < LaneStep && !turns) continue;
                heading = d / m;
                kept.Add(raw[i]);
            }
            if (raw.Count > 1) kept.Add(raw[raw.Count - 1]);
            return kept;
        }

        /// <summary>In a district scene, not DontDestroyOnLoad (HOVA, managers).</summary>
        private static bool InDistrict(GameObject go)
        {
            var s = go.scene;
            return s.IsValid() && s.buildIndex >= 0;
        }

        /// <summary>Active sectors (the names the game shows): name, key, area.</summary>
        private static List<(string label, string key, Bounds bounds)> Sectors()
        {
            var list = new List<(string, string, Bounds)>();
            var all = UnityEngine.Object.FindObjectsOfType<Sector>();
            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i];
                if (s == null) continue;
                var col = s.GetComponent<Collider>();
                string key = s.localizedName ?? "";
                string label = !string.IsNullOrEmpty(s.localizedName2) ? s.localizedName2 : key.Length > 0 ? key : s.name;
                list.Add((label, key, col != null && col.enabled ? col.bounds : new Bounds(s.transform.position, Vector3.zero)));
            }
            list.Sort((x, y) => string.CompareOrdinal(x.Item1, y.Item1));
            return list;
        }

        /// <summary>The smallest sector holding the point, else "".</summary>
        private static string SectorAt(Vector3 p, List<(string label, string key, Bounds bounds)> sectors)
        {
            string best = "";
            float bestVolume = float.MaxValue;
            for (int i = 0; i < sectors.Count; i++)
            {
                var b = sectors[i].bounds;
                if (!b.Contains(p)) continue;
                float volume = b.size.x * b.size.y * b.size.z;
                if (volume < bestVolume) { bestVolume = volume; best = sectors[i].label; }
            }
            return best;
        }

        /// <summary>The sector name the game shows now.</summary>
        private static string GameSectorName()
        {
            try { var g = Global.Instance; return g != null ? g.SectorName ?? "" : ""; }
            catch (Exception) { return ""; }
        }

        private static bool ComesOutIn(int scene, List<Lift> lifts)
        {
            if (PassageScenes.Contains(SceneName(scene))) return true;
            for (int i = 0; i < lifts.Count; i++) if (lifts[i].destination == scene) return true;
            return false;
        }

        private static string PassageSceneList(List<Lift> lifts)
        {
            var names = new List<string>();
            for (int i = 0; i < SceneCount(); i++) if (ComesOutIn(i, lifts)) names.Add(SceneName(i));
            return string.Join(", ", names);
        }

        // ---- Scenes of the build

        private static string[] sceneNames;

        // build 8245592's scenes, in case the engine's list can't be read
        private static readonly string[] BuildScenes =
        {
            "Startup", "Main", "Level-1_CORA", "Level-1_Ventz", "Level0_Left", "Level1", "Level2_Midtown", "Level3", "Level4",
            "Level5_PizzaCEO", "Level5_JayK", "Apartment", "Level0_Middle", "Level0_Right", "Level_Lift",
        };

        private static int SceneCount()
        {
            if (sceneNames != null) return sceneNames.Length;
            try
            {
                int n = SceneManager.sceneCountInBuildSettings;
                var names = new string[n];
                for (int i = 0; i < n; i++) names[i] = Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(i));
                sceneNames = names;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning($"goto: the engine's scene list is unreadable ({e.Message}); the names of build 8245592 are used");
                sceneNames = BuildScenes;
            }
            return sceneNames.Length;
        }

        private static string SceneName(int i) => i >= 0 && i < SceneCount() && !string.IsNullOrEmpty(sceneNames[i]) ? sceneNames[i] : "scene " + i;

        /// <summary>Build index by name or number; -1 if none.</summary>
        private static int SceneIndex(string s)
        {
            int n = SceneCount();
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i >= 0 && i < n ? i : -1;
            for (i = 0; i < n; i++) if (string.Equals(sceneNames[i], s, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string SceneNames()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < SceneCount(); i++) sb.Append(i > 0 ? ", " : "").Append(SceneName(i));
            return sb.ToString();
        }

        // ---- Formatting

        // invariant culture (some cultures print minus as U+2212), NaN as null
        private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        private static string F(float v) => float.IsFinite(v) ? v.ToString("0.##", CultureInfo.InvariantCulture) : "null";
        private static string K(float v) => float.IsFinite(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "null"; // keyframe precision
        private static string J(Vector3 v) => $"[{F(v.x)}, {F(v.y)}, {F(v.z)}]";
        private static string V(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

        /// <summary>JSON string escape, control characters as \u00XX.</summary>
        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '\\' || ch == '"') sb.Append('\\').Append(ch);
                else if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
#endif
