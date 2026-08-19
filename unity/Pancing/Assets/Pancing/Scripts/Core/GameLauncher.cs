using UnityEngine;

namespace Pancing.Core
{
    /// <summary>
    /// Spawns Bootstrap the moment the runtime is alive, before any scene loads.
    ///
    /// This is what lets the project ship with a single EMPTY scene: there is no
    /// GameObject to place, no component to attach, and therefore nothing about
    /// the scene that can be wrong. It also means pressing Play from any scene in
    /// the editor gives you the real game rather than an empty grey viewport.
    /// </summary>
    public static class GameLauncher
    {
        private const string RootName = "PancingRoot";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Launch()
        {
            // A domain reload in the editor re-runs this; a live root means we are
            // already up and a second one would double-simulate everything.
            if (GameObject.Find(RootName) != null) return;

            Game.Reset();
            var root = new GameObject(RootName);
            Object.DontDestroyOnLoad(root);
            root.AddComponent<Bootstrap>();
        }
    }
}
