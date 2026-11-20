using System;
using CloudpunkVR.Input;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CloudpunkVR.UI
{
    /// <summary>
    /// Option rows (resolution, volume...) want a d-pad. Here the sticks step them (hold to repeat) and so does the
    /// laser: left half of the value goes back, right half forward. Goes through the row's own ChangeValue.
    /// </summary>
    internal static class MenuValues
    {
        private static int heldDir;
        private static float nextRepeat;
        private static bool warned;

        public static OptionsButton Selected()
        {
            var es = EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            return go != null ? go.GetComponentInParent<OptionsButton>() : null;
        }

        /// <summary>Stick x (either stick) while a menu is open.</summary>
        public static void Update(float x)
        {
            int dir = x > 0.6f ? 1 : x < -0.6f ? -1 : 0;
            if (dir == 0) { heldDir = 0; return; }
            var row = Selected();
            if (row == null) { heldDir = 0; return; }
            if (dir != heldDir)
            {
                heldDir = dir;
                nextRepeat = Time.unscaledTime + 0.45f;
                Step(row, dir);
            }
            else if (Time.unscaledTime >= nextRepeat)
            {
                nextRepeat = Time.unscaledTime + 0.16f;
                Step(row, dir);
            }
        }

        /// <summary>Laser click on an option row. False if the target isn't one.</summary>
        public static bool Click(GameObject target, Vector2 pixel)
        {
            var row = target != null ? target.GetComponentInParent<OptionsButton>() : null;
            if (row == null) return false;
            int dir = 1;
            var cam = VR.CameraDriver.UiCamera;
            var value = row.value;
            if (cam != null && value != null)
            {
                Vector3 centre = value.rectTransform.TransformPoint(value.rectTransform.rect.center);
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(cam, centre);
                if (pixel.x < screen.x) dir = -1;
            }
            Step(row, dir);
            return true;
        }

        private static void Step(OptionsButton row, int dir)
        {
            try
            {
                row.ChangeValue(dir);
                Haptics.UiTick(0.012f, 0.3f, 170f);
                Plugin.L.LogInfo($"Option '{row.name}': {(row.value != null ? row.value.text : "?")}");
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; Plugin.L.LogWarning("Option row did not change: " + e.Message); }
            }
        }
    }
}
