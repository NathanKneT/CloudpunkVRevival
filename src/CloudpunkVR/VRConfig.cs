using BepInEx.Configuration;

namespace CloudpunkVR
{
    public enum TurnMode { Snap, Smooth, Off }
    public enum Hand { Right, Left }
    public enum ScreenEffectLevel { Full, Reduced, Off }
    public enum PanelFollow { Smooth, Lazy, Fixed }
    public enum RainMode { World, Overlay, Off }
    public enum MirrorQuality { Off, Low, High, Game }
    public enum SpectatorFormat { Landscape, Portrait, PortraitTurned }

    /// <summary>All user settings, stored in BepInEx/config/CloudpunkVR.cfg.</summary>
    internal static class VRConfig
    {
        // General
        public static ConfigEntry<int> MaxEyeResolution;
        public static ConfigEntry<float> RenderScale;
        public static ConfigEntry<bool> AutoRenderScale;
        public static ConfigEntry<bool> Diagnostics;
#if DEV
        public static ConfigEntry<bool> StereoTest;
        public static ConfigEntry<bool> DevCommandFile;
        public static bool DevHooks => DevCommandFile.Value;
#else
        public static bool DevHooks => false;
#endif
        public static ConfigEntry<bool> StereoFixes;
        public static ConfigEntry<bool> OcclusionCulling;

        // Comfort
        public static ConfigEntry<TurnMode> Turn;
        public static ConfigEntry<float> SnapTurnAngle;
        public static ConfigEntry<float> SmoothTurnSpeed;
        public static ConfigEntry<bool> FlightVignette;
        public static ConfigEntry<float> VignetteStrength;
        public static ConfigEntry<bool> LevelHorizonInCockpit;
        public static ConfigEntry<bool> HeadRelativeWalking;
        public static ConfigEntry<bool> FirstPersonOnFoot;
        public static ConfigEntry<bool> StartInCockpit;
        public static ConfigEntry<bool> CameraShake;
        public static ConfigEntry<ScreenEffectLevel> ScreenEffects;
        public static ConfigEntry<RainMode> Rain;
        public static ConfigEntry<bool> CockpitWindowBlur;
        public static ConfigEntry<bool> Jog;
        public static ConfigEntry<float> JogSpeed;
        public static ConfigEntry<bool> CrouchToSneak;
        public static ConfigEntry<bool> ReachToInteract;
        public static ConfigEntry<bool> LookHighlight;

        // Body
        public static ConfigEntry<float> HeightOffset;
        public static ConfigEntry<bool> Hands;
        public static ConfigEntry<bool> CockpitCharm;
        public static ConfigEntry<bool> CockpitCoffee;
        public static ConfigEntry<bool> FingersFromTouch;
        public static ConfigEntry<bool> GrabWheel;
        public static ConfigEntry<float> WheelLock;
        public static ConfigEntry<bool> GrabLever;

        // Interface
        public static ConfigEntry<float> ScreenDistance;
        public static ConfigEntry<float> MenuAngle;
        public static ConfigEntry<float> HudDistance;
        public static ConfigEntry<float> HudAngle;
        public static ConfigEntry<float> HudScale;
        public static ConfigEntry<Hand> PointerHand;
        public static ConfigEntry<Input.PromptStyle> ButtonPrompts;
        public static ConfigEntry<bool> EyeSelect;
        public static ConfigEntry<bool> MenuFocus;
        public static ConfigEntry<PanelFollow> MenuFollow;
        public static ConfigEntry<bool> WristDisplay;
        public static ConfigEntry<bool> HudOnWindshield;
        public static ConfigEntry<bool> CockpitRadar;
        public static ConfigEntry<bool> SkipTitleScreen;
        public static ConfigEntry<bool> MenuSign;
        public static ConfigEntry<float> MirrorAngle;
        public static ConfigEntry<bool> RearMirror;

        // Feedback
        public static ConfigEntry<bool> Haptics;
        public static ConfigEntry<float> HapticsStrength;
        public static ConfigEntry<bool> EngineRumble;
        public static ConfigEntry<bool> AdaptiveTriggers;

        // Performance
        public static ConfigEntry<float> TrafficDensity;
        public static ConfigEntry<MirrorQuality> Mirrors;

        // Free camera (flat game, for filming)
        public static ConfigEntry<bool> FreeCamEnabled;
        public static ConfigEntry<float> FreeCamMoveSpeed;
        public static ConfigEntry<float> FreeCamLookSpeed;
        public static ConfigEntry<float> FreeCamDuration;
        public static ConfigEntry<float> FreeCamEase;
        public static ConfigEntry<float> FreeCamFollowSmoothing;
        public static ConfigEntry<int> FreeCamFramerate;
        public static ConfigEntry<Film.FrameFormat> FreeCamSaveFrames;
        public static ConfigEntry<bool> FreeCamKeepOcclusion;

        // Spectator
        public static ConfigEntry<bool> SpectatorEnabled;
        public static ConfigEntry<SpectatorFormat> SpectatorFormat;
        public static ConfigEntry<float> SpectatorFov;
        public static ConfigEntry<float> SpectatorSmoothing;
        public static ConfigEntry<float> SpectatorTilt;
        public static ConfigEntry<string> SpectatorKey;

        // Filming
        public static ConfigEntry<bool> CleanFrame;
        public static ConfigEntry<bool> CleanFrameKeepDialogue;
        public static ConfigEntry<string> CleanFrameKey;

        public static void Bind(ConfigFile cfg)
        {
            // [General] StartInVR is bound by the preloader patcher
            MaxEyeResolution = cfg.Bind("General", "MaxEyeResolution", 2200, new ConfigDescription(
                "Largest side, in pixels, of the image drawn for each eye. SteamVR sizes it from the graphics card, not from the game: on a recent card it asks for over 4000 x 4000 per eye, four times the pixels of a PS VR2's screen, which this game cannot draw 90 or 120 times a second. 2200 is a little more than most headsets' screens, but less than very high-resolution ones (2560 and more per eye): raise it to your headset's width or more there. At 90 Hz try 2400. Raise it for sharpness if the log shows room (Performance lines), lower it if the game stutters; 0 always uses SteamVR's size (with AutoRenderScale = false and RenderScale = 1 the mod then leaves the eye resolution alone).",
                new AcceptableValueRange<int>(0, 8192)));
            RenderScale = cfg.Bind("General", "RenderScale", 1.0f, new ConfigDescription(
                "Share of each eye's resolution (after MaxEyeResolution) actually drawn, per side (0.5 to 1). Lower it if the game stutters.",
                new AcceptableValueRange<float>(0.5f, 1f)));
            StereoFixes = cfg.Bind("General", "StereoFixes", true,
                "Fix the game's image effects that come out different in the two eyes: no bloom smoothing over time, lens flares, lens dirt or starburst; no volumetric light smoothing over time; no sun rays; the sky, the city's fog and the volumetric light's beams and halos drawn for each eye. false keeps the game's own settings (from the next launch).");
            AutoRenderScale = cfg.Bind("General", "AutoRenderScale", true,
                "When the graphics card cannot draw a frame for every refresh of the headset, draw fewer pixels: down within two seconds to the step that fits (10% less per side per step, down to 80%, 70% at 90 Hz and below). Back up one step at a time after a calm spell, only while a menu or a black screen hides the change, at most every two minutes. Each step re-creates the eye images, a short hitch, so steps are rare. If even the lowest step is too much, the log suggests 90 Hz or a lower MaxEyeResolution.");
#if DEV
            StereoTest = cfg.Bind("General", "StereoTest", false,
                "Test mode for something that looks different in the two eyes: every 12 seconds one of the game's effects is switched off and a label in the headset says which. Note under which label the difference goes away.");
#endif
            OcclusionCulling = cfg.Bind("General", "OcclusionCulling", true,
                "The game's own occlusion culling (GDOC), which hides what buildings cover. In VR it runs in its two-eye mode; false turns it off (more to draw, but nothing can differ between the eyes).");
            Diagnostics = cfg.Bind("General", "Diagnostics", false,
                "Write a detailed dump of cameras, canvases and input to the BepInEx log on each scene change (for bug reports).");
#if DEV
            DevCommandFile = cfg.Bind("Developer", "CommandFile", false,
                "For automated tests only: read commands from BepInEx/config/CloudpunkVR.devcmd (see DevCommands.cs).");
#else
            // dev-only settings
            Retire(cfg, "General", "StereoTest");
            Retire(cfg, "Developer", "CommandFile");
#endif

            Turn = cfg.Bind("Comfort", "TurnMode", TurnMode.Snap,
                "How the right stick turns you on foot: Snap (comfortable), Smooth, or Off (turn with your body).");
            SnapTurnAngle = cfg.Bind("Comfort", "SnapTurnAngle", 30f, new ConfigDescription(
                "Degrees per snap turn.", new AcceptableValueRange<float>(10f, 90f)));
            SmoothTurnSpeed = cfg.Bind("Comfort", "SmoothTurnSpeed", 120f, new ConfigDescription(
                "Degrees per second for smooth turning.", new AcceptableValueRange<float>(30f, 360f)));
            FlightVignette = cfg.Bind("Comfort", "FlightVignette", true,
                "Darken the edges of your view when the HOVA turns hard, falls fast, swerves, crashes or brakes hard. Reduces motion sickness.");
            VignetteStrength = cfg.Bind("Comfort", "VignetteStrength", VignetteStrengthDefault, new ConfigDescription(
                "How much of the view the vignette can cover (0 = none, 1 = strong). It comes with hard turns, fast falls, swerves, crashes and hard stops; not when flying straight on, in a wide turn or between levels.",
                new AcceptableValueRange<float>(0f, 1f)));
            LevelHorizonInCockpit = cfg.Bind("Comfort", "LevelHorizonInCockpit", true,
                "Keep the horizon level in the cockpit: the HOVA's banking does not tilt your head.");
            HeadRelativeWalking = cfg.Bind("Comfort", "HeadRelativeWalking", true,
                "On foot, pushing the stick forward walks where you look (otherwise where the controller points).");
            FirstPersonOnFoot = cfg.Bind("Comfort", "FirstPersonOnFoot", true,
                "On foot the view stays first person: the game's own first-person camera, even in a new game, which starts behind Rania. " +
                "false leaves the game's on-foot cameras alone (behind Rania, not made for a headset); Square on foot is then the game's camera unlock, as on a pad.");
            StartInCockpit = cfg.Bind("Comfort", "StartInCockpit", true,
                "Switch the HOVA to its cockpit view each time you get in (the outside view stays one click away).");
            CameraShake = cfg.Bind("Comfort", "CameraShake", false,
                "Keep the game's camera wobble and shake effects. Off by default: in a headset they move your eyes without your head.");
            Jog = cfg.Bind("Comfort", "Jog", true,
                "On foot, click the left stick while walking to jog; let the stick go to stop. The game itself has no run.");
            JogSpeed = cfg.Bind("Comfort", "JogSpeed", 1.6f, new ConfigDescription(
                "Jogging speed, as a share of the game's walking speed.", new AcceptableValueRange<float>(1.1f, 2.5f)));
            CrouchToSneak = cfg.Bind("Comfort", "CrouchToSneak", true,
                "On foot, lowering your head about 40 cm below where it was at the last recenter makes Rania sneak, as L2 does.");
            ReachToInteract = cfg.Bind("Comfort", "ReachToInteract", true,
                "On foot, bring a hand to the person or thing the game offers to interact with and squeeze the grip (L1 / R1 on PS VR2 Sense): the same as Cross. The grips do nothing else on foot.");
            LookHighlight = cfg.Bind("Comfort", "LookHighlight", true,
                "On foot, what you can take or use glows softly while you look at it (with your eyes on PS VR2 with the PSVR2 Toolkit, else with your head), and becomes the one Cross and your hand act on.");
            Rain = cfg.Bind("Comfort", "Rain", RainMode.World,
                "How the rain falls outside the HOVA. World: the game's own raindrops (those it shows around the cockpit) fall around you. Overlay: the streaks the game draws over the whole picture, as on a monitor; in a headset they stay in front of your eyes when you turn your head. Off: neither, a little lighter for the graphics card. Puddles, ripples and the windscreen's drops stay either way.");
            CockpitWindowBlur = cfg.Bind("Comfort", "CockpitWindowBlur", false,
                "The game frosts the HOVA's windows where the wipers do not reach: the city behind them is blurred. That blur is made from a flat copy of the picture and came out different in each eye in a headset (lights smeared in one eye, sharp in the other). false (default) keeps the glass, its raindrops, wipers and cracks, clear and the same in both eyes; true brings the game's blur back.");
            ScreenEffects = cfg.Bind("Comfort", "ScreenEffects", ScreenEffectLevel.Reduced,
                "Full-screen effects from drinks, drugs and boosts (double vision, ripples, speed lines): Full as in the game, Reduced (a quarter), or Off. They move the whole view in a headset.");

            HeightOffset = cfg.Bind("Body", "HeightOffset", 0f, new ConfigDescription(
                "Extra eye height in meters. Your eyes are placed at Rania's eyes when the game starts and on recenter (R3), seated or standing; this shifts them.", new AcceptableValueRange<float>(-0.5f, 0.5f)));
            GrabWheel = cfg.Bind("Body", "GrabWheel", true,
                "In the cockpit, grab the rim of the HOVA's wheel with the strafe button (L1/R1 on PS VR2 Sense, grip elsewhere), one hand or two: the hand closes on the rim and the wheel turns under your hands, up to WheelLock. Let go and it springs back while the stick takes over again. R2 still accelerates.");
            WheelLock = cfg.Bind("Body", "WheelLock", 35f, new ConfigDescription(
                "How far you turn the HOVA's wheel for full steering, in degrees either way (20 to 90); the wheel stops there. 35 is about half of the game's own wheel (65), like power steering. The HOVA starts to turn after the same few degrees at any speed (6 at 35): the game cuts the steering to a quarter at top speed, and the wheel makes up the part of that which never turned it at all. In game: pause menu, VREVIVAL, Wheel sensitivity (25, 35, 50 or 65).",
                new AcceptableValueRange<float>(Input.WheelSteering.MinLock, Input.WheelSteering.MaxLock)));
            GrabLever = cfg.Bind("Body", "GrabLever", true,
                "In the cockpit, grab the lift lever right of the wheel with the strafe button (L1/R1 on PS VR2 Sense, grip elsewhere) and tilt it to climb or dive, the way the game's own lever moves when the right stick climbs or dives: back toward you to climb, forward to dive. The middle is neutral (a click as it drops in), with a bump at the stops. Let go and it springs back to the middle while the stick takes over again.");
            FingersFromTouch = cfg.Bind("Body", "FingersFromTouch", true,
                "PS VR2 Sense: fingers posed from the touch sensors (thumb on the buttons, index on the trigger, middle finger on the grip button, with the ring and little fingers going with it); the hand is open when nothing is touched. false shows the controller driver's own hand skeleton instead.");
            Hands = cfg.Bind("Body", "Hands", true,
                "Rania's hands, in voxels like the game's characters and in her model's colors (fingerless gloves, implant lights, or the sleeve of the outfit she wears). Fingers follow what the controller senses.");
            CockpitCharm = cfg.Bind("Body", "CockpitCharm", true,
                "A little voxel Camus hangs on a chain from the HOVA's ceiling, up and to the right of the wheel. It swings with the flight, and your hands push it, spin it, or hold it with the strafe button (L1 / R1 on PS VR2 Sense, the grip elsewhere).");
            CockpitCoffee = cfg.Bind("Body", "CockpitCoffee", true,
                "Rania's night-shift coffee in a cup holder on the HOVA's centre console, low on your right beside the lever. It sloshes with the flight: fly smoothly, because a hard brake or a crash spills it. While hot it steams a little, below your view. Take it with the strafe button (L1 / R1 on PS VR2 Sense, the grip elsewhere), bring it to your mouth and tip it to drink. Let go over the holder and it drops in; let go anywhere else, or throw it, and it lands in the cabin, where you can pick it up again (out of reach, it comes back to the holder). A full, hot cup each time you get back in after a few seconds on foot.");

            ScreenDistance = cfg.Bind("Interface", "MenuDistance", 2.0f, new ConfigDescription(
                "How far menus, shops and the map float, in meters. Only their depth: MenuAngle sets their size.", new AcceptableValueRange<float>(1f, 5f)));
            MenuAngle = cfg.Bind("Interface", "MenuAngle", 50f, new ConfigDescription(
                "The widest a menu, a shop or the map may be, in degrees of your view (30 to 80). The open menu and what you read beside it (who speaks, a merchant's offers and their description) are shown alone, enlarged when small, never wider than this.",
                new AcceptableValueRange<float>(30f, 80f)));
            HudDistance = cfg.Bind("Interface", "HudDistance", HudDistanceDefault, new ConfigDescription(
                "How far the in-game HUD floats, in meters. Only its depth: HudAngle and HudScale set its size. In the cockpit (HudOnWindshield) it sits just past the windscreen.", new AcceptableValueRange<float>(0.8f, 4f)));
            HudAngle = cfg.Bind("Interface", "HudAngle", 48f, new ConfigDescription(
                "The HUD's corners (minimap, jobs, dialogue, notifications) are brought within this width of your view, in degrees (30 to 80), and five eighths of it in height, so you read them without turning your eyes to the edges. A line of dialogue is at most three quarters of it wide (in the cockpit at most 34 degrees whatever HudScale, the room between the dashboard's screens).",
                new AcceptableValueRange<float>(30f, 80f)));
            HudScale = cfg.Bind("Interface", "HudScale", 1f, new ConfigDescription(
                "Size of the HUD's pieces (0.6 to 1.5). At 1, the game's small HUD text reads at about 1 degree: 0.04 degree per pixel of its 1080p layout for text, 0.035 for the minimap; the cockpit radar is 11 degrees across.",
                new AcceptableValueRange<float>(0.6f, 1.5f)));
            // replaced by MenuAngle
            Retire(cfg, "Interface", "MenuWidth");
            PointerHand = cfg.Bind("Interface", "PointerHand", Hand.Right,
                "Hand holding the laser pointer for menus.");
            ButtonPrompts = cfg.Bind("Interface", "ButtonPrompts", Input.PromptStyle.Auto,
                "Button icons in the game's prompts: Auto follows the controllers in hand (PlayStation symbols for PS VR2 Sense, A/B/X/Y for the others), or force PlayStation or Xbox.");
            MenuFocus = cfg.Bind("Interface", "MenuFocus", true,
                "The game draws most menus small, in a corner of the screen. With a menu open, its buttons and what you read beside them are shown alone in front of you, a little below eye level, enlarged when small. false shows the whole screen, MenuAngle wide.");
            MenuFollow = cfg.Bind("Interface", "MenuFollow", PanelFollow.Smooth,
                "How menus and the HUD follow your head: Smooth (always, gently, a little below your gaze), Lazy (only after a large turn) or Fixed (where they opened; R3 brings them back).");
            WristDisplay = cfg.Bind("Interface", "WristDisplay", true,
                "Look at the back of your left wrist, as at a watch: where you are, your jobs and their distance, the last lines said to you; on foot also the game's minimap, money and fuel, which then leave the view in front of you.");
            HudOnWindshield = cfg.Bind("Interface", "HudOnWindshield", true,
                "In the cockpit, the HUD is fixed to the HOVA's windscreen like a head-up display instead of following your head. Calls and lines then show there once, straight ahead just above the dashboard, and the game's own copy on the dashboard is hidden; false, or the game's HUD hidden, leaves them on the dashboard as the game has them.");
            CockpitRadar = cfg.Bind("Interface", "CockpitRadar", false,
                "In the cockpit, a round copy of the game's radar also shows low on the right of the HUD, on the windscreen, so you see it without turning your head to the dashboard's side screen. Off by default: the dashboard's screen already shows the minimap (it stays as it is either way).");
            SkipTitleScreen = cfg.Bind("Interface", "SkipTitleScreen", true,
                "In VR, the game's \"press any key to continue\" after it starts passes by itself and you land on the main menu. false keeps it (shown in front of you; any button passes it).");
            MenuSign = cfg.Bind("Interface", "MenuSign", true,
                "The main menu's neon sign gets a second line under the game's CLOUDPUNK: VREVIVAL, put together from pieces of the game's own neon letters (no image added). It lights up letter by letter each time the menu comes up; F11 in the menu lights it again. In VR the menu's list can sit over the end of the line while you look at CLOUDPUNK; it follows your gaze. false leaves the game's sign as it is. Flat and in VR.");
            MirrorAngle = cfg.Bind("Interface", "MirrorAngle", 60f, new ConfigDescription(
                "How wide a view of what is behind each of the HOVA's two rear-view screens shows, in degrees across (30 to 110). Narrower makes things behind you bigger, wider shows more around them. The game showed 113.",
                new AcceptableValueRange<float>(30f, 110f)));
            RearMirror = cfg.Bind("Interface", "RearMirror", false,
                "Off by default for now (try it with MirrorQuality = Low). A rear-view mirror in a voxel frame hangs from the HOVA's ceiling beside Camus, on his left, under the district sign; he knocks against it rather than swinging through it. It works like a real one: what you see in it depends on where your head is, and it looks out of the HOVA's back (which has no rear window) at what follows you. It adds about one small mirror's drawing per frame; MirrorQuality sets its sharpness and shadows, and Off darkens it too.");
            EyeSelect = cfg.Bind("Interface", "EyeSelect", true,
                "PS VR2 with the PSVR2 Toolkit: when the laser is not on a menu, the button you look at is selected; Cross confirms it.");

            Haptics = cfg.Bind("Feedback", "Haptics", true,
                "Controller vibration: the game's rumble, menu ticks, calls, notifications, lifts, collisions.");
            HapticsStrength = cfg.Bind("Feedback", "HapticsStrength", 1f, new ConfigDescription(
                "Scales every vibration from the mod (0 to 2).", new AcceptableValueRange<float>(0f, 2f)));
            EngineRumble = cfg.Bind("Feedback", "EngineRumble", true,
                "A light hum in both hands while the HOVA's engine pulls, stronger with throttle and speed.");
            AdaptiveTriggers = cfg.Bind("Feedback", "AdaptiveTriggers", true,
                "PS VR2 Sense with the PSVR2 Toolkit: a throttle spring on R2 and a firmer brake on L2 in the HOVA, a buzzing throttle during a boost, a stiff one without fuel. Menus free both triggers.");

            TrafficDensity = cfg.Bind("Performance", "TrafficDensity", 1f, new ConfigDescription(
                "Share of the cars and pedestrians the game's quality option allows (0.25 to 1). Lower it if the city stutters in the headset; cars change at the next load.",
                new AcceptableValueRange<float>(0.25f, 1f)));
            Mirrors = cfg.Bind("Performance", "MirrorQuality", MirrorQuality.Game,
                "The HOVA's mirrors. The game films one blurry 64 x 32 picture of the left rear and shows it on both small screens low on each side of the dashboard. Low: each screen shows its own side, sharp (256 pixels across), level with the horizon like the view, and the rear-view mirror (RearMirror) 512 across, one mirror drawn per frame in all (the rear-view one every second frame), without shadows. High: the screens 512 across, the mirror 768, all every frame, with shadows within 30 m. Game (default): the game's own blurry mirror on both screens (the rear-view mirror, if on, as Low). Off: everything dark, nothing drawn for them.");

            FreeCamEnabled = cfg.Bind("FreeCam", "Enabled", false,
                "A free camera for filming, in the flat game only (StartInVR = Never, or no headset), in the city or on the main menu: F6 turns it on and off. Fly it, drop keyframes and play them back as a smooth camera move, follow the HOVA, slow the world down. Keys in the README's Filming section.");
            FreeCamMoveSpeed = cfg.Bind("FreeCam", "MoveSpeed", 6f, new ConfigDescription(
                "How fast the free camera flies, in meters per second (Shift four times faster, Ctrl four times slower).", new AcceptableValueRange<float>(0.5f, 50f)));
            FreeCamLookSpeed = cfg.Bind("FreeCam", "LookSpeed", 1f, new ConfigDescription(
                "How fast the mouse turns the free camera.", new AcceptableValueRange<float>(0.1f, 5f)));
            FreeCamDuration = cfg.Bind("FreeCam", "Duration", 10f, new ConfigDescription(
                "How long a new path plays, in seconds of video (Home / End change it by a second while the camera is on).", new AcceptableValueRange<float>(1f, 600f)));
            FreeCamEase = cfg.Bind("FreeCam", "Ease", 0.3f, new ConfigDescription(
                "Share of a take spent speeding up at the start, and the same slowing down at the end (0 = constant speed, 0.5 = all ease).", new AcceptableValueRange<float>(0f, 0.5f)));
            FreeCamFollowSmoothing = cfg.Bind("FreeCam", "FollowSmoothing", 0.6f, new ConfigDescription(
                "Following the HOVA, how far behind its moves the camera eases, in seconds (0 = locked to it).", new AcceptableValueRange<float>(0f, 3f)));
            FreeCamFramerate = cfg.Bind("FreeCam", "Framerate", 60, new ConfigDescription(
                "Takes run on fixed steps at this frame rate: each frame moves the world and the camera by exactly one frame's time (1/60 s at 60), paced to the screen, so a path plays the same frames every time. 0 plays in real time.", new AcceptableValueRange<int>(0, 240)));
            FreeCamSaveFrames = cfg.Bind("FreeCam", "SaveFrames", Film.FrameFormat.Off,
                "Off, Png or Jpg: every frame of a take saved to BepInEx\\CloudpunkVR-takes, for a clean video however slow the game draws (with Framerate on). Heavy on disk at 4K: about 10 MB a frame in Png.");
            FreeCamKeepOcclusion = cfg.Bind("FreeCam", "KeepOcclusionCulling", false,
                "Keep the game's occlusion culling on in the free camera. Off by default: it works from the frames before, so a fast camera move can show buildings popping in, and two takes can come out different. Off costs a bit more to draw.");

            SpectatorEnabled = cfg.Bind("Spectator", "Enabled", false,
                "For recording (OBS): the game's window on the monitor shows a steady view of what you see in the headset instead of the headset's mirror, which shakes with every move of your head. It is smoothed and kept level with the horizon, and cut from the headset's own picture, so it costs next to nothing. Everything the headset shows is in it, the comfort vignette too (FlightVignette = false while you film). Read at start: true starts with the view on. The Key switches it on and off while you play, whatever this says.");
            SpectatorFormat = cfg.Bind("Spectator", "Format", CloudpunkVR.SpectatorFormat.Landscape,
                "Landscape: 16:9, 1920 x 1080 in a 1920 x 1080 window. Portrait: 9:16 upright in the window (1080 x 1920 needs a window at least 1920 tall). PortraitTurned: 9:16 on its side, filling a 16:9 window, so a 1920 x 1080 window gives 1080 x 1920; turn it back in OBS (Transform, Rotate 90 degrees clockwise).");
            SpectatorFov = cfg.Bind("Spectator", "FieldOfView", 75f, new ConfigDescription(
                "Degrees across the picture's long side (40 to 110). The view is cut from the headset's picture, so it can't be wider than that picture (the log says how wide it can go); the narrower, the more room the smoothing has.",
                new AcceptableValueRange<float>(40f, 110f)));
            SpectatorSmoothing = cfg.Bind("Spectator", "Smoothing", 0.5f, new ConfigDescription(
                "How much the view smooths your head's moves (0 to 1). 0 follows your head as it is (the tilt of your head is still taken out), 0.5 is steady like a camera on a gimbal, 1 is slow and floaty. Quick turns are followed anyway when the view reaches the edge of the headset's picture.",
                new AcceptableValueRange<float>(0f, 1f)));
            SpectatorTilt = cfg.Bind("Spectator", "Tilt", 8f, new ConfigDescription(
                "Degrees the view looks below where you look (-10 to 20), so your hands on the wheel are in the picture while you watch the road.",
                new AcceptableValueRange<float>(-10f, 20f)));
            SpectatorKey = cfg.Bind("Spectator", "Key", "F8",
                "Key that switches the spectator view on and off in VR, in the game's window or OBS's (pressed in another window, or with Alt or Ctrl held, it does nothing; don't give it to an OBS hotkey too). A Unity key name: F1 to F15, a letter, a digit, Insert, Home, PageUp...; empty for none. Each time it switches the view on, the [Spectator] lines are read again from this file: change them, save, press it twice. Only these lines: the rest of the file is edited with the game closed.");

            CleanFrame = cfg.Bind("Filming", "CleanFrame", false,
                "Clean frame, for filming: the picture without any interface. The game's HUD (minimap, money and fuel, jobs, the cockpit's district sign, its prompts like PARK VEHICLE), and the mod's own (the windscreen HUD, the wrist display, the radar, the laser away from a menu, the comfort vignette, the passage card, the glow on what you look at) all go; Rania's hands, the wheel, the lever, the coffee, Camus, the cockpit and its mirrors stay. In VR (the headset, so the spectator view too) and in the flat game. Menus, the map and the inventory still open and show as usual. Read at start: true starts with it on. CleanFrameKey switches it on and off while you play, whatever this says.");
            CleanFrameKeepDialogue = cfg.Bind("Filming", "CleanFrameKeepDialogue", true,
                "With the clean frame on, keep the game's dialogue line (portrait, name and words, and a call's box) where it shows without it: on the windscreen in the cockpit, as the game's subtitle on foot. false hides it too.");
            CleanFrameKey = cfg.Bind("Filming", "CleanFrameKey", "F2",
                "Key that switches the clean frame on and off, in VR and in the flat game, in the game's window or OBS's (pressed in another window, or with Alt or Ctrl held, it does nothing; don't give it to an OBS hotkey too). On the main menu it hides the menu itself for a title shot, and brings it back. A Unity key name as for [Spectator] Key; empty for none. Each time it switches the clean frame on, CleanFrameKeepDialogue and this line are read again from the file.");

            ConfigRevision = cfg.Bind("Internal", "ConfigRevision", 0,
                "Which one-time changes of defaults this file has had (the mod sets it; leave it as it is).");
            Migrate();
        }

        // ---- One-time changes of defaults.

        // a new default only reaches new files, so a revision moves values still at the old default
        private const int CurrentRevision = 2;
        private const float HudDistanceDefault = 2.0f;
        private const float VignetteStrengthDefault = 0.4f;
        public static ConfigEntry<int> ConfigRevision;

        private static void Migrate()
        {
            int from = ConfigRevision.Value;
            if (from >= CurrentRevision) return;
            var changes = new System.Collections.Generic.List<string>();
            if (from < 1)
            {
                // 0.2.0: HUD a bit further, windscreen radar off (the dashboard has a minimap)
                if (System.Math.Abs(HudDistance.Value - 1.6f) < 0.001f) { HudDistance.Value = HudDistanceDefault; changes.Add($"HudDistance 1.6 -> {HudDistanceDefault:0.0}"); }
                if (CockpitRadar.Value) { CockpitRadar.Value = false; changes.Add("CockpitRadar true -> false"); }
            }
            if (from < 2)
            {
                // 0.2.0: lighter vignette
                if (System.Math.Abs(VignetteStrength.Value - 0.55f) < 0.001f) { VignetteStrength.Value = VignetteStrengthDefault; changes.Add($"VignetteStrength 0.55 -> {VignetteStrengthDefault:0.00}"); }
            }
            ConfigRevision.Value = CurrentRevision;
            Plugin.L.LogInfo($"Settings revision {from} -> {CurrentRevision}: " +
                             (changes.Count > 0 ? string.Join(", ", changes) + " (the new defaults; values you had changed are kept)." : "nothing to change."));
        }

        /// <summary>Settings removed at this launch, with their old values.</summary>
        public static string Retired { get; private set; } = "none";

        /// <summary>
        /// Drops an old setting from the file. BepInEx keeps unknown lines, so bind and remove it: the next save
        /// (any later Bind) leaves it out. No direct write: BepInEx's own copes with a read-only file.
        /// </summary>
        private static void Retire(ConfigFile cfg, string section, string key)
        {
            var definition = new ConfigDefinition(section, key);
            bool saveOnSet = cfg.SaveOnConfigSet;
            cfg.SaveOnConfigSet = false; // don't write it back on bind
            var old = cfg.Bind(definition, "");
            cfg.Remove(definition);
            cfg.SaveOnConfigSet = saveOnSet;
            if (old.Value.Length == 0) return;
            string entry = $"[{section}] {key} = {old.Value}";
            Retired = Retired == "none" ? entry : Retired + ", " + entry;
            Plugin.L.LogInfo($"Retired setting {entry}: it no longer exists and leaves the file.");
        }

        /// <summary>
        /// Runtime set + one save (VREVIVAL menu). BepInEx writes the whole file from memory, so it's reloaded first to
        /// keep hand edits, with save-on-set off meanwhile. Returns "saved" or why not.
        /// </summary>
        public static string SetAndSave(System.Action set)
        {
            var file = WheelLock.ConfigFile;
            bool saveOnSet = file.SaveOnConfigSet;
            string reread = "";
            try
            {
                file.SaveOnConfigSet = false;
                try { file.Reload(); }
                catch (System.Exception e) { reread = $", the file not read again first ({e.Message})"; }
                set();
                file.Save();
                return "saved" + reread;
            }
            catch (System.Exception e)
            {
                Plugin.L.LogWarning("Settings could not be saved: " + e.Message);
                return "NOT SAVED (" + e.Message + ")";
            }
            finally { file.SaveOnConfigSet = saveOnSet; }
        }

        /// <summary>The file on disk as "Section.Key" to value (empty if unreadable).</summary>
        public static System.Collections.Generic.Dictionary<string, string> FileValues()
        {
            var values = new System.Collections.Generic.Dictionary<string, string>();
            try
            {
                string section = "";
                foreach (var raw in System.IO.File.ReadAllLines(WheelLock.ConfigFile.ConfigFilePath))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]")) { section = line.Substring(1, line.Length - 2); continue; }
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[section + "." + line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch (System.Exception) { /* nothing read: every line counts as missing */ }
            return values;
        }

        /// <summary>Re-reads one section at runtime (filming keys). Returns what changed, or null if unreadable.</summary>
        public static string ReadSectionAgain(string section, System.Func<string, ConfigEntryBase> entryFor)
        {
            // no save on set: it would write the whole file over edits made to other sections
            var file = SpectatorKey.ConfigFile;
            bool saveOnSet = file.SaveOnConfigSet;
            file.SaveOnConfigSet = false;
            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "CloudpunkVR.cfg");
                if (!System.IO.File.Exists(path)) return "no settings file";
                bool inSection = false;
                string changes = "";
                foreach (var raw in System.IO.File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[")) { inSection = line == "[" + section + "]"; continue; }
                    if (!inSection || line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string name = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    var entry = entryFor(name);
                    if (entry == null) continue;
                    string before = entry.GetSerializedValue();
                    entry.SetSerializedValue(value);
                    string after = entry.GetSerializedValue();
                    if (after != before) changes += (changes.Length > 0 ? ", " : "") + $"{name} {before} -> {after}";
                }
                return changes.Length > 0 ? changes : "nothing changed";
            }
            catch (System.Exception e)
            {
                Plugin.L.LogWarning($"[{section}] could not be read again from the settings file: {e.Message}");
                return null;
            }
            finally { file.SaveOnConfigSet = saveOnSet; }
        }
    }
}
