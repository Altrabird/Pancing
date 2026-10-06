using System.Collections.Generic;
using UnityEngine;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.Audio
{
    /// <summary>
    /// Every sound in the game. Clips are synthesised offline by art/make_audio.py
    /// into Resources/Audio — no samples, no licences.
    ///
    /// Three layers:
    ///   - a bed that fits the place and the hour: kampung birds and the merbok
    ///     by the pond, the cicada swell in the jungle, crickets and katak at
    ///     night — cross-faded, never cut;
    ///   - water and weather on top: the river's rush at Sungai Berbatu, rain
    ///     scaled by the world's rain amount;
    ///   - the fishing itself, driven by the simulation: the cast whoosh, the
    ///     plop, a tick on every nibble, the bloop when the float goes under, the
    ///     reel clicking at the speed the reel actually delivers, the clutch
    ///     screaming while it slips, and a bonang flourish for a fish in the net.
    ///
    /// The bite cue matters most: the hookset window can be a third of a second,
    /// and until now the only alert was visual.
    /// </summary>
    public sealed class AudioService : MonoBehaviour
    {
        private readonly Dictionary<string, AudioClip> _clips = new Dictionary<string, AudioClip>();
        private AudioSource _bedA, _bedB, _water, _rain, _drag;
        private readonly AudioSource[] _fx = new AudioSource[6];
        private int _fxNext;
        private string _bedClip;
        private float _bedMix = 1f;   // 1 = A audible
        private float _reelPhase;
        private string _spotId = "";

        public static AudioService Create(Transform parent)
        {
            var go = new GameObject("Audio");
            go.transform.SetParent(parent, false);
            var a = go.AddComponent<AudioService>();
            a.Build();
            return a;
        }

        private void Build()
        {
            foreach (var c in Resources.LoadAll<AudioClip>("Audio")) _clips[c.name] = c;

            _bedA = Loop("BedA");
            _bedB = Loop("BedB");
            _water = Loop("Water");
            _rain = Loop("Rain", "amb_hujan");
            _drag = Loop("Drag", "drag");
            for (int i = 0; i < _fx.Length; i++)
            {
                _fx[i] = gameObject.AddComponent<AudioSource>();
                _fx[i].playOnAwake = false;
            }
            AudioListener.volume = PlayerPrefs.GetInt("pancing.mute", 0) == 1 ? 0f : 1f;

            var bus = Game.Bus;
            bus.On(EV.CastStart, _ => Play("cast", 0.75f, 0.08f));
            bus.On(EV.CastLand, _ => Play("plop", 0.8f, 0.12f));
            bus.On(EV.Nibble, _ => Play("nibble", 0.7f, 0.15f));
            bus.On(EV.BiteOn, _ => Play("bloop", 1f, 0.05f));
            bus.On(EV.Hooked, _ => { Play("strike", 0.8f, 0.1f); Play("splash", 0.7f, 0.1f); });
            bus.On(EV.FishJump, _ => Play("splash", 1f, 0.15f));
            bus.On(EV.LineSnap, _ => Play("snap", 1f, 0.05f));
            bus.On(EV.HookLost, _ => Play("splash", 0.5f, 0.2f));
            bus.On<CatchCard>(EV.Landed, card => { if (card != null && !card.Lost) Play("catch", 0.9f, 0f); });
            bus.On(EV.LevelUp, _ => Play("levelup", 0.9f, 0f));
            bus.On(EV.GearBuy, _ => Play("coin", 0.8f, 0.03f));
        }

        private AudioSource Loop(string name, string clip = null)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.loop = true;
            src.playOnAwake = false;
            src.volume = 0f;
            if (clip != null && _clips.TryGetValue(clip, out var c)) { src.clip = c; src.Play(); }
            return src;
        }

        /// <summary>One-shot with a little pitch variation so repeats do not machine-gun.</summary>
        public void Play(string clip, float volume, float pitchJitter)
        {
            if (!_clips.TryGetValue(clip, out var c)) return;
            var src = _fx[_fxNext];
            _fxNext = (_fxNext + 1) % _fx.Length;
            src.pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            src.PlayOneShot(c, volume);
        }

        /// <summary>Called every frame by Bootstrap.</summary>
        public void Apply(in FishingGame.Telemetry tm, World world, Spot spot, float dt)
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.M))
            {
                bool mute = AudioListener.volume > 0f;
                AudioListener.volume = mute ? 0f : 1f;
                PlayerPrefs.SetInt("pancing.mute", mute ? 1 : 0);
            }
            if (world == null || spot == null) return;

            // --- bed: the place and the hour -------------------------------------
            float light = Mathf.Clamp01((float)world.Light);
            string want = light < 0.3f ? "amb_malam" : spot.Id == "kolam" ? "amb_kampung" : "amb_hutan";
            if (want != _bedClip && _clips.TryGetValue(want, out var bed))
            {
                // Start the new bed on whichever source is currently silent.
                var into = _bedMix > 0.5f ? _bedB : _bedA;
                into.clip = bed;
                into.time = Random.Range(0f, bed.length * 0.9f);
                into.Play();
                _bedClip = want;
                _bedTarget = into == _bedA ? 1f : 0f;
            }
            _bedMix = Mathf.MoveTowards(_bedMix, _bedTarget, dt / 3f);   // 3 s cross-fade
            float wet = Mathf.Clamp01((float)world.Rain);
            float bedVol = 0.55f * (1f - wet * 0.5f);
            _bedA.volume = bedVol * _bedMix;
            _bedB.volume = bedVol * (1f - _bedMix);

            // --- water and weather ------------------------------------------------
            if (spot.Id != _spotId)
            {
                _spotId = spot.Id;
                _water.Stop();
                if (spot.Id == "sungai" && _clips.TryGetValue("amb_sungai", out var river))
                {
                    _water.clip = river;
                    _water.Play();
                }
            }
            _water.volume = Mathf.MoveTowards(_water.volume, spot.Id == "sungai" ? 0.42f : 0f, dt);
            _rain.volume = Mathf.MoveTowards(_rain.volume, wet * 0.7f, dt * 0.5f);

            // --- the reel and the clutch -------------------------------------------
            // One click per ~4 cm of line actually recovered, so a reel losing to
            // the fish audibly slows down even with the button held.
            float gain = Mathf.Max(0f, (float)tm.ReelGain);
            _reelPhase += gain * 24f * dt;
            if (_reelPhase >= 1f)
            {
                _reelPhase -= Mathf.Floor(_reelPhase);
                Play("reel_tick", 0.35f, 0.06f);
            }
            float dragTarget = tm.Rod.Slipping ? 0.55f : 0f;
            _drag.volume = Mathf.MoveTowards(_drag.volume, dragTarget, dt * 4f);
            _drag.pitch = 0.9f + Mathf.Clamp01((float)tm.Rod.LoadFrac) * 0.4f;
        }

        private float _bedTarget = 1f;
    }
}
