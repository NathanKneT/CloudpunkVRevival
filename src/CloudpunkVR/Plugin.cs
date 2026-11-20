using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace CloudpunkVR
{
    [BepInPlugin(Guid, Name, Version)]
    public partial class Plugin : BasePlugin
    {
        public const string Guid = "com.cloudpunkvr.plugin";
        public const string Name = "Cloudpunk VR";
        public const string Version = "0.3.0";

        internal static ManualLogSource L;
        internal static Plugin Instance;
        internal static string PluginDir;

        public override void Load()
        {
            Instance = this;
            L = Log;
            PluginDir = System.IO.Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            // shared with the preloader patcher, which reads StartInVR
            VRConfig.Bind(new BepInEx.Configuration.ConfigFile(System.IO.Path.Combine(Paths.ConfigPath, "CloudpunkVR.cfg"), true));

            L.LogMessage($"{Name} {Version} loading (game build check: {GameBuild.Describe()})");
#if DEV
            L.LogWarning("Developer build: test commands and probes are compiled in.");
            try { SetUpBench(); } // before anything else is patched
            catch (System.Exception e) { L.LogError("Bench guard: " + e.Message); }
#endif

            ClassInjector.RegisterTypeInIl2Cpp<Diagnostics>();
            AddComponent<Diagnostics>();

            // the patcher already picked VR or flat (StartInVR, --vr, --novr)
            string start = System.Environment.GetEnvironmentVariable("CLOUDPUNKVR_START") ?? "vr:unknown";
            if (start.StartsWith("flat:"))
            {
                string reason = start.Substring(5);
                L.LogMessage($"Flat launch ({reason}); the VR systems stay off.");
                if (reason == "SteamVR is not running")
                {
                    ClassInjector.RegisterTypeInIl2Cpp<FlatNotice>();
                    AddComponent<FlatNotice>();
                }
                ClassInjector.RegisterTypeInIl2Cpp<Film.FreeCam>(); // filming, flat game only
                AddComponent<Film.FreeCam>();
                return;
            }

            VR.VRSession.InstallNativeResolver();
            ClassInjector.RegisterTypeInIl2Cpp<VRManager>();
            ClassInjector.RegisterTypeInIl2Cpp<VR.RenderHook>();
            ApplyPatches();
            AddComponent<VRManager>();
            System.AppDomain.CurrentDomain.ProcessExit += (_, _) => Input.AdaptiveTriggers.FreeAtExit();
        }

#if DEV
        /// <summary>Bench runs: Steam achievements blocked (Bench.Dev.cs).</summary>
        static partial void SetUpBench();
#endif

        /// <summary>One patch class at a time, so a changed game method costs one feature, not the whole mod.</summary>
        private static void ApplyPatches()
        {
            var harmony = new Harmony(Guid);
            int ok = 0, failed = 0;
            foreach (var type in typeof(Plugin).Assembly.GetTypes())
            {
                // inside the try: reading the attributes resolves game types that may be missing
                try
                {
                    if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (System.Exception e) { failed++; L.LogWarning($"Patch {type.Name} not applied: {e.GetBaseException().Message}"); }
            }
            L.LogInfo($"Harmony patches: {ok} applied" + (failed > 0 ? $", {failed} failed" : "") + ".");
        }
    }
}
