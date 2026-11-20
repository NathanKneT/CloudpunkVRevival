using System;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace CloudpunkVR
{
    /// <summary>Logs a short VR status on scene change. Full dump (cameras, canvases, input) on F9 or Diagnostics=true.</summary>
    public class Diagnostics : MonoBehaviour
    {
        public Diagnostics(IntPtr ptr) : base(ptr) { }

        private string lastScenes = "";

        private void Update()
        {
            string scenes = SceneList();
            if (scenes != lastScenes)
            {
                lastScenes = scenes;
                Plugin.L.LogInfo("Scenes: " + scenes);
                LogVrStatus();
                if (VRConfig.Diagnostics.Value) Dump();
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.F9)) Dump();
            Input.VirtualGamepad.Attach();
            UI.MenuSign.Update(); // flat and in VR
            Film.CleanFrame.Update(); // flat and in VR: its key
            Film.WindowSize.Update(); // logs the window size when it changes
#if DEV
            DevCommands.Poll();
#endif
        }

        // after game Updates, before canvases build and cameras draw
        private void LateUpdate() => Film.CleanFrame.LateUpdate();

        private static string SceneList()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                sb.Append(s.name).Append(s.isLoaded ? "" : "(loading)").Append(' ');
            }
            return sb.ToString().Trim();
        }

        public static void LogVrStatus()
        {
            try
            {
                Plugin.L.LogInfo($"XR: enabled={XRSettings.enabled} device='{XRSettings.loadedDeviceName}' active={XRSettings.isDeviceActive} " +
                                 $"present={XRDevice.isPresent} model='{XRDevice.model}' eye={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} " +
                                 $"refresh={XRDevice.refreshRate} stereo={XRSettings.stereoRenderingMode} supported=[{string.Join(",", XRSettings.supportedDevices)}]");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("XR status unavailable: " + e.Message);
            }
        }

        public static void Dump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== Cloudpunk VR diagnostics =====");
            sb.AppendLine("Scenes: " + SceneList());
            try { DumpCameras(sb); } catch (Exception e) { sb.AppendLine("cameras: " + e); }
            try { DumpCanvases(sb); } catch (Exception e) { sb.AppendLine("canvases: " + e); }
            try { DumpInput(sb); } catch (Exception e) { sb.AppendLine("input: " + e); }
            try { DumpGlobal(sb); } catch (Exception e) { sb.AppendLine("global: " + e); }
            Plugin.L.LogInfo(sb.ToString());
            LogVrStatus();
        }

        private static void DumpCameras(StringBuilder sb)
        {
            sb.AppendLine("-- Cameras");
            foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (!cam.gameObject.scene.IsValid()) continue;
                sb.AppendLine($"  {Path(cam.transform)} active={cam.gameObject.activeInHierarchy} enabled={cam.enabled} depth={cam.depth} clear={cam.clearFlags} " +
                              $"mask=0x{cam.cullingMask:X8} rt={(cam.targetTexture ? cam.targetTexture.name + " " + cam.targetTexture.width + "x" + cam.targetTexture.height : "screen")} " +
                              $"eye={cam.stereoTargetEye} fov={cam.fieldOfView:F1} near={cam.nearClipPlane} far={cam.farClipPlane} occl={cam.useOcclusionCulling} " +
                              $"hdr={cam.allowHDR} msaa={cam.allowMSAA} pos={cam.transform.position} rot={cam.transform.eulerAngles}");
                sb.Append("     components: ");
                foreach (var c in cam.GetComponents<Component>())
                    if (c != null) sb.Append(c.GetIl2CppType().Name).Append(' ');
                sb.AppendLine();
            }
        }

        private static void DumpCanvases(StringBuilder sb)
        {
            sb.AppendLine("-- Root canvases");
            foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (!c.gameObject.scene.IsValid() || !c.isRootCanvas) continue;
                var rt = c.GetComponent<RectTransform>();
                sb.AppendLine($"  {Path(c.transform)} active={c.gameObject.activeInHierarchy} mode={c.renderMode} cam={(c.worldCamera ? c.worldCamera.name : "-")} " +
                              $"order={c.sortingOrder} plane={c.planeDistance} size={(rt ? rt.sizeDelta.ToString() : "?")} layer={LayerMask.LayerToName(c.gameObject.layer)}");
            }
        }

        private static void DumpInput(StringBuilder sb)
        {
            sb.AppendLine("-- InControl devices");
            if (!InControl.InputManager.IsSetup) { sb.AppendLine("  (InControl not set up yet)"); return; }
            var devices = InControl.InputManager.Devices;
            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                sb.AppendLine($"  {d.Name} style={d.DeviceStyle} class={d.DeviceClass} active={d.IsActive}");
            }
            var gi = GameInput.Instance;
            if (gi == null || gi.playerActions == null) return;
            sb.AppendLine("-- Game actions (InControl bindings)");
            var actions = gi.playerActions.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                sb.Append($"  {a.Name}:");
                var b = a.Bindings;
                for (int j = 0; j < b.Count; j++) sb.Append($" [{b[j].BindingSourceType}:{b[j].Name}]");
                sb.AppendLine();
            }
        }

        private static void DumpGlobal(StringBuilder sb)
        {
            var g = Global.Instance;
            if (g == null) { sb.AppendLine("-- Global: none yet"); return; }
            sb.AppendLine($"-- Global: mainCamera={Name(g.mainCamera)} uiCamera={Name(g.uiCamera)} menuCamera={(g.menuCamera ? Path(g.menuCamera) : "-")} " +
                          $"inVehicle={Global.IsInVehicle} cockpit={Global.IsInVehicleCockpit} inMenu={g.IsInMenu()} blackScreen={Global.BlackScreenIsOn}");
            if (g.vehicleInterior != null)
            {
                var vi = g.vehicleInterior;
                sb.AppendLine($"   vehicleInterior: cameraCockpit={Name(vi.cameraCockpit)} interiorLocal={(vi.interiorLocal ? Path(vi.interiorLocal) : "-")} " +
                              $"interiorCameraTransform={(vi.interiorCameraTransform ? Path(vi.interiorCameraTransform) : "-")}");
            }
        }

        private static string Name(Camera c) => c ? Path(c.transform) : "-";

        public static string Path(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }
    }
}
