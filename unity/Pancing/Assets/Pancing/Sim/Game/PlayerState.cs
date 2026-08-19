using System;
using System.Collections.Generic;

namespace Pancing.Sim
{
    /// <summary>Best catch for one species.</summary>
    public sealed class RecordEntry
    {
        public double LengthCm, MassKg;
        public bool Trophy;
        public long At;
        public string Spot;
    }

    public sealed class Quest
    {
        public string Id, Name, Desc;
        public double RewardMoney, RewardXp;
        public Func<PlayerStats, bool> Check;
        public bool Done;
        public long At;
    }

    /// <summary>Everything the quest log and the stats panel read.</summary>
    public sealed class PlayerStats
    {
        public int Casts, Bites, HookedCount, Landed, Lost, Snaps, Spooked, Missed, Junk, Trophies;
        public int LandedStreak, BestStreak;
        public double HeaviestKg, LongestCm;
        public int BestRarityOrder;
        public double TotalMassKg, TotalEarned, PlaySeconds;
        public readonly Dictionary<string, int> ByPhase = new Dictionary<string, int>();
        public readonly Dictionary<string, int> BySpecies = new Dictionary<string, int>();
        public readonly Dictionary<string, int> BySpot = new Dictionary<string, int>();
        /// <summary>Species with a record entry. Set by PlayerState so quests can read it.</summary>
        public int SpeciesRecorded;
        public bool HasKelahRecord;

        public int Phase(string id) => ByPhase.TryGetValue(id, out int n) ? n : 0;
    }

    /// <summary>
    /// Player state: progression, wallet, inventory, records, quests, save/load.
    /// A port of web/src/game/state.js.
    ///
    /// One store, one shape, one save. Every mutation goes through a method that
    /// emits an event, so the UI is a pure subscriber and never polls. The save is
    /// versioned with forward migrations, because a fishing game lives or dies on
    /// whether last week's record book survives this week's patch.
    /// </summary>
    public sealed class PlayerState
    {
        public const string SaveKey = "pancing.save.v1";
        public const int SaveVersion = 3;

        private readonly EventBus _bus;
        private readonly GearDb _gearDb;
        private readonly SpeciesDb _speciesDb;
        private readonly SpotDb _spotDb;

        public int Level = 1;
        public double Xp;
        public double Money = 120;
        public string SpotId = "kolam";
        public readonly Dictionary<string, string> Equipped = new Dictionary<string, string>();
        public readonly Dictionary<string, List<string>> Owned = new Dictionary<string, List<string>>();
        /// <summary>Consumable bait counts. Non-consumables are absent from this map.</summary>
        public readonly Dictionary<string, double> Stock = new Dictionary<string, double>();
        public readonly List<string> UnlockedSpots = new List<string>();
        public readonly Dictionary<string, RecordEntry> Records = new Dictionary<string, RecordEntry>();
        public readonly PlayerStats Stats = new PlayerStats();
        public readonly List<Quest> Quests = new List<Quest>();
        public long CreatedAt;

        // settings
        public bool SoundOn = true;
        public string Quality = "high";

        public PlayerState(EventBus bus, GearDb gearDb, SpeciesDb speciesDb, SpotDb spotDb)
        {
            _bus = bus;
            _gearDb = gearDb;
            _speciesDb = speciesDb;
            _spotDb = spotDb;
            BuildQuests();
            Reset();
        }

        /// <summary>Level curve: gentle at first, then a steady grind that never walls.</summary>
        public static int XpForLevel(int level) => (int)MathUtil.JsRound(60 * Math.Pow(level, 1.62));

        public static int TotalXpForLevel(int level)
        {
            int sum = 0;
            for (int i = 1; i < level; i++) sum += XpForLevel(i);
            return sum;
        }

        private void BuildQuests()
        {
            Quests.Clear();
            Quests.Add(new Quest { Id = "first_fish", Name = "Tarikan Pertama", Desc = "Daratkan ikan pertama anda.", RewardMoney = 50, RewardXp = 30, Check = s => s.Landed >= 1 });
            Quests.Add(new Quest { Id = "five_species", Name = "Pengumpul", Desc = "Daratkan 5 spesies berbeza.", RewardMoney = 200, RewardXp = 120, Check = s => s.SpeciesRecorded >= 5 });
            Quests.Add(new Quest { Id = "kilo_club", Name = "Kelab Sekilo", Desc = "Daratkan ikan melebihi 1.0 kg.", RewardMoney = 150, RewardXp = 90, Check = s => s.HeaviestKg >= 1.0 });
            Quests.Add(new Quest { Id = "no_snap", Name = "Tangan Halus", Desc = "Daratkan 10 ikan tanpa tali putus.", RewardMoney = 300, RewardXp = 180, Check = s => s.LandedStreak >= 10 });
            Quests.Add(new Quest { Id = "night_owl", Name = "Burung Hantu", Desc = "Daratkan 5 ikan pada waktu malam.", RewardMoney = 250, RewardXp = 150, Check = s => s.Phase("night") >= 5 });
            Quests.Add(new Quest { Id = "rare_hunter", Name = "Pemburu Sukar", Desc = "Daratkan satu ikan gred Sukar atau lebih.", RewardMoney = 400, RewardXp = 260, Check = s => s.BestRarityOrder >= 3 });
            Quests.Add(new Quest { Id = "trophy", Name = "Piala", Desc = "Daratkan seekor ikan trofi.", RewardMoney = 600, RewardXp = 400, Check = s => s.Trophies >= 1 });
            Quests.Add(new Quest { Id = "legend", Name = "Legenda Sungai", Desc = "Daratkan Kelah Merah.", RewardMoney = 3000, RewardXp = 1500, Check = s => s.HasKelahRecord });
        }

        public void Reset()
        {
            Level = 1; Xp = 0; Money = 120;
            SpotId = _spotDb.All.Count > 0 ? _spotDb.All[0].Id : "kolam";
            CreatedAt = Now();

            Equipped.Clear();
            Owned.Clear();
            Stock.Clear();
            UnlockedSpots.Clear();
            Records.Clear();

            foreach (var slot in GearDb.Slots)
            {
                string starter = _gearDb.StarterKit.TryGetValue(slot, out string s) ? s : null;
                if (starter == null)
                {
                    var list = _gearDb.Slot(slot);
                    starter = list.Count > 0 ? list[0].Id : null;
                }
                if (starter == null) continue;
                Equipped[slot] = starter;
                Owned[slot] = new List<string> { starter };
            }

            // The starter worm never runs out; everything else is counted.
            if (Equipped.TryGetValue("lure", out string lure)) Stock[lure] = double.PositiveInfinity;

            UnlockedSpots.Add(SpotId);
            foreach (var q in Quests) { q.Done = false; q.At = 0; }

            ResetStats();
        }

        private void ResetStats()
        {
            var s = Stats;
            s.Casts = s.Bites = s.HookedCount = s.Landed = s.Lost = s.Snaps = 0;
            s.Spooked = s.Missed = s.Junk = s.Trophies = 0;
            s.LandedStreak = s.BestStreak = 0;
            s.HeaviestKg = s.LongestCm = 0;
            s.BestRarityOrder = 0;
            s.TotalMassKg = s.TotalEarned = s.PlaySeconds = 0;
            s.ByPhase.Clear(); s.BySpecies.Clear(); s.BySpot.Clear();
            s.SpeciesRecorded = 0;
            s.HasKelahRecord = false;
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /* --- derived --------------------------------------------------------- */

        public Spot Spot => _spotDb.Get(SpotId);

        /// <summary>Resolved gear records for the physics layer.</summary>
        public GearSet Gear() => _gearDb.Resolve(
            Equipped.TryGetValue("rod", out var r) ? r : null,
            Equipped.TryGetValue("reel", out var e) ? e : null,
            Equipped.TryGetValue("line", out var l) ? l : null,
            Equipped.TryGetValue("lure", out var u) ? u : null);

        public GearItem Equip(string slot) =>
            Equipped.TryGetValue(slot, out string id) ? _gearDb.Get(id) : null;

        public struct XpProgress { public double Current; public int Need; public double Pct; }

        public XpProgress GetXpProgress()
        {
            int need = XpForLevel(Level);
            return new XpProgress { Current = Xp, Need = need, Pct = MathUtil.Clamp01(Xp / need) };
        }

        /// <summary>Luck rises slowly with level; it only ever widens the good tail.</summary>
        public double Luck() => MathUtil.Clamp01((Level - 1) * 0.028);

        public bool Owns(string id)
        {
            var item = _gearDb.Get(id);
            return item != null && Owned.TryGetValue(item.Slot, out var list) && list.Contains(id);
        }

        public double StockOf(string id)
        {
            var item = _gearDb.Get(id);
            if (item == null || !item.Consumable) return double.PositiveInfinity;
            return Stock.TryGetValue(id, out double n) ? n : 0;
        }

        /* --- mutations -------------------------------------------------------- */

        public struct MoneyChange { public double Money, Delta; public string Reason; }

        public double AddMoney(double amount, string reason = "")
        {
            Money = Math.Max(0, Money + amount);
            if (amount > 0) Stats.TotalEarned += amount;
            _bus?.Emit(EV.Money, new MoneyChange { Money = Money, Delta = amount, Reason = reason });
            return Money;
        }

        public struct LevelUpInfo { public int Level; public List<GearItem> Unlocked; }

        public int AddXp(double amount)
        {
            Xp += amount;
            _bus?.Emit(EV.XpGain, GetXpProgress());
            int leveled = 0;
            while (Xp >= XpForLevel(Level))
            {
                Xp -= XpForLevel(Level);
                Level++;
                leveled++;
                _bus?.Emit(EV.LevelUp, new LevelUpInfo { Level = Level, Unlocked = NewlyUnlocked() });
            }
            if (leveled > 0) CheckSpotUnlocks();
            return leveled;
        }

        /// <summary>Gear that just became purchasable at the current level.</summary>
        public List<GearItem> NewlyUnlocked()
        {
            var outv = new List<GearItem>();
            foreach (var item in _gearDb.All) if (item.Level == Level) outv.Add(item);
            return outv;
        }

        public void CheckSpotUnlocks()
        {
            foreach (var s in _spotDb.All)
            {
                if (s.Level <= Level && !UnlockedSpots.Contains(s.Id))
                {
                    UnlockedSpots.Add(s.Id);
                    _bus?.Emit(EV.Unlock, s);
                }
            }
        }

        public struct BuyResult { public bool Ok; public string Reason; public GearItem Item; public double Cost, Need; }

        public BuyResult Buy(string id, int qty = 1)
        {
            var item = _gearDb.Get(id);
            if (item == null) return new BuyResult { Reason = "unknown" };
            if (item.Level > Level) return new BuyResult { Reason = "level", Need = item.Level };

            bool isRestock = item.Consumable && Owns(id);
            double cost = item.Price * (isRestock ? qty : 1);
            if (cost > Money) return new BuyResult { Reason = "money", Need = cost };

            AddMoney(-cost, $"buy:{id}");
            if (!Owns(id))
            {
                if (!Owned.TryGetValue(item.Slot, out var list))
                {
                    list = new List<string>();
                    Owned[item.Slot] = list;
                }
                list.Add(id);
            }
            if (item.Consumable)
            {
                double current = Stock.TryGetValue(id, out double c) ? c : 0;
                double add = double.IsPositiveInfinity(item.Stock) ? double.PositiveInfinity : item.Stock * qty;
                Stock[id] = double.IsPositiveInfinity(current) || double.IsPositiveInfinity(add)
                    ? double.PositiveInfinity
                    : current + add;
            }
            var result = new BuyResult { Ok = true, Item = item, Cost = cost };
            _bus?.Emit(EV.GearBuy, result);
            return result;
        }

        public bool EquipItem(string id)
        {
            var item = _gearDb.Get(id);
            if (item == null || !Owns(id)) return false;
            if (item.Consumable && StockOf(id) <= 0) return false;
            Equipped[item.Slot] = id;
            _bus?.Emit(EV.GearEquip, item);
            return true;
        }

        /// <summary>Bait is spent per cast, not per catch. Falls back to worms when empty.</summary>
        public bool ConsumeBait()
        {
            if (!Equipped.TryGetValue("lure", out string id)) return true;
            var item = _gearDb.Get(id);
            if (item == null || !item.Consumable) return true;

            double have = Stock.TryGetValue(id, out double n) ? n : 0;
            if (double.IsPositiveInfinity(have)) return true;
            if (have <= 0)
            {
                string fallback = _gearDb.StarterKit.TryGetValue("lure", out string f) ? f : "worm";
                Equipped["lure"] = fallback;
                _bus?.Emit(EV.LureOut, item);
                return false;
            }
            Stock[id] = have - 1;
            return true;
        }

        public struct TravelResult { public bool Ok; public string Reason; public Spot Spot; public double Need; }

        public TravelResult Travel(string spotId)
        {
            var spot = _spotDb.ById.TryGetValue(spotId, out var s) ? s : null;
            if (spot == null) return new TravelResult { Reason = "unknown" };
            if (!UnlockedSpots.Contains(spotId)) return new TravelResult { Reason = "locked", Need = spot.Level };
            if (spot.EntryFee > Money) return new TravelResult { Reason = "money", Need = spot.EntryFee };
            if (spot.EntryFee > 0) AddMoney(-spot.EntryFee, $"entry:{spotId}");
            SpotId = spotId;
            _bus?.Emit(EV.SpotChange, spot);
            return new TravelResult { Ok = true, Spot = spot };
        }

        /* --- catch recording --------------------------------------------------- */

        public struct CatchReward
        {
            public bool IsRecord;
            public int Levels;
            public List<Quest> QuestRewards;
            public double Value, Xp;
        }

        /// <summary>
        /// Record a landed fish. Returns the reward summary so the catch card can be
        /// built from one object.
        /// </summary>
        public CatchReward RecordCatch(FishRoll fish, double value, double xp, string phase, bool keep = true)
        {
            var s = Stats;
            var sp = fish.Species;

            s.Landed++;
            s.LandedStreak++;
            s.BestStreak = Math.Max(s.BestStreak, s.LandedStreak);
            s.TotalMassKg = MathUtil.RoundTo(s.TotalMassKg + fish.MassKg, 1000);
            Bump(s.BySpecies, sp.Id);
            Bump(s.BySpot, SpotId);
            if (!string.IsNullOrEmpty(phase)) Bump(s.ByPhase, phase);
            if (sp.RarityId == "junk") s.Junk++;
            if (fish.Trophy) s.Trophies++;
            s.HeaviestKg = Math.Max(s.HeaviestKg, fish.MassKg);
            s.LongestCm = Math.Max(s.LongestCm, fish.LengthCm);

            var rarity = _speciesDb.RarityOf(sp);
            s.BestRarityOrder = Math.Max(s.BestRarityOrder, rarity?.Order ?? 0);

            // Record book, keyed by species and beaten on length.
            Records.TryGetValue(sp.Id, out var prev);
            bool isRecord = prev == null || fish.LengthCm > prev.LengthCm;
            if (isRecord)
            {
                Records[sp.Id] = new RecordEntry
                {
                    LengthCm = fish.LengthCm, MassKg = fish.MassKg, Trophy = fish.Trophy,
                    At = Now(), Spot = SpotId,
                };
                _bus?.Emit(EV.Record, sp);
            }
            RefreshRecordDerivedStats();

            if (keep && value > 0) AddMoney(value, $"catch:{sp.Id}");
            int levels = AddXp(xp);
            var questRewards = CheckQuests();

            return new CatchReward
            {
                IsRecord = isRecord, Levels = levels,
                QuestRewards = questRewards, Value = value, Xp = xp,
            };
        }

        /// <summary>
        /// Two quests ask about the record book rather than the counters, so the
        /// book's shape has to be mirrored into stats before the checks run.
        /// </summary>
        private void RefreshRecordDerivedStats()
        {
            Stats.SpeciesRecorded = Records.Count;
            Stats.HasKelahRecord = Records.ContainsKey("kelah");
        }

        private static void Bump(Dictionary<string, int> map, string key)
        {
            map[key] = map.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        public void RegisterLoss(string kind)
        {
            var s = Stats;
            s.LandedStreak = 0;
            if (kind == "snap") s.Snaps++;
            else if (kind == "spooked") s.Spooked++;
            else if (kind == "missed") s.Missed++;
            else s.Lost++;
        }

        public List<Quest> CheckQuests()
        {
            var done = new List<Quest>();
            foreach (var q in Quests)
            {
                if (q.Done) continue;
                if (q.Check == null || !q.Check(Stats)) continue;
                q.Done = true;
                q.At = Now();
                if (q.RewardMoney > 0) AddMoney(q.RewardMoney, $"quest:{q.Id}");
                if (q.RewardXp > 0) AddXp(q.RewardXp);
                _bus?.Emit(EV.QuestDone, q);
                done.Add(q);
            }
            return done;
        }

        public struct RecordRow { public string Id; public Species Species; public RecordEntry Entry; }

        public List<RecordRow> RecordBook()
        {
            var rows = new List<RecordRow>();
            foreach (var kv in Records)
            {
                var sp = _speciesDb.Get(kv.Key);
                if (sp == null) continue;
                rows.Add(new RecordRow { Id = kv.Key, Species = sp, Entry = kv.Value });
            }
            rows.Sort((a, b) =>
                (b.Species.Value * b.Entry.MassKg).CompareTo(a.Species.Value * a.Entry.MassKg));
            return rows;
        }

        /* --- persistence -------------------------------------------------------- */

        public string ToJson()
        {
            var root = Json.Object_()
                .Set("version", SaveVersion)
                .Set("createdAt", CreatedAt)
                .Set("level", Level)
                .Set("xp", Xp)
                .Set("money", Money)
                .Set("spot", SpotId);

            var equipped = Json.Object_();
            foreach (var kv in Equipped) equipped.Set(kv.Key, kv.Value);
            root.Set("equipped", equipped);

            var owned = Json.Object_();
            foreach (var kv in Owned)
            {
                var arr = Json.Array_();
                foreach (var id in kv.Value) arr.Add(Json.Of(id));
                owned.Set(kv.Key, arr);
            }
            root.Set("owned", owned);

            var stock = Json.Object_();
            foreach (var kv in Stock) stock.Set(kv.Key, kv.Value);
            root.Set("stock", stock);

            var spots = Json.Array_();
            foreach (var id in UnlockedSpots) spots.Add(Json.Of(id));
            root.Set("unlockedSpots", spots);

            var records = Json.Object_();
            foreach (var kv in Records)
            {
                records.Set(kv.Key, Json.Object_()
                    .Set("lengthCm", kv.Value.LengthCm)
                    .Set("massKg", kv.Value.MassKg)
                    .Set("trophy", kv.Value.Trophy)
                    .Set("at", kv.Value.At)
                    .Set("spot", kv.Value.Spot));
            }
            root.Set("records", records);

            var quests = Json.Object_();
            foreach (var q in Quests) if (q.Done) quests.Set(q.Id, q.At);
            root.Set("quests", quests);

            var st = Json.Object_()
                .Set("casts", Stats.Casts).Set("bites", Stats.Bites)
                .Set("hooked", Stats.HookedCount).Set("landed", Stats.Landed)
                .Set("lost", Stats.Lost).Set("snaps", Stats.Snaps)
                .Set("spooked", Stats.Spooked).Set("missed", Stats.Missed)
                .Set("junk", Stats.Junk).Set("trophies", Stats.Trophies)
                .Set("landedStreak", Stats.LandedStreak).Set("bestStreak", Stats.BestStreak)
                .Set("heaviestKg", Stats.HeaviestKg).Set("longestCm", Stats.LongestCm)
                .Set("bestRarityOrder", Stats.BestRarityOrder)
                .Set("totalMassKg", Stats.TotalMassKg).Set("totalEarned", Stats.TotalEarned)
                .Set("playSeconds", Stats.PlaySeconds);
            st.Set("byPhase", IntMap(Stats.ByPhase));
            st.Set("bySpecies", IntMap(Stats.BySpecies));
            st.Set("bySpot", IntMap(Stats.BySpot));
            root.Set("stats", st);

            root.Set("settings", Json.Object_().Set("sound", SoundOn).Set("quality", Quality));
            return root.ToString();
        }

        private static Json IntMap(Dictionary<string, int> map)
        {
            var o = Json.Object_();
            foreach (var kv in map) o.Set(kv.Key, kv.Value);
            return o;
        }

        /// <summary>
        /// Load a save, applying forward migrations. Unknown ids are dropped rather
        /// than crashing — a renamed lure must not brick last week's record book.
        /// Returns false (and leaves a fresh state) if the payload is unreadable.
        /// </summary>
        public bool FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            if (!Json.TryParse(json, out var root, out string err))
            {
                LoadError = err;
                Reset();
                return false;
            }

            Reset();
            int v = root["version"].AsInt(1);

            CreatedAt = (long)root["createdAt"].AsDouble(Now());
            Level = Math.Max(1, root["level"].AsInt(1));
            Xp = root["xp"].AsDouble();
            Money = root["money"].AsDouble(120);

            foreach (var kv in root["equipped"].Fields) Equipped[kv.Key] = kv.Value.AsString();

            foreach (var kv in root["owned"].Fields)
            {
                var list = new List<string>();
                foreach (var it in kv.Value.Items)
                {
                    string id = it.AsString();
                    if (id != null) list.Add(id);
                }
                Owned[kv.Key] = list;
            }

            Stock.Clear();
            foreach (var kv in root["stock"].Fields) Stock[kv.Key] = kv.Value.AsDouble();

            UnlockedSpots.Clear();
            foreach (var it in root["unlockedSpots"].Items)
            {
                string id = it.AsString();
                if (id != null) UnlockedSpots.Add(id);
            }

            Records.Clear();
            foreach (var kv in root["records"].Fields)
            {
                Records[kv.Key] = new RecordEntry
                {
                    LengthCm = kv.Value["lengthCm"].AsDouble(),
                    MassKg = kv.Value["massKg"].AsDouble(),
                    Trophy = kv.Value["trophy"].AsBool(),
                    At = (long)kv.Value["at"].AsDouble(),
                    Spot = kv.Value["spot"].AsString(SpotId),
                };
            }

            var s = root["stats"];
            Stats.Casts = s["casts"].AsInt(); Stats.Bites = s["bites"].AsInt();
            Stats.HookedCount = s["hooked"].AsInt(); Stats.Landed = s["landed"].AsInt();
            Stats.Lost = s["lost"].AsInt(); Stats.Snaps = s["snaps"].AsInt();
            Stats.Spooked = s["spooked"].AsInt(); Stats.Missed = s["missed"].AsInt();
            Stats.Junk = s["junk"].AsInt(); Stats.Trophies = s["trophies"].AsInt();
            Stats.LandedStreak = s["landedStreak"].AsInt(); Stats.BestStreak = s["bestStreak"].AsInt();
            Stats.HeaviestKg = s["heaviestKg"].AsDouble(); Stats.LongestCm = s["longestCm"].AsDouble();
            Stats.BestRarityOrder = s["bestRarityOrder"].AsInt();
            Stats.TotalMassKg = s["totalMassKg"].AsDouble(); Stats.TotalEarned = s["totalEarned"].AsDouble();
            Stats.PlaySeconds = s["playSeconds"].AsDouble();
            LoadIntMap(s["byPhase"], Stats.ByPhase);
            LoadIntMap(s["bySpecies"], Stats.BySpecies);
            LoadIntMap(s["bySpot"], Stats.BySpot);

            foreach (var q in Quests)
            {
                var got = root["quests"][q.Id];
                q.Done = !got.IsNull;
                q.At = q.Done ? (long)got.AsDouble() : 0;
            }

            SoundOn = root["settings"]["sound"].AsBool(true);
            Quality = root["settings"]["quality"].AsString("high");
            SpotId = root["spot"].AsString(SpotId);

            Migrate(v);
            return true;
        }

        private static void LoadIntMap(Json src, Dictionary<string, int> dst)
        {
            dst.Clear();
            foreach (var kv in src.Fields) dst[kv.Key] = kv.Value.AsInt();
        }

        public string LoadError { get; private set; }

        /// <summary>
        /// Forward migrations plus a hard integrity pass. Everything here is written
        /// to be idempotent, so replaying it on an already-current save is a no-op.
        /// </summary>
        private void Migrate(int fromVersion)
        {
            if (fromVersion < 3)
            {
                // v3 split consumable stock out of `owned` and added spot unlocks.
                if (UnlockedSpots.Count == 0 && _spotDb.All.Count > 0)
                    UnlockedSpots.Add(_spotDb.All[0].Id);

                foreach (var item in _gearDb.Slot("lure"))
                {
                    if (!item.Consumable) continue;
                    if (Owned.TryGetValue("lure", out var lures) && lures.Contains(item.Id)
                        && !Stock.ContainsKey(item.Id))
                    {
                        Stock[item.Id] = item.Stock;
                    }
                }
            }

            // Drop anything that no longer exists in the data tables rather than
            // crashing on a renamed id.
            foreach (var slot in GearDb.Slots)
            {
                if (!Owned.TryGetValue(slot, out var list)) { list = new List<string>(); Owned[slot] = list; }
                list.RemoveAll(id => _gearDb.Get(id) == null || _gearDb.Get(id).Slot != slot);
                if (list.Count == 0)
                {
                    string starter = _gearDb.StarterKit.TryGetValue(slot, out string st) ? st : null;
                    if (starter == null)
                    {
                        var all = _gearDb.Slot(slot);
                        starter = all.Count > 0 ? all[0].Id : null;
                    }
                    if (starter != null) list.Add(starter);
                }

                string equipped = Equipped.TryGetValue(slot, out string eq) ? eq : null;
                if (equipped == null || _gearDb.Get(equipped) == null || !list.Contains(equipped))
                {
                    if (list.Count > 0) Equipped[slot] = list[0];
                }
            }

            var stale = new List<string>();
            foreach (var id in Records.Keys) if (_speciesDb.Get(id) == null) stale.Add(id);
            foreach (var id in stale) Records.Remove(id);
            RefreshRecordDerivedStats();

            UnlockedSpots.RemoveAll(id => !_spotDb.ById.ContainsKey(id));
            if (UnlockedSpots.Count == 0 && _spotDb.All.Count > 0) UnlockedSpots.Add(_spotDb.All[0].Id);
            if (!_spotDb.ById.ContainsKey(SpotId) || !UnlockedSpots.Contains(SpotId))
                SpotId = UnlockedSpots.Count > 0 ? UnlockedSpots[0] : SpotId;

            CheckSpotUnlocks();
        }
    }
}
