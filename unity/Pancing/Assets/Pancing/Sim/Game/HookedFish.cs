using System;
using System.Collections.Generic;

namespace Pancing.Sim
{
    public enum FightState
    {
        /// <summary>Hard sustained pull away from the angler.</summary>
        Run,
        /// <summary>Straight down, sulking on the bottom.</summary>
        Dive,
        /// <summary>Head-shakes; murders hook hold.</summary>
        Thrash,
        /// <summary>Steady mid-effort, the fish's default cruise.</summary>
        Circle,
        /// <summary>Short violent burst, usually near the bank.</summary>
        Surge,
        /// <summary>Recovering stamina; your window to gain line.</summary>
        Rest,
        /// <summary>Airborne; a tight line here throws the hook.</summary>
        Jump,
        /// <summary>Out of gas, comes in on its side.</summary>
        Beaten,
    }

    public enum FightEventType { State, Jump, Splash, HookShock, StructureHit }

    public struct FightEvent
    {
        public FightEventType Type;
        public FightState State;
        public double Amount;
        public SnagPoint Snag;
    }

    /// <summary>A snag in fish-local metres: X lateral, Z out from the angler.</summary>
    public struct SnagPoint
    {
        public double X, Z, R;
        public string Kind;
        public double Dist;
    }

    /// <summary>
    /// The hooked fish: an agent, not a health bar. A port of web/src/game/fish.js.
    ///
    /// The fish decides what to do based on how much stamina it has left, how hard
    /// it is being pulled, and where the nearest structure is. The player never
    /// fights a number — they fight a thing that has opinions.
    ///
    /// The stamina economy is the whole game:
    ///
    ///   drain = f(tension)     pulling hard tires the fish faster
    ///   line/hook damage       ...but pulling hard also breaks your tackle
    ///
    /// So there is no single correct tension. There is a moving band, and the fish
    /// keeps moving it by changing behaviour. A digger wants you impatient; a
    /// thrasher wants you tight; a jumper wants you tight at exactly the wrong
    /// moment.
    /// </summary>
    public sealed class HookedFish
    {
        /* --- tuning ---------------------------------------------------------- */

        /// <summary>Peak pull in newtons for a strength-1.0 fish of average size.</summary>
        private const double ForceScale = 78;
        /// <summary>How much of the fish's force scales with its actual mass.</summary>
        private const double MassInfluence = 0.55;
        /// <summary>Stamina drained per second at full effort (tension == the fish's own max).</summary>
        private const double DrainAtFullLoad = 0.165;
        /// <summary>Stamina burned simply by being hooked and panicking. Guarantees
        /// that every fight terminates even against a passive angler.</summary>
        private const double DrainBase = 0.032;
        /// <summary>Recovery on a slack line. Deliberately well below the base drain:
        /// letting a fish rest should cost you time, not hand it the fight back.</summary>
        private const double RecoverRate = 0.030;
        /// <summary>Effort below which the fish is coasting and starts to recover.</summary>
        private const double RestEffort = 0.09;
        /// <summary>Below this stamina the fish stops choosing and just gets dragged.</summary>
        private const double BeatenAt = 0.06;
        /// <summary>Head-shake hook damage per second while thrashing.</summary>
        private const double ThrashHookRate = 0.16;
        /// <summary>Hook damage from landing a jump on a tight line.</summary>
        private const double JumpTightPenalty = 0.34;
        /// <summary>Distance in metres at which structure becomes reachable.</summary>
        private const double StructureReach = 2.2;

        private readonly Rng _rng;

        public readonly Species Species;
        public readonly double LengthCm;
        public readonly double MassKg;
        public readonly bool Trophy;
        public readonly FightProfile Profile;

        public double Strength;
        public double StaminaMax;
        public double Stamina = 1;
        public double Aggression, Burst, StructureSeek, JumpChance;
        /// <summary>A clean hookset in the corner of the jaw holds; a lip-hook does not.</summary>
        public double HookQuality;

        public FightState State = FightState.Run;
        public double StateTime;
        public double StateDuration;
        public double Dist;
        public double Depth;
        public double Lateral;
        public double VelAway;
        public double Effort;
        public double Pull;
        public double SmoothPull;
        public double Airborne;
        public SnagPoint? NearStructure;
        public bool OnStructure;
        public double Elapsed;
        public int JumpsMade;

        private double _thrashPhase;
        private double _surgeCooldown;

        private readonly List<FightEvent> _events = new List<FightEvent>();
        private readonly List<double> _weights = new List<double>(8);
        private readonly List<FightState> _states = new List<FightState>(8);

        public HookedFish(FishRoll spec, SpeciesDb db, Rng rng,
                          double hookQuality = 0.7, double startDist = 12,
                          double startDepth = 1.0, double startLateral = 0)
        {
            _rng = rng;
            Species = spec.Species;
            LengthCm = spec.LengthCm;
            MassKg = spec.MassKg;
            Trophy = spec.Trophy;
            Profile = db.ProfileOf(Species);

            var f = Species.Fight;
            // Size relative to the species' own mean — a big one really does pull
            // harder.
            double sizeRatio = LengthCm / Species.Length.Mean;
            double sizeBoost = Math.Pow(MathUtil.Clamp(sizeRatio, 0.5, 2.4), MassInfluence);

            // PARITY: two Float(0.88, 1.14) draws, strength first, then stamina.
            // Individual variation — two fish of the same species and size still
            // differ.
            Strength = f.Strength * _rng.Float(0.88, 1.14) * sizeBoost;
            StaminaMax = f.Stamina * _rng.Float(0.88, 1.14)
                       * MathUtil.Lerp(1, 1.35, MathUtil.Clamp01((sizeRatio - 1) / 1.4));
            Stamina = 1;
            Aggression = f.Aggression;
            Burst = f.Burst;
            StructureSeek = f.StructureSeek;
            JumpChance = f.JumpChance;

            HookQuality = MathUtil.Clamp(hookQuality, 0.05, 1);

            State = FightState.Run;
            StateTime = 0;
            StateDuration = _rng.Float(1.2, 2.4);
            Dist = startDist;
            Depth = startDepth;
            Lateral = startLateral;
            _thrashPhase = _rng.Float(0, Math.PI * 2);
        }

        /// <summary>Peak force this fish can produce right now, in newtons.</summary>
        public double MaxForce => ForceScale * Strength;

        /// <summary>
        /// Pick the next behaviour. Weighted by profile bias, current stamina, how
        /// hard the player is pulling, and whether there is cover within reach.
        /// </summary>
        private FightState ChooseState(double loadFrac)
        {
            var p = Profile;
            double s = Stamina;

            if (s <= BeatenAt) return FightState.Beaten;

            // Exhausted fish rest; a fish being pulled hard resists rather than
            // resting. PARITY: short-circuits, so the Bool draw only happens when
            // the fish is both tired and not under load.
            if (s < 0.30 && loadFrac < RodSystem.ZoneGood && _rng.Bool(0.55)) return FightState.Rest;

            bool structureNear = NearStructure.HasValue &&
                NearStructure.Value.Dist < StructureReach * (1 + StructureSeek);

            _weights.Clear();
            _states.Clear();

            void W(FightState st, double w) { _states.Add(st); _weights.Add(w); }

            W(FightState.Run, p.RunBias * (0.35 + s * 1.15) * (1 + Burst * 0.6));
            W(FightState.Dive, p.DiveBias * (0.45 + s * 0.85) * (structureNear ? 2.4 : 1));
            W(FightState.Thrash, p.ThrashBias * (0.25 + s * 0.9) * (1 + loadFrac * 0.9));
            W(FightState.Circle, p.CircleBias * (0.55 + (1 - s) * 0.85));
            W(FightState.Surge, p.SurgeRate * Burst * (_surgeCooldown > 0 ? 0 : 1)
                * (Dist < 5 ? 1.9 : 0.7) * (0.3 + s));
            W(FightState.Rest, p.RestRate * (1 - s) * 1.4);

            // Jumping is opportunistic: a jumper near the surface with gas left.
            if (JumpChance > 0 && s > 0.25 && Depth < 1.6 && _surgeCooldown <= 0)
            {
                W(FightState.Jump, JumpChance * 2.2 * (loadFrac > RodSystem.ZoneGood ? 1.6 : 1));
            }

            int idx = _rng.WeightedIndex(_weights);
            return idx >= 0 ? _states[idx] : FightState.Circle;
        }

        private void EnterState(FightState state)
        {
            State = state;
            StateTime = 0;
            switch (state)
            {
                case FightState.Run: StateDuration = _rng.Float(1.4, 3.4); break;
                case FightState.Dive: StateDuration = _rng.Float(1.8, 4.0); break;
                case FightState.Thrash: StateDuration = _rng.Float(0.7, 1.8); break;
                case FightState.Circle: StateDuration = _rng.Float(2.0, 4.5); break;
                case FightState.Surge:
                    StateDuration = _rng.Float(0.35, 0.9);
                    _surgeCooldown = _rng.Float(2.5, 5.0);
                    break;
                case FightState.Rest: StateDuration = _rng.Float(1.0, 2.6); break;
                case FightState.Jump:
                    StateDuration = 0.85;
                    Airborne = 0;
                    _surgeCooldown = _rng.Float(3, 6);
                    JumpsMade++;
                    break;
                case FightState.Beaten: StateDuration = 999; break;
            }
        }

        public struct Ctx
        {
            /// <summary>Newtons currently on the line, from RodSystem.</summary>
            public double Tension;
            /// <summary>Tension / breaking strain, from RodSystem.</summary>
            public double LoadFrac;
            /// <summary>Snag points in fish-local metres.</summary>
            public IReadOnlyList<SnagPoint> Structures;
            /// <summary>Bed depth at the fish's position.</summary>
            public double MaxDepth;
        }

        public struct Report
        {
            public double Pull;
            public FightState State;
            public double Stamina;
            public double VelAway;
            public double Dist, Depth, Lateral, Airborne;
            public bool OnStructure;
            public bool Beaten;
            public IReadOnlyList<FightEvent> Events;
        }

        public Report Update(double dt, in Ctx ctx)
        {
            Elapsed += dt;
            StateTime += dt;
            _surgeCooldown = Math.Max(0, _surgeCooldown - dt);
            _events.Clear();

            // --- stamina economy ---------------------------------------------
            // Effort is tension measured against THIS FISH'S strength, not against
            // the line's breaking strain. That distinction is the whole difficulty
            // curve: 30 N is a crushing workload for a Tilapia and a gentle stretch
            // for a Toman, so heavy tackle beats small fish quickly and still
            // cannot bully a big one. Scaling by the line instead would have made
            // better line tire fish *slower*, which is exactly backwards.
            double effort = MathUtil.Clamp01(ctx.Tension / Math.Max(MaxForce, 1));
            double load = MathUtil.Clamp01(ctx.LoadFrac);
            double drain = DrainBase + DrainAtFullLoad * Math.Pow(effort, 1.35);
            bool resting = State == FightState.Rest || State == FightState.Beaten;
            double recovery = effort < RestEffort ? RecoverRate * (resting ? 1.8 : 1.0) : 0;
            Effort = effort;
            Stamina = MathUtil.Clamp01(Stamina - (drain / StaminaMax) * dt + recovery * dt);

            // --- behaviour transitions ----------------------------------------
            if (StateTime >= StateDuration)
            {
                var next = ChooseState(ctx.LoadFrac);
                if (next != State)
                {
                    EnterState(next);
                    _events.Add(new FightEvent { Type = FightEventType.State, State = State });
                    if (State == FightState.Jump) _events.Add(new FightEvent { Type = FightEventType.Jump });
                }
                else
                {
                    StateTime = 0;
                    StateDuration *= _rng.Float(0.7, 1.3);
                }
            }

            // --- force output --------------------------------------------------
            double pull = 0;
            double vDepth = 0;
            double vLateral = 0;
            double gas = MathUtil.SmoothStep(0, 0.35, Stamina);   // weak fish pull weakly
            double t = StateTime;

            switch (State)
            {
                case FightState.Run:
                {
                    // Accelerate into the run, then fade — fish do not pull flat.
                    double shape = Math.Pow(Math.Sin(MathUtil.Clamp01(t / StateDuration) * Math.PI), 0.7);
                    pull = MaxForce * (0.62 + Aggression * 0.38) * shape * gas;
                    vLateral = (Lateral >= 0 ? 1 : -1) * 0.9 * gas;
                    break;
                }
                case FightState.Dive:
                {
                    pull = MaxForce * 0.55 * gas * (0.8 + 0.2 * Math.Sin(t * 2.1));
                    vDepth = 1.15 * gas;
                    break;
                }
                case FightState.Thrash:
                {
                    // Rapid oscillation: the tension needle should visibly hammer.
                    _thrashPhase += dt * 17;
                    double shake = 0.5 + 0.5 * Math.Sin(_thrashPhase);
                    pull = MaxForce * (0.35 + 0.65 * shake) * gas;
                    vDepth = Math.Sin(_thrashPhase * 0.5) * 0.3;
                    break;
                }
                case FightState.Circle:
                {
                    pull = MaxForce * 0.38 * gas * (0.85 + 0.15 * Math.Sin(t * 1.3));
                    vLateral = Math.Sin(t * 0.9) * 1.1 * gas;
                    break;
                }
                case FightState.Surge:
                {
                    double shape = Math.Exp(-Math.Pow((t - 0.18) / 0.22, 2));
                    pull = MaxForce * (1.05 + Burst * 0.55) * shape * gas;
                    break;
                }
                case FightState.Rest:
                {
                    pull = MaxForce * 0.12 * gas;
                    break;
                }
                case FightState.Jump:
                {
                    // Out of the water: no drag on the fish, so the line goes light
                    // for a beat, then the fish lands. A tight line on landing tears
                    // the hook.
                    Airborne = Math.Sin(MathUtil.Clamp01(t / StateDuration) * Math.PI);
                    pull = MaxForce * 0.25 * (1 - Airborne) * gas;
                    vDepth = -Airborne * 2.0;
                    if (t >= StateDuration - dt && load > RodSystem.ZoneGood)
                    {
                        _events.Add(new FightEvent
                        {
                            Type = FightEventType.HookShock,
                            Amount = JumpTightPenalty * (0.6 + load * 0.7),
                        });
                        _events.Add(new FightEvent { Type = FightEventType.Splash });
                    }
                    break;
                }
                case FightState.Beaten:
                {
                    pull = MaxForce * 0.09;
                    vDepth = -0.35;
                    break;
                }
            }

            Pull = pull;
            SmoothPull = MathUtil.Damp(SmoothPull, pull, 12, dt);

            // --- movement -------------------------------------------------------
            // Pure force balance against water drag. The fish swims away only while
            // it out-pulls the line, and is dragged in when the line out-pulls it.
            // Nothing here knows about the reel or the clutch: reeling shortens the
            // line, which raises tension, which flips the sign of the net force.
            // The tug of war is emergent, which is why a locked drag and a loose
            // drag feel so different without either being special-cased.
            double T = ctx.Tension;
            double net = pull - T;
            // Water drag on a body of this mass, N per m/s. Big fish are hard to
            // stop and also hard to accelerate.
            double waterDrag = 34 + MassKg * 14 + LengthCm * 0.22;
            double vel = MathUtil.Clamp(net / waterDrag, -2.6, 3.2);
            Dist = Math.Max(0.6, Dist + vel * dt);
            VelAway = vel;

            double freedom = 1 - MathUtil.Clamp01(load * 0.8);

            Depth = MathUtil.Clamp(Depth + vDepth * dt, 0, ctx.MaxDepth <= 0 ? 6 : ctx.MaxDepth);
            Lateral += vLateral * freedom * dt;
            if (Math.Abs(Lateral) > 6) Lateral *= 0.92;

            // --- structure ------------------------------------------------------
            NearStructure = null;
            if (ctx.Structures != null && ctx.Structures.Count > 0)
            {
                double best = double.PositiveInfinity;
                for (int i = 0; i < ctx.Structures.Count; i++)
                {
                    var s = ctx.Structures[i];
                    double d = MathUtil.Hypot(s.X - Lateral, s.Z - Dist) - s.R;
                    if (d < best)
                    {
                        best = d;
                        s.Dist = d;
                        NearStructure = s;
                    }
                }
                bool wasOn = OnStructure;
                OnStructure = NearStructure.HasValue && NearStructure.Value.Dist <= 0;
                if (OnStructure && !wasOn)
                {
                    _events.Add(new FightEvent
                    {
                        Type = FightEventType.StructureHit,
                        Snag = NearStructure.Value,
                    });
                }
            }

            // Diving fish actively steer toward cover.
            if (NearStructure.HasValue && (State == FightState.Dive || State == FightState.Run))
            {
                double pullToward = StructureSeek * freedom * 0.8 * dt;
                Lateral += Math.Sign(NearStructure.Value.X - Lateral) * pullToward;
            }

            // --- hook wear from behaviour ---------------------------------------
            if (State == FightState.Thrash)
            {
                double wear = ThrashHookRate * Profile.HookWear
                            * (0.4 + load * 1.2) * (1.35 - HookQuality) * dt;
                _events.Add(new FightEvent { Type = FightEventType.HookShock, Amount = wear });
            }

            return new Report
            {
                Pull = Pull,
                State = State,
                Stamina = Stamina,
                VelAway = VelAway,
                Dist = Dist,
                Depth = Depth,
                Lateral = Lateral,
                Airborne = Airborne,
                OnStructure = OnStructure,
                Beaten = State == FightState.Beaten,
                Events = _events,
            };
        }

        public struct Telemetry
        {
            public Species Species;
            public FightState State;
            public double Stamina, LengthCm, MassKg, Dist, Depth;
            /// <summary>The renderer positions the fish from all three axes — omitting
            /// lateral here silently produced NaN positions downstream.</summary>
            public double Lateral;
            public double VelAway, Effort, Pull, MaxForce, Airborne;
            public bool OnStructure, Trophy;
            public double Elapsed;
        }

        public Telemetry GetTelemetry() => new Telemetry
        {
            Species = Species,
            State = State,
            Stamina = Stamina,
            LengthCm = LengthCm,
            MassKg = MassKg,
            Dist = Dist,
            Depth = Depth,
            Lateral = Lateral,
            VelAway = VelAway,
            Effort = Effort,
            Pull = SmoothPull,
            MaxForce = MaxForce,
            Airborne = Airborne,
            OnStructure = OnStructure,
            Trophy = Trophy,
            Elapsed = Elapsed,
        };
    }
}
