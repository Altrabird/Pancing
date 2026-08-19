using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Pancing.Sim;

/// <summary>
/// Parity dump — C# side. The mirror of dump_js.mjs, running the identical
/// scripted scenario through the real Pancing.Sim sources. run.sh compiles this
/// together with the Sim assembly using the C# compiler that ships inside Unity
/// (no .NET SDK install required) and diffs the output against Node's.
///
/// Every section here must produce byte-identical text to its JavaScript twin.
/// When adding one, add it to both files in the same commit — a section that
/// exists on only one side silently passes.
/// </summary>
internal static class DumpCs
{
    private static readonly StringBuilder Out = new StringBuilder();

    private static void Say(string s) => Out.Append(s).Append('\n');

    /// <summary>Fixed-width, invariant culture — a diff must point at a field, not a locale.</summary>
    private static string F(double x)
    {
        if (x == 0.0) x = 0.0; // normalise -0
        return x.ToString("F12", CultureInfo.InvariantCulture);
    }

    private static string I(double x) => ((long)x).ToString(CultureInfo.InvariantCulture);

    /// <summary>Enum names are lowercase on the JavaScript side; match them.</summary>
    private static string L(object enumValue) => enumValue.ToString().ToLowerInvariant();

    private static SpeciesDb _species;
    private static GearDb _gear;
    private static SpotDb _spots;

    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : ".";
        string dataDir = Path.Combine(root, "shared", "data");

        _species = SpeciesDb.Load(File.ReadAllText(Path.Combine(dataDir, "species.json")));
        _gear = GearDb.Load(File.ReadAllText(Path.Combine(dataDir, "gear.json")));
        _spots = SpotDb.Load(File.ReadAllText(Path.Combine(dataDir, "spots.json")));

        SectionRng();
        SectionData();
        SectionRod();
        SectionCast();
        SectionCatchTable();
        SectionBite();
        SectionFishAi();

        Console.Out.Write(Out.ToString());
        return 0;
    }

    /* -------------------------------------------------------------- 1. RNG */
    // The integer stream must match bit for bit. Everything else in the game is
    // downstream of it, so a drift here is the only kind unrecoverable.

    private static void SectionRng()
    {
        Say("# section rng-words");
        {
            var r = new Rng(42u);
            for (int n = 0; n < 16; n++) Say($"rngword {n} {r.NextWord()}");
        }

        Say("# section rng-hash");
        foreach (var s in new[] { "pancing", "tilapia", "kolam", "Ikan Keli", "", "0" })
        {
            Say($"hash \"{s}\" {Rng.HashSeed(s)}");
        }

        Say("# section rng-floats");
        {
            var r = new Rng("pancing");
            for (int n = 0; n < 12; n++) Say($"float {n} {F(r.Next())}");
        }

        Say("# section rng-normal");
        {
            var r = new Rng(1337u);
            for (int n = 0; n < 8; n++) Say($"normal {n} {F(r.Normal(21, 6))}");
            for (int n = 0; n < 4; n++) Say($"nclamp {n} {F(r.NormalClamped(21, 6, 11, 45))}");
        }

        Say("# section rng-fork");
        {
            var r = new Rng(9001u);
            foreach (var label in new[] { "bite", "fight", "catch" })
            {
                Say($"fork {label} {r.Fork(label).Seed}");
            }
        }

        Say("# section rng-weighted");
        {
            // Order matters: the roll walks the entries in the order given. This
            // section exists because the C# side had to reproduce JavaScript object
            // ordering — see the note on Json._fields.
            var r = new Rng(777u);
            var keys = new[] { "a", "b", "c", "d", "e" };
            var weights = new List<double> { 3, 1, 0, 6, 2 };
            var counts = new Dictionary<string, int>();
            foreach (var k in keys) counts[k] = 0;
            for (int n = 0; n < 400; n++)
            {
                int idx = r.WeightedIndex(weights);
                if (idx >= 0) counts[keys[idx]]++;
            }
            foreach (var k in keys) Say($"weighted {k} {counts[k]}");
        }
    }

    /* ------------------------------------------------------------- 2. data */

    private static void SectionData()
    {
        Say("# section data");
        Say($"counts {_species.All.Count} {_gear.All.Count} {_spots.All.Count}");

        foreach (var id in new[] { "tilapia", "keli", "toman", "kelah", "udang_galah", "plastik" })
        {
            var sp = _species.Get(id);
            if (sp == null) { Say($"species {id} MISSING"); continue; }
            Say($"species {id} {F(sp.Allometry.A)} {F(sp.Allometry.B)} {F(sp.DepthLo)} {F(sp.DepthHi)} " +
                $"{F(sp.Length.Mean)} {F(sp.Bite.Window)} {sp.Fight.Profile} {sp.RarityId} {sp.MinLevel}");
        }

        foreach (var id in new[] { "rod_fiber", "reel_spin4000", "line_braid30", "worm", "popper" })
        {
            var g = _gear.Get(id);
            Say($"gear {id} {g.Slot} {F(g.Price)} {g.Level}");
        }

        // Pool ORDER, not just contents — the weighted draw walks it in order.
        foreach (var id in new[] { "kolam", "sungai", "tasik" })
        {
            var spot = _spots.Get(id);
            var keys = new List<string>();
            foreach (var kv in spot.Pool) keys.Add(kv.Key);
            Say($"pool {id} {string.Join(",", keys)}");
            Say($"spot {id} {F(spot.MaxDepth)} {F(spot.WaterClarity)} {F(spot.SnagDensity)} {spot.Structure.Length}");
        }

        // DepthAt is code, not data, in both engines. Sample it on a grid.
        foreach (var id in new[] { "kolam", "sungai", "tasik" })
        {
            var spot = _spots.Get(id);
            for (int a = 0; a <= 4; a++)
            {
                var row = new List<string>();
                for (int b = 0; b <= 4; b++) row.Add(F(spot.DepthAt(a / 4.0, -1 + b * 0.5)));
                Say($"depth {id} {a} {string.Join(" ", row)}");
            }
        }

        foreach (var h in new[] { 0, 4.9, 5.0, 7.99, 8.0, 12.5, 17.0, 19.4, 19.5, 23.9 })
        {
            Say($"phase {F(h)} {_spots.PhaseForHour(h).Id}");
        }
    }

    /* -------------------------------------------------- 3. tension solver */

    private static GearSet Gear() => new GearSet
    {
        Rod = _gear.Get("rod_fiber").AsRod(),
        Reel = _gear.Get("reel_spin4000").AsReel(),
        Line = _gear.Get("line_braid30").AsLine(),
        Lure = _gear.Get("spinner").AsLure(),
    };

    private static void SectionRod()
    {
        var gear = Gear();

        Say("# section rod-curve");
        {
            var rs = new RodSystem();
            rs.Configure(gear);
            rs.LineOut = 12;
            for (int n = 0; n <= 10; n++)
            {
                double t = n * 15;
                Say($"curve {n} {F(rs.LineExtension(t))} {F(rs.RodDeflection(t))}");
            }
            for (int n = 0; n <= 10; n++) Say($"solve {n} {F(rs.SolveTension(n * 0.05))}");
        }

        Say("# section rod-fight");
        {
            var rs = new RodSystem();
            rs.Configure(gear);
            rs.SetDragFrac(0.62);
            rs.Respool(14);

            const double dt = 1.0 / 120.0;
            double fishDist = 14;
            for (int tick = 0; tick < 900; tick++)
            {
                double t = tick * dt;
                double runA = Math.Max(0, Math.Sin(t * 0.9)) * 2.4;
                double runB = Math.Max(0, Math.Sin(t * 0.31 - 0.6)) * 3.1;
                fishDist += (runA + runB - 1.35) * dt;
                fishDist = Math.Max(0.5, fishDist);

                double reelInput = (t > 1.5 && Math.Sin(t * 2.2) > -0.2) ? 1 : 0;
                bool onStructure = t > 4.0 && t < 5.2;
                if (tick == 300) rs.SetDragFrac(0.86);
                if (tick == 600) rs.SetDragFrac(0.40);

                var step = rs.Update(dt, new RodSystem.Ctx
                {
                    FishDist = fishDist, ReelInput = reelInput,
                    OnStructure = onStructure, ExtraLoad = 0.4, AllowSlip = true,
                });

                if (tick % 30 == 0)
                {
                    Say($"fight {tick} {F(step.Tension)} {F(step.LineOut)} {F(step.LoadFrac)} " +
                        $"{F(rs.LineIntegrity)} {F(rs.HookHold)} {F(step.Bend)} {F(step.SlipRate)} " +
                        $"{L(step.Zone)} {(step.Slipping ? 1 : 0)} {(step.Snapped ? 1 : 0)}");
                }
            }

            var tm = rs.GetTelemetry();
            Say($"final {F(tm.Tension)} {F(tm.LineOut)} {F(tm.LineIntegrity)} {F(tm.HookHold)} " +
                $"{F(tm.Peak)} {L(tm.Zone)} {(tm.DragUnsafe ? 1 : 0)}");
        }
    }

    /* ---------------------------------------------------------- 4. casting */

    private static void SectionCast()
    {
        Say("# section cast");
        var gear = Gear();
        var tip = new Vec3(0, 2.5, 0);
        // Three release points: perfect, early, and held right through the
        // overload band — the three outcomes the charge curve is shaped to give.
        double[] holds = { 1.15, 0.62, 1.55 };

        for (int c = 0; c < holds.Length; c++)
        {
            var cs = new CastSystem(new Rng((uint)(500 + c)));
            cs.AimYaw = 0.12;
            cs.BeginCharge();

            const double dt = 1.0 / 120.0;
            double held = 0;
            bool auto = false;
            while (held < holds[c])
            {
                if (cs.UpdateCharge(dt)) { auto = true; break; }
                held += dt;
            }
            var rel = cs.DoRelease(tip, gear, 0.4);
            Say($"release {c} {F(rel.Power)} {F(rel.Quality)} {(rel.Backlash ? 1 : 0)} " +
                $"{(rel.Perfect ? 1 : 0)} {(auto ? 1 : 0)}");
            Say($"vel {c} {F(cs.Vel.X)} {F(cs.Vel.Y)} {F(cs.Vel.Z)}");

            int ticks = 0;
            double impact = double.NaN;
            bool splashed = false;
            while (cs.Phase == CastPhase.Flying && ticks < 2000)
            {
                var r = cs.UpdateFlight(dt, 0, 0.4);
                ticks++;
                if (r.Event == CastSystem.FlightEvent.Splash) { splashed = true; impact = r.Impact; break; }
                if (r.Event == CastSystem.FlightEvent.DryLand) break;
            }
            Say($"flight {c} {ticks} {F(cs.Distance)} {F(cs.Pos.X)} {F(cs.Pos.Z)} " +
                $"{(splashed ? F(impact) : "none")}");

            int sinkTicks = 0;
            while (cs.Phase == CastPhase.Sinking && sinkTicks < 4000)
            {
                cs.UpdateSink(dt, gear.Lure, 4.2);
                sinkTicks++;
            }
            Say($"sink {c} {sinkTicks} {F(cs.SinkDepth)} {F(cs.TargetDepth)}");

            double moved = cs.Retrieve(dt * 20, 0.9, tip);
            Say($"retrieve {c} {F(moved)} {F(cs.Distance)} {F(cs.SinkDepth)}");
        }
    }

    /* ------------------------------------------------------ 5. catch table */

    private static CatchTable.Ctx TableCtx()
    {
        TimePhase night = null;
        foreach (var p in _spots.Phases) if (p.Id == "night") night = p;

        return new CatchTable.Ctx
        {
            Spot = _spots.Get("tasik"),
            Phase = night,
            Weather = _spots.GetWeather("rain"),
            Lure = _gear.Get("shrimp").AsLure(),
            LureDepthNorm = 0.78,
            Level = 12,
            ActivityBonus = 1.25,
            Db = _species,
        };
    }

    private static void SectionCatchTable()
    {
        Say("# section catchtable");
        var ctx = TableCtx();

        var entries = CatchTable.BuildTable(ctx, out double total);
        Say($"table total {F(total)} entries {entries.Count}");
        foreach (var e in entries) Say($"entry {e.Id} {F(e.Weight)}");

        // Draw distribution: the single strongest signal that the weighted pick,
        // the pool ordering and the modifier stack all agree.
        var r = new Rng(24680u);
        var counts = new Dictionary<string, int>();
        for (int n = 0; n < 600; n++)
        {
            var sp = CatchTable.DrawSpecies(r, ctx);
            string id = sp != null ? sp.Id : "null";
            counts[id] = counts.TryGetValue(id, out int c) ? c + 1 : 1;
        }
        var ids = new List<string>(counts.Keys);
        ids.Sort(StringComparer.Ordinal);   // JavaScript's default sort is by code unit
        foreach (var id in ids) Say($"draw {id} {counts[id]}");

        var rr = new Rng(13579u);
        foreach (var id in new[] { "tilapia", "toman", "kelah", "udang_galah" })
        {
            var sp = _species.Get(id);
            for (int n = 0; n < 3; n++)
            {
                var fish = CatchTable.RollFish(rr, _species, sp, sizeBias: 0.3, luck: 0.2);
                Say($"roll {id} {n} {F(fish.LengthCm)} {F(fish.MassKg)} {F(fish.Sigma)} " +
                    $"{F(fish.Condition)} {(fish.Trophy ? 1 : 0)} {CatchTable.ValueOf(fish)} " +
                    $"{CatchTable.XpOf(fish)} {CatchTable.ClassOf(fish).Label}");
            }
        }
    }

    /* ------------------------------------------------------------- 6. bite */

    private static void SectionBite()
    {
        Say("# section bite");
        var bs = new BiteSystem(new Rng(31415u), _species.LureMismatch);
        bs.Begin();

        var spot = _spots.Get("kolam");
        var lure = _gear.Get("worm").AsLure();
        var line = _gear.Get("line_mono8").AsLine();
        // A fixed candidate order, so this section tests the FSM rather than the draw.
        string[] candidateOrder = { "tilapia", "keli", "puyu", "haruan" };
        int drawn = 0;

        const double dt = 1.0 / 120.0;
        int struckAt = -1;
        var events = new List<string>();

        for (int tick = 0; tick < 6000; tick++)
        {
            double t = tick * dt;
            // Strike 180 ms after the first hookset window opens; also strike once
            // well before anything is happening, to exercise the whiff path.
            bool struck = tick == 240 || (struckAt >= 0 && tick == struckAt);

            var ev = bs.Update(dt, new BiteSystem.Ctx
            {
                Lure = lure, Line = line, Spot = spot,
                LureDepthNorm = 0.35,
                RetrieveRate = t > 12 ? 0.25 : 0,
                Noise = 0.12,
                SpotActivity = 1.1,
                Jerk = (t > 20 && t < 20.2) ? 0.9 : 0.1,
                Struck = struck,
                DrawCandidate = () => _species.Get(candidateOrder[drawn++ % candidateOrder.Length]),
            });

            if (ev.Any)
            {
                events.Add($"bite {tick} {L(ev.Type)} {(ev.Species != null ? ev.Species.Id : "-")} " +
                           $"{ev.Reason ?? "-"} " +
                           $"{(ev.Type == BiteEventType.Bite ? F(ev.Window) : "-")} " +
                           $"{(ev.Type == BiteEventType.Hooked ? F(ev.Quality) : "-")}");
                if (ev.Type == BiteEventType.Bite) struckAt = tick + (int)MathUtil.JsRound(0.18 / dt);
            }
            if (events.Count >= 40) break;
        }

        foreach (var e in events) Say(e);
        var bt = bs.GetTelemetry();
        Say($"biteend {L(bt.State)} {F(bt.Attraction)} {F(bt.Presentation)} {F(bt.Cooldown)} {bt.NibblesLeft}");
    }

    /* ------------------------------------------------------------ 7. fight */

    private static void SectionFishAi()
    {
        Say("# section fishai");
        foreach (var id in new[] { "toman", "kelah", "tilapia", "ranting" })
        {
            var sp = _species.Get(id);
            var rr = new Rng(Rng.HashSeed($"fight:{id}"));
            var roll = CatchTable.RollFish(rr, _species, sp, sizeBias: 0.5, luck: 0.1);
            var fish = new HookedFish(roll, _species, new Rng(Rng.HashSeed($"agent:{id}")),
                hookQuality: 0.8, startDist: 16, startDepth: 1.4, startLateral: 2.0);

            Say($"fishinit {id} {F(fish.Strength)} {F(fish.StaminaMax)} {F(fish.MaxForce)} {F(fish.StateDuration)}");

            const double dt = 1.0 / 120.0;
            var structures = new List<SnagPoint>
            {
                new SnagPoint { X = 3.0, Z = 9.0, R = 1.4, Kind = "timber" },
            };
            int stateChanges = 0;
            double hookShock = 0;

            for (int tick = 0; tick < 2400; tick++)
            {
                // A scripted angler: steady pressure with a periodic pump, so the
                // fish gets to see both a tight line and a slack one.
                double t = tick * dt;
                double tension = 26 + 18 * Math.Sin(t * 0.7) + 8 * Math.Sin(t * 2.9);
                var r = fish.Update(dt, new HookedFish.Ctx
                {
                    Tension = tension, LoadFrac = tension / 133.4,
                    Structures = structures, MaxDepth = 6.5,
                });

                for (int k = 0; k < r.Events.Count; k++)
                {
                    if (r.Events[k].Type == FightEventType.State) stateChanges++;
                    if (r.Events[k].Type == FightEventType.HookShock) hookShock += r.Events[k].Amount;
                }

                if (tick % 400 == 0)
                {
                    Say($"fish {id} {tick} {F(r.Pull)} {F(r.Stamina)} {F(r.Dist)} {F(r.Depth)} " +
                        $"{F(r.Lateral)} {L(r.State)} {(r.OnStructure ? 1 : 0)}");
                }
            }

            Say($"fishend {id} {stateChanges} {F(hookShock)} {fish.JumpsMade} {F(fish.Elapsed)}");
        }
    }
}
