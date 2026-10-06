using System;
using System.IO;
using UnityEngine;

namespace Pancing.Core
{
    /// <summary>
    /// Command-line switches for headless screenshot checks of a built player:
    ///
    ///   Pancing.exe -spot sungai -shot C:\tmp\a.png -shotdelay 8
    ///
    /// -spot overrides the saved spot, -shot writes one screenshot then quits.
    /// Any of them makes the run read-only: the player's save is never written,
    /// so a test run cannot move someone's angler to another lake.
    /// </summary>
    public static class DevArgs
    {
        public static readonly string Spot = Get("-spot");
        public static readonly string ShotPath = Get("-shot");
        public static readonly float ShotDelay = float.TryParse(Get("-shotdelay"), out var d) ? d : 7f;
        public static readonly float Hour = float.TryParse(Get("-hour"), out var h) ? h : -1f;
        public static readonly bool AutoFish = Has("-autofish");
        public static readonly string Weather = Get("-weather");

        /// <summary>With -autofish, -shot fires once a fight has run this long
        /// (falls back to -shotdelay x 4 if nothing bites).</summary>
        public static readonly float ShotFight = float.TryParse(Get("-shotfight"), out var sf) ? sf : -1f;
        public static float FightSeconds;
        /// <summary>-shotcatch: shoot 1.2 s after a fish is landed (the catch card).</summary>
        public static readonly bool ShotCatch = Has("-shotcatch");
        public static float LandedAt = -1f;

        public static bool Active => Spot != null || ShotPath != null || Hour >= 0f || AutoFish || Weather != null;

        private static string Get(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static bool Has(string key) =>
            Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>Called every frame by Bootstrap; takes the shot once, then quits.</summary>
        public static void Tick(MonoBehaviour host)
        {
            if (ShotPath == null || _shotTaken) return;
            bool fightReady = (ShotFight >= 0f && FightSeconds >= ShotFight)
                           || (ShotCatch && LandedAt > 0f && Time.time > LandedAt + 1.2f);
            float wait = ShotFight >= 0f || ShotCatch ? ShotDelay * 4f : ShotDelay;
            if (!fightReady && Time.timeSinceLevelLoad < wait) return;
            _shotTaken = true;
            host.StartCoroutine(Capture());
        }

        // ReadPixels after end-of-frame rather than ScreenCapture: that module is
        // not in this project, and this also picks up the overlay UI.
        private static System.Collections.IEnumerator Capture()
        {
            yield return new WaitForEndOfFrame();
            var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
            tex.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ShotPath)));
            File.WriteAllBytes(ShotPath, tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            _quitAt = Time.realtimeSinceStartup + 0.5f;
        }

        public static void TickQuit()
        {
            if (_quitAt > 0f && Time.realtimeSinceStartup > _quitAt) Application.Quit();
        }

        private static bool _shotTaken;
        private static float _quitAt = -1f;
    }
}
