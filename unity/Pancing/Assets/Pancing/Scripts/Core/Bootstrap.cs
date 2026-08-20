using UnityEngine;
using Pancing.Controls;
using Pancing.Render;
using Pancing.Sim;
using Pancing.UI;

namespace Pancing.Core
{
    /// <summary>
    /// Builds the entire game from code — data, simulation, world, camera, input
    /// and UI — so the project runs with NO manual scene setup. Press Play, or
    /// build with an empty scene, and you get a game.
    ///
    /// That is not a stunt. It means the scene file is never a merge conflict,
    /// never drifts out of step with the code, and never has to be repaired by
    /// hand after a rename. The only thing a build needs is one empty scene, and
    /// BuildTool creates that for you.
    /// </summary>
    public sealed class Bootstrap : MonoBehaviour
    {
        /// <summary>
        /// The simulation runs at a locked 120 Hz. The tension solver is only
        /// meaningful at a fixed dt — it bisects for a force that depends on how far
        /// the fish moved this step — so rendering at 30 fps must not change how a
        /// fish fights. Rendering runs free and reads the latest state.
        /// </summary>
        private const double SimHz = 120.0;
        private const double SimStep = 1.0 / SimHz;
        /// <summary>Never simulate more than this much wall time in one frame; a
        /// stall (alt-tab, a GC spike) must not fast-forward the fight.</summary>
        private const double MaxCatchUp = 0.25;

        private const float AutosaveInterval = 30f;

        private double _accumulator;
        private float _autosaveTimer;

        private InputService _input;
        private CameraRig _camera;
        private WaterSurface _water;
        private EnvironmentBuilder _env;
        private TackleView _tackle;
        private AnglerView _angler;
        private AimArrow _aim;
        private Hud _hud;
        private PanelSystem _panels;

        private void Start()
        {
            Application.targetFrameRate = 60;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            if (!LoadData()) return;
            BuildSimulation();
            BuildScene();
            WireEvents();

            Debug.Log($"[Pancing] Ready — {Game.Species.All.Count} species, " +
                      $"{Game.Gear.All.Count} items, {Game.Spots.All.Count} spots.");
        }

        /* --- data ------------------------------------------------------------- */

        private bool LoadData()
        {
            // Resources rather than StreamingAssets: it loads synchronously on every
            // platform, including Android, where StreamingAssets lives inside the APK
            // and needs UnityWebRequest to read.
            string species = LoadText("species");
            string gear = LoadText("gear");
            string spots = LoadText("spots");
            if (species == null || gear == null || spots == null) return false;

            try
            {
                Game.Species = SpeciesDb.Load(species);
                Game.Gear = GearDb.Load(gear);
                Game.Spots = SpotDb.Load(spots);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Pancing] Data failed to parse — aborting startup. {e.Message}");
                return false;
            }

            if (Game.Species.All.Count == 0 || Game.Spots.All.Count == 0)
            {
                Debug.LogError("[Pancing] Data loaded but empty — check Resources/*.json.");
                return false;
            }
            return true;
        }

        private static string LoadText(string name)
        {
            var asset = Resources.Load<TextAsset>(name);
            if (asset == null)
            {
                Debug.LogError($"[Pancing] Missing Resources/{name}.json. " +
                               "Run: node shared/tools/export-data.mjs");
                return null;
            }
            return asset.text;
        }

        /* --- simulation --------------------------------------------------------- */

        private void BuildSimulation()
        {
            Game.Bus = new EventBus { Log = Debug.LogError };
            // Seeded from the clock so each session differs, but seeded from ONE
            // number so a session can be reproduced from it if a bug needs chasing.
            uint seed = (uint)System.DateTime.Now.Ticks;
            Game.Rng = new Rng(seed);

            Game.State = new PlayerState(Game.Bus, Game.Gear, Game.Species, Game.Spots);
            LoadSave();

            Game.World = new World(Game.Rng.Fork("world"), Game.Bus, Game.Spots, Game.State.Spot);
            Game.Fishing = new FishingGame(Game.Rng, Game.Bus, Game.State, Game.World, Game.Species);
        }

        private void LoadSave()
        {
            string json = PlayerPrefs.GetString(PlayerState.SaveKey, null);
            if (string.IsNullOrEmpty(json)) return;
            if (!Game.State.FromJson(json))
            {
                Debug.LogWarning($"[Pancing] Save unreadable, starting fresh. {Game.State.LoadError}");
            }
        }

        private void Save()
        {
            try
            {
                PlayerPrefs.SetString(PlayerState.SaveKey, Game.State.ToJson());
                PlayerPrefs.Save();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Pancing] Save failed: {e.Message}");
            }
        }

        /* --- scene -------------------------------------------------------------- */

        /// <summary>
        /// Take over the scene before building anything into it.
        ///
        /// The game supplies its own camera, sun and audio listener, and a build
        /// starts from the empty scene BuildTool creates — so this never mattered
        /// there. But pressing Play from Unity's DEFAULT scene means a Main Camera
        /// and a Directional Light are already present, and then:
        ///
        ///   - two AudioListeners warn every single frame, which is where the
        ///     "999+" in the console comes from;
        ///   - two cameras at the same depth render in an undefined order, so the
        ///     one that actually follows the fight can silently lose, and the game
        ///     looks like it is ignoring the camera code entirely;
        ///   - a second directional light doubles the sun and flattens the water.
        ///
        /// Runs before our own objects exist, so anything it finds is foreign by
        /// definition. Disabled rather than destroyed: it is the user's scene, and
        /// they get it back intact when they leave Play mode.
        /// </summary>
        private void ClaimScene()
        {
            foreach (var cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
            {
                if (cam.transform.IsChildOf(transform)) continue;
                cam.gameObject.SetActive(false);
            }

            foreach (var listener in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
            {
                if (listener.transform.IsChildOf(transform)) continue;
                listener.enabled = false;
            }

            foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.transform.IsChildOf(transform)) continue;
                if (light.type == LightType.Directional) light.gameObject.SetActive(false);
            }
        }

        private void BuildScene()
        {
            ClaimScene();

            var spot = Game.State.Spot;
            // Phones get the cheaper water grid and no planar reflection. The
            // difference is one pass and about 12 000 vertices, which is exactly the
            // budget a five-year-old Android phone does not have.
            bool highQuality = !Application.isMobilePlatform
                            && Game.State.Quality != "low";

            _input = InputService.Create(transform);
            _camera = CameraRig.Create(transform);
            _env = EnvironmentBuilder.Create(transform, spot);
            _water = WaterSurface.Create(transform, spot, highQuality);
            _tackle = TackleView.Create(transform);
            _angler = AnglerView.Create(transform);
            _aim = AimArrow.Create(transform);
            _hud = Hud.Create(transform, _input);
            _panels = PanelSystem.Create(transform, _input, _hud);

            QualitySettings.shadowDistance = highQuality ? 45f : 0f;
        }

        /* --- events -------------------------------------------------------------- */

        private void WireEvents()
        {
            var bus = Game.Bus;

            bus.On<FishingGame.Fx>(EV.Ripple, fx =>
                _water?.Ripple(ToUnity(fx.Pos), (float)fx.Strength));

            bus.On<FishingGame.Fx>(EV.Splash, fx =>
            {
                _water?.Ripple(ToUnity(fx.Pos), (float)fx.Strength);
                _camera?.Shake((float)fx.Strength * 0.35f);
            });

            bus.On<FishingGame.Toast>(EV.Toast, t => _hud?.Toast(t.Text, t.Kind));

            bus.On<CatchCard>(EV.Landed, card =>
            {
                _hud?.ShowCatch(card);
                Save();
            });

            bus.On(EV.LineSnap, _ => _camera?.Shake(0.9f));
            bus.On(EV.HookLost, _ => _camera?.Shake(0.5f));
            bus.On<HookedFish.Telemetry>(EV.FishJump, _ => _camera?.Shake(0.4f));

            bus.On<Spot>(EV.SpotChange, spot =>
            {
                Game.World.SetSpot(spot);
                _water?.SetSpot(spot);
                // The bank, the trees and the bathymetry are all spot-specific, so a
                // travel is a rebuild rather than a re-tint.
                if (_env != null) Destroy(_env.gameObject);
                _env = EnvironmentBuilder.Create(transform, spot);
                Save();
            });

            bus.On(EV.GearBuy, _ => Save());
            bus.On(EV.GearEquip, _ => Save());

            bus.On<Quest>(EV.QuestDone, q => _hud?.Toast($"Misi selesai: {q.Name}", "good"));
            bus.On<PlayerState.LevelUpInfo>(EV.LevelUp, info =>
                _hud?.Toast($"Naik ke Tahap {info.Level}!", "good"));
        }

        private static Vector3 ToUnity(Vec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);

        /* --- the loop -------------------------------------------------------------- */

        private void Update()
        {
            if (!Game.Ready) return;

            float dt = Time.deltaTime;

            // A modal pauses the world. Without this the clock keeps running and a
            // half-charged cast auto-fires into the lake while the player is reading
            // the shop — the charge meter does not care that a window is over it.
            bool paused = _panels != null && _panels.IsOpen;
            if (paused)
            {
                _wasCastHeld = false;
                _accumulator = 0;
            }
            else
            {
                HandleInput();
            }

            // Fixed-step the simulation, free-run the renderer.
            if (!paused) _accumulator += Mathf.Min(dt, (float)MaxCatchUp);
            var gameInput = _input.ToGameInput();
            int steps = 0;
            while (_accumulator >= SimStep && steps < 32)
            {
                Game.World.Update(SimStep);
                Game.Fishing.Update(SimStep, gameInput);
                _accumulator -= SimStep;
                steps++;
            }

            var tm = Game.Fishing.GetTelemetry();

            _water?.SetConditions((float)Game.World.Wind, (float)Game.World.Chop, (float)Game.World.Light);
            _env?.ApplyConditions(Game.World, _camera != null ? _camera.Camera : null);
            _tackle?.Apply(tm, Game.Fishing, _water);
            _angler?.Apply(tm, _input.AimYaw, _tackle != null ? _tackle.RodDir : Vector3.forward, dt);
            _aim?.Apply(tm, Game.Fishing, _input.AimYaw, _water);
            _camera?.Apply(tm, _input.AimYaw, _tackle != null ? _tackle.LurePos : Vector3.zero, dt);
            _hud?.Apply(tm, Game.World, Game.State, dt);

            _autosaveTimer += dt;
            if (_autosaveTimer >= AutosaveInterval) { _autosaveTimer = 0f; Save(); }
        }

        private bool _wasCastHeld;

        private void HandleInput()
        {
            var game = Game.Fishing;
            game.Aim(_input.AimYaw);

            // Press begins the charge, release throws. Doing it on the edges rather
            // than on the held state is what makes the release timing the player's
            // decision instead of a poll artefact.
            if (_input.CastHeld && !_wasCastHeld) game.BeginCast();
            if (!_input.CastHeld && _wasCastHeld) game.ReleaseCast();
            _wasCastHeld = _input.CastHeld;

            if (_input.StrikePressed) game.Strike();

            if (UnityEngine.Input.GetKeyDown(KeyCode.X)) game.ReelInHard();
        }

        private void OnApplicationPause(bool paused) { if (paused && Game.Ready) Save(); }
        private void OnApplicationQuit() { if (Game.Ready) Save(); }
    }
}
