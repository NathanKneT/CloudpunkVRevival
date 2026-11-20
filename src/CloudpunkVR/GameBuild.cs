using System;
using System.IO;
using System.Text.RegularExpressions;

namespace CloudpunkVR
{
    /// <summary>The Steam build the mod was tested on, to warn after game updates.</summary>
    internal static class GameBuild
    {
        public const string TestedBuildId = "8245592";
        public const string TestedUnity = "2018.4.29f1";

        public static string SteamBuildId { get; private set; }

        public static string Describe()
        {
            SteamBuildId = ReadBuildId();
            if (SteamBuildId == null) return $"Steam build unknown, tested on {TestedBuildId}";
            return SteamBuildId == TestedBuildId
                ? $"Steam build {SteamBuildId}, tested"
                : $"Steam build {SteamBuildId}, NOT the tested build {TestedBuildId}: report problems with this number";
        }

        private static string ReadBuildId()
        {
            try
            {
                // <library>/steamapps/common/Cloudpunk -> <library>/steamapps/appmanifest_746850.acf
                string game = BepInEx.Paths.GameRootPath;
                string manifest = Path.GetFullPath(Path.Combine(game, "..", "..", "appmanifest_746850.acf"));
                if (!File.Exists(manifest)) return null;
                var m = Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"(\\d+)\"");
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
