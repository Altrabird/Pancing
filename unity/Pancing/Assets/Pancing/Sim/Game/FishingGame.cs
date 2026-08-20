using System;
using System.Collections.Generic;

namespace Pancing.Sim
{
    public enum GameState { Ready, Charging, Flying, Sinking, Fishing, Fight, Resolve }

    /// <summary>What the catch card is built from — a landed fish, or a lost one.</summary>
    public sealed class CatchCard
    {
        public bool Lost;
        public string LostKind;
        public FishRoll Fish;
        public Species Species;
        public double Value, Xp;
        public bool IsRecord;
        public int Levels;
        public List<Quest> QuestRewards;
        public SizeClass SizeClass;
        public double FightSeconds;
        public double PeakTension;
        public string SpotId;
        public string PhaseId;
    }

    /// <summary>Per-tick player input, already normalised by the host.</summary>
    public struct GameInput
    {
        /// <summary>0..1 requested retrieve.</summary>
        public double ReelAxis;
        /// <summary>-1..1 drag adjustment.</summary>
        public double DragAxis;
    }

    /// <summary>
    /// The fishing loop — a port of web/src/game/fishing.js.
    ///
    /// This is the state machine that owns a single cast, from winding up to
    /// either a fish in the net or a bare hook. It is the only place that knows
    /// about all the subsystems at once, and it is deliberately headless: it never
    /// touches UnityEngine, a MonoBehaviour, or the wall clock. The renderer
    /// subscribes to its events and the test harness drives it directly.
    ///
    ///   READY ─cast─&gt; CHARGING ─release─&gt; FLYING ─splash─&gt; SINKING ─&gt; FISHING
    ///     ^                                                              │
    ///     │                                                      hookset ▼
    ///     └──── RESOLVE &lt;── FIGHT &lt;─────────────────────────────────  HOOKED
    /// </summary>
    public sealed class FishingGame
    {
        /// <summary>How long the catch card stays up before the rod resets.</summary>
        private const double ResolveHold = 2.6;
        /// <summary>The far edge of the fishable water, metres. Casting maps onto this.</summary>
        public const double MaxCast = 34;
        /// <summary>Half-width of the fishable water, metres.</summary>
        public const double HalfWidth = 18;

        private readonly Rng _rng;
        private readonly EventBus _bus;
        private readonly PlayerState _state;
        private readonly World _world;
        private readonly SpeciesDb _speciesDb;

        public readonly CastSystem Cast;
        public readonly BiteSystem Bite;
        public readonly RodSystem Rod;
        public HookedFish Fish;

        public GameState Phase = GameState.Ready;
        public double ResolveTimer;
        public CatchCard LastCatch;
        public double LureDepth;
        public double LureDepthNorm;
        public double BedDepth = 1;
        public double RetrieveRate;
        public double Jerk;
        public Vec3 TipPos = new Vec3(0, 1.9, 0);
        public double HookedElapsed;
        public double SessionSeconds;

        private GearSet _gear;
        private FishRoll _pending;
        private double _prevReel;
        private bool _strikeQueued;
        /// <summary>
        /// Counts casts, and seeds each fight's RNG sub-stream.
        ///
        /// DELIBERATE DIVERGENCE from the JavaScript reference, which forks on
        /// `Date.now()`. A wall-clock seed makes a fight unreproducible, which
        /// defeats the point of having a seeded RNG at all — you cannot replay a
        /// bug report, and the balance harness cannot pin a fight. Counting casts
        /// gives the same decorrelation with none of that. The subsystem parity
        /// tests seed their own streams explicitly, so this does not affect them.
        /// </summary>
        private int _castIndex;

        private readonly List<SnagPoint> _snagBuffer = new List<SnagPoint>();

        public GearSet Gear => _gear;

        public FishingGame(Rng rng, EventBus bus, PlayerState state, World world, SpeciesDb speciesDb)
        {
            _rng = rng;
            _bus = bus;
            _state = state;
            _world = world;
            _speciesDb = speciesDb;

            Cast = new CastSystem(rng.Fork("cast"));
            Bite = new BiteSystem(rng.Fork("bite"), speciesDb.LureMismatch);
            Rod = new RodSystem();

            RefreshGear();
            _bus.On(EV.GearEquip, _ => RefreshGear());
        }

        public void RefreshGear()
        {
            _gear = _state.Gear();
            Rod.Configure(_gear);
            TipPos.Y = 1.05 + _gear.Rod.Length * 0.55;
        }

        /* --- lure position helpers ------------------------------------------- */

        /// <summary>Normalised (u, v) of the lure across the castable water, for DepthAt().</summary>
        public void LureUV(out double u, out double v)
        {
            u = MathUtil.Clamp01(Cast.Distance / MaxCast);
            v = MathUtil.Clamp(Cast.Pos.X / HalfWidth, -1, 1);
        }

        public double BedDepthAt()
        {
            LureUV(out double u, out double v);
            var spot = _state.Spot;
            return Math.Max(0.35, spot.DepthAt(u, v) * spot.MaxDepth);
        }

        /// <summary>Structures near the lure, expressed in metres relative to the rod tip.</summary>
        public IReadOnlyList<SnagPoint> StructuresNear()
        {
            _snagBuffer.Clear();
            var spot = _state.Spot;
            foreach (var s in spot.Structure)
            {
                _snagBuffer.Add(new SnagPoint
                {
                    X = s.V * HalfWidth,
                    Z = s.U * MaxCast,
                    R = s.R * 12,
                    Kind = s.Kind,
                });
            }
            return _snagBuffer;
        }

        /* --- input entry points ------------------------------------------------ */

        public bool BeginCast()
        {
            if (Phase != GameState.Ready) return false;
            if (!Cast.BeginCharge()) return false;
            Phase = GameState.Charging;
            return true;
        }

        public bool ReleaseCast()
        {
            if (Phase != GameState.Charging) return false;
            _state.ConsumeBait();
            RefreshGear();
            var result = Cast.DoRelease(TipPos, _gear, _world.Wind);
            if (!result.Ok) return false;

            Phase = GameState.Flying;
            _castIndex++;
            _state.Stats.Casts++;
            Rod.Respool(2.0);
            Bite.Reset();
            _bus.Emit(EV.CastStart, result);
            if (result.Backlash)
            {
                _bus.Emit(EV.Toast, new Toast { Text = "Tali kusut! Lontaran tersekat.", Kind = "warn" });
            }
            return true;
        }

        /// <summary>The hookset. Queued, so the strike is honoured on the next tick
        /// regardless of where in the frame the input arrived.</summary>
        public void Strike() => _strikeQueued = true;

        public void Aim(double yaw, double? pitch = null)
        {
            Cast.AimYaw = yaw;
            if (pitch.HasValue) Cast.AimPitch = pitch.Value;
        }

        /// <summary>Give up on the current cast and wind everything back in.</summary>
        public void ReelInHard()
        {
            if (Phase == GameState.Fishing || Phase == GameState.Sinking) FinishCast("reeled-in");
        }

        /// <summary>
        /// Drop everything and return to READY from any phase except a live fight.
        ///
        /// ReelInHard only covers a lure that is already in the water. Travelling to
        /// another location has to work from a half-charged cast or a lure still in
        /// the air too, and leaving either running while the lake is rebuilt around
        /// it means a cast landing in water that no longer exists.
        ///
        /// Refuses during FIGHT on purpose: abandoning a hooked fish by walking away
        /// should not be free, and the panels already refuse to open with one on.
        /// </summary>
        public bool Abort()
        {
            if (Phase == GameState.Fight) return false;
            if (Phase != GameState.Ready) FinishCast("aborted");
            return true;
        }

        public struct Toast { public string Text, Kind; }

        /* --- the tick ---------------------------------------------------------- */

        public void Update(double dt, GameInput input)
        {
            SessionSeconds += dt;
            _state.Stats.PlaySeconds = MathUtil.JsRound(SessionSeconds);

            bool struck = _strikeQueued;
            _strikeQueued = false;

            // Drag adjustment is always live; a fight is often won on the clutch.
            if (input.DragAxis != 0) Rod.AdjustDrag(input.DragAxis * 0.45 * dt);

            // Rod-tip jerk: how violently the angler is changing retrieve. Cautious
            // fish notice.
            double reel = input.ReelAxis;
            Jerk = MathUtil.Damp(Jerk, Math.Abs(reel - _prevReel) / Math.Max(dt, 1e-4) * 0.02, 8, dt);
            _prevReel = reel;

            switch (Phase)
            {
                case GameState.Charging: TickCharging(dt); break;
                case GameState.Flying: TickFlying(dt); break;
                case GameState.Sinking: TickSinking(dt); break;
                case GameState.Fishing: TickFishing(dt, reel, struck); break;
                case GameState.Fight: TickFight(dt, reel, struck); break;
                case GameState.Resolve: TickResolve(dt); break;
            }
        }

        private void TickCharging(double dt)
        {
            if (Cast.UpdateCharge(dt)) ReleaseCast();
        }

        private void TickFlying(double dt)
        {
            var r = Cast.UpdateFlight(dt, 0, _world.Wind);
            if (r.Event == CastSystem.FlightEvent.Splash)
            {
                BedDepth = BedDepthAt();
                Phase = GameState.Sinking;
                Rod.Respool(Math.Max(2.0, Cast.Distance));
                _bus.Emit(EV.CastLand, new Landing { Pos = r.Pos, Impact = r.Impact, Distance = Cast.Distance });
                _bus.Emit(EV.Splash, new Fx { Pos = r.Pos, Strength = MathUtil.Clamp01(r.Impact / 18) });
                _bus.Emit(EV.Ripple, new Fx { Pos = r.Pos, Strength = MathUtil.Clamp01(r.Impact / 14) });
            }
            else if (r.Event == CastSystem.FlightEvent.DryLand)
            {
                _bus.Emit(EV.Toast, new Toast { Text = "Tersangkut di darat.", Kind = "warn" });
                FinishCast("dryland");
            }
        }

        public struct Landing { public Vec3 Pos; public double Impact, Distance; }
        public struct Fx { public Vec3 Pos; public double Strength; }

        private void TickSinking(double dt)
        {
            bool settled = Cast.UpdateSink(dt, _gear.Lure, BedDepth);
            LureDepth = Cast.SinkDepth;
            LureDepthNorm = MathUtil.Clamp01(LureDepth / Math.Max(BedDepth, 0.1));
            if (settled)
            {
                Phase = GameState.Fishing;
                Bite.Begin();
                _bus.Emit(EV.LureSettled, new Settled { Depth = LureDepth, Bed = BedDepth });
            }
        }

        public struct Settled { public double Depth, Bed; }

        private void TickFishing(double dt, double reel, bool struck)
        {
            // Retrieving moves the lure home and lifts it in the column.
            RetrieveRate = reel * _gear.Reel.Retrieve;
            if (RetrieveRate > 0)
            {
                Cast.Retrieve(dt, RetrieveRate, TipPos);
                Rod.LineOut = Math.Max(0.6, Cast.Distance);
                BedDepth = BedDepthAt();
                LureDepth = Cast.SinkDepth;
                LureDepthNorm = MathUtil.Clamp01(LureDepth / Math.Max(BedDepth, 0.1));
                _bus.Emit(EV.Ripple, new Fx { Pos = Cast.Pos, Strength = 0.12 * reel });
            }
            else
            {
                // A settled bait keeps sinking slowly toward its working depth.
                Cast.Phase = CastPhase.Settled;
            }

            // Reeled all the way back with nothing on: the cast is over.
            if (Cast.Distance <= 1.4) { FinishCast("retrieved"); return; }

            // Keep the tension model live even with nothing on the end, so the meter
            // and the rod bend show the weight of the lure and the drag of the water.
            Rod.LineOut = Math.Max(0.6, Cast.Distance);
            Rod.Update(dt, new RodSystem.Ctx
            {
                FishDist = Rod.LineOut,
                ReelInput = 0,
                ExtraLoad = 0.9 + reel * 4.5 * (0.4 + _gear.Lure.Sink),
                AllowSlip = false,
            });

            var ev = Bite.Update(dt, new BiteSystem.Ctx
            {
                Lure = _gear.Lure,
                Line = _gear.Line,
                Spot = _state.Spot,
                LureDepthNorm = LureDepthNorm,
                RetrieveRate = reel,
                Noise = _world.SurfaceNoise() + _gear.Lure.Noise * reel,
                SpotActivity = _world.Activity(),
                Jerk = Jerk,
                Struck = struck,
                DrawCandidate = DrawCandidate,
            });

            if (ev.Any) HandleBiteEvent(ev);
        }

        private Species DrawCandidate() => CatchTable.DrawSpecies(_rng, new CatchTable.Ctx
        {
            Spot = _state.Spot,
            Phase = _world.Phase,
            Weather = _world.Weather,
            Lure = _gear.Lure,
            LureDepthNorm = LureDepthNorm,
            Level = _state.Level,
            ActivityBonus = _world.Activity(),
            Db = _speciesDb,
        });

        private void HandleBiteEvent(in BiteEvent ev)
        {
            switch (ev.Type)
            {
                case BiteEventType.Interest:
                    _bus.Emit(EV.Interest, ev.Species);
                    break;
                case BiteEventType.Nibble:
                    _state.Stats.Bites++;
                    _bus.Emit(EV.Nibble, ev);
                    _bus.Emit(EV.Ripple, new Fx { Pos = Cast.Pos, Strength = 0.22 });
                    break;
                case BiteEventType.Committing:
                    _bus.Emit(EV.BiteOn, ev);
                    break;
                case BiteEventType.Bite:
                    _bus.Emit(EV.BiteOn, ev);
                    _bus.Emit(EV.Ripple, new Fx { Pos = Cast.Pos, Strength = 0.45 });
                    break;
                case BiteEventType.Hooked:
                    BeginFight(ev.Species, ev.Quality);
                    break;
                case BiteEventType.Missed:
                    _state.RegisterLoss("missed");
                    _bus.Emit(EV.BiteMissed, ev);
                    _bus.Emit(EV.Toast, new Toast { Text = "Terlepas — sambaran lambat.", Kind = "miss" });
                    break;
                case BiteEventType.Spooked:
                    _state.RegisterLoss("spooked");
                    _bus.Emit(EV.Spooked, ev);
                    if (ev.Reason == "struck-early")
                        _bus.Emit(EV.Toast, new Toast { Text = "Terlalu awal! Ikan lari.", Kind = "miss" });
                    break;
                case BiteEventType.Whiff:
                    _bus.Emit(EV.HooksetEarly, null);
                    break;
            }
        }

        private void BeginFight(Species species, double quality)
        {
            var roll = CatchTable.RollFish(_rng, _speciesDb, species, _gear.Lure.SizeBias, _state.Luck());
            _pending = roll;
            Fish = new HookedFish(
                roll, _speciesDb, _rng.Fork($"fight:{_castIndex}"),
                hookQuality: quality,
                startDist: Math.Max(3, Cast.Distance),
                startDepth: LureDepth,
                // The fish is where the lure is, not on the centreline. Without this
                // the fish never starts near the cover it is supposed to run for.
                startLateral: Cast.Pos.X);

            Rod.Respool(Math.Max(3, Cast.Distance));
            Rod.HookHold = MathUtil.Clamp(0.35 + quality * 0.65, 0.2, 1);
            Phase = GameState.Fight;
            HookedElapsed = 0;
            _state.Stats.HookedCount++;
            _bus.Emit(EV.Hooked, roll);
            _bus.Emit(EV.FightStart, Fish.GetTelemetry());
            _bus.Emit(EV.Splash, new Fx { Pos = Cast.Pos, Strength = 0.6 });
        }

        private void TickFight(double dt, double reel, bool struck)
        {
            HookedElapsed += dt;
            var fish = Fish;

            // 1. Fish decides and pulls.
            var fr = fish.Update(dt, new HookedFish.Ctx
            {
                Tension = Rod.Tension,
                LoadFrac = Rod.LoadFrac,
                Structures = StructuresNear(),
                MaxDepth = BedDepth,
            });

            for (int i = 0; i < fr.Events.Count; i++)
            {
                var e = fr.Events[i];
                switch (e.Type)
                {
                    case FightEventType.HookShock:
                        Rod.ShockHook(e.Amount);
                        break;
                    case FightEventType.Jump:
                        _bus.Emit(EV.FishJump, fish.GetTelemetry());
                        _bus.Emit(EV.Splash, new Fx { Pos = Cast.Pos, Strength = 0.8 });
                        break;
                    case FightEventType.Splash:
                        _bus.Emit(EV.Splash, new Fx { Pos = Cast.Pos, Strength = 0.7 });
                        _bus.Emit(EV.Ripple, new Fx { Pos = Cast.Pos, Strength = 0.6 });
                        break;
                    case FightEventType.State:
                        _bus.Emit(EV.FightStateChanged, fish.GetTelemetry());
                        break;
                    case FightEventType.StructureHit:
                        Rod.Abrade(0.06);
                        _bus.Emit(EV.Snagged, e.Snag);
                        break;
                }
            }

            // 2. The fish has moved; the rod now solves the tension that the new
            //    geometry implies. Nothing is added on top — the pull already showed
            //    up as distance, and distance is what stretches line.
            var report = Rod.Update(dt, new RodSystem.Ctx
            {
                FishDist = fish.Dist,
                ReelInput = reel,
                OnStructure = fr.OnStructure,
                AllowSlip = true,
            });

            // 3. Outcomes.
            if (report.Snapped) { LoseFish("snap"); return; }
            if (report.HookLost) { LoseFish("hook"); return; }
            if (fr.OnStructure && Rod.LineIntegrity < 0.12) { LoseFish("snag"); return; }

            // Landing: the fish is at the rod tip and can be lifted — either because
            // it has nothing left, or because the tackle simply out-guns it. Without
            // that second clause a 200 g Tilapia on 30 lb braid would still need a
            // full exhaustion fight, which is nonsense.
            bool outgunned = fish.MaxForce < Rod.Drag * 0.8;
            if (fish.Dist <= 1.7 && (fish.Stamina < 0.30 || outgunned || fish.State == FightState.Beaten))
            {
                LandFish();
                return;
            }

            // A hookset during a fight is a "pump" — it costs hook hold for nothing.
            if (struck) Rod.ShockHook(0.05);

            if (report.OverloadedRod) _bus.Emit(EV.RodOverload, report.Tension);
            _bus.Emit(EV.Ripple, new Fx
            {
                Pos = Cast.Pos,
                Strength = 0.05 + fr.Pull / Math.Max(fish.MaxForce, 1e-6) * 0.1,
            });
        }

        private void LandFish()
        {
            var fish = _pending;
            string phaseId = _world.Phase?.Id;
            int value = CatchTable.ValueOf(fish);
            int xp = CatchTable.XpOf(fish);
            var reward = _state.RecordCatch(fish, value, xp, phaseId);

            LastCatch = new CatchCard
            {
                Fish = fish,
                Species = fish.Species,
                Value = value,
                Xp = xp,
                IsRecord = reward.IsRecord,
                Levels = reward.Levels,
                QuestRewards = reward.QuestRewards,
                SizeClass = CatchTable.ClassOf(fish),
                FightSeconds = MathUtil.RoundTo(HookedElapsed, 10),
                PeakTension = MathUtil.RoundTo(Rod.PeakTension, 10),
                SpotId = _state.SpotId,
                PhaseId = phaseId,
            };

            _bus.Emit(EV.Landed, LastCatch);
            _bus.Emit(EV.Splash, new Fx { Pos = Cast.Pos, Strength = 0.9 });
            EnterResolve();
        }

        private void LoseFish(string kind)
        {
            string text = kind == "snap" ? "Tali putus!"
                        : kind == "hook" ? "Mata kail terlucut!"
                        : kind == "snag" ? "Tersangkut — ikan bawa ke reba."
                        : "Ikan terlepas.";

            _state.RegisterLoss(kind == "snap" ? "snap" : "lost");
            if (kind == "snap") _bus.Emit(EV.LineSnap, Rod.Tension);
            else _bus.Emit(EV.HookLost, kind);
            _bus.Emit(EV.Toast, new Toast { Text = text, Kind = "fail" });

            LastCatch = new CatchCard { Lost = true, LostKind = kind, Species = Fish?.Species };
            EnterResolve();
        }

        private void EnterResolve()
        {
            Phase = GameState.Resolve;
            ResolveTimer = ResolveHold;
            Fish = null;
            _pending = null;
            _bus.Emit(EV.FightEnd, LastCatch);
        }

        private void TickResolve(double dt)
        {
            ResolveTimer -= dt;
            if (ResolveTimer <= 0) FinishCast("resolved");
        }

        private void FinishCast(string reason)
        {
            Phase = GameState.Ready;
            Cast.Reset();
            Bite.Reset();
            Rod.Respool(0);
            Fish = null;
            RetrieveRate = 0;
            LureDepth = 0;
            _bus.Emit(EV.ReelIn, reason);
        }

        /* --- telemetry ---------------------------------------------------------- */

        public struct Telemetry
        {
            public GameState Phase;
            public CastSystem.ChargeMeter Cast;
            public double CastDistance, LureDepth, LureDepthNorm, BedDepth;
            public RodSystem.Telemetry Rod;
            public BiteSystem.Telemetry Bite;
            public HookedFish.Telemetry? Fish;
            public CatchCard LastCatch;
            public GearSet Gear;
            public double FightSeconds;
        }

        public Telemetry GetTelemetry() => new Telemetry
        {
            Phase = Phase,
            Cast = Cast.Meter(),
            CastDistance = Cast.Distance,
            LureDepth = LureDepth,
            LureDepthNorm = LureDepthNorm,
            BedDepth = BedDepth,
            Rod = Rod.GetTelemetry(),
            Bite = Bite.GetTelemetry(),
            Fish = Fish?.GetTelemetry(),
            LastCatch = LastCatch,
            Gear = _gear,
            FightSeconds = HookedElapsed,
        };
    }
}
