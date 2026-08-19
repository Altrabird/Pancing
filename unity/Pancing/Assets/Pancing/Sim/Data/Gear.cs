using System.Collections.Generic;

namespace Pancing.Sim
{
    /// <summary>
    /// One shop item, whatever slot it belongs to. The JavaScript build keeps four
    /// separate tables and then flattens them into one id-keyed map; the exporter
    /// does that flattening for us and stamps each item with its slot, so nothing
    /// here has to know that "the fourth table is lures".
    ///
    /// Fields that do not apply to a slot are simply zero — a line has no
    /// CastPower. That is deliberately not modelled as a class hierarchy: the shop
    /// UI, the save file and the equip logic all want one uniform record, and
    /// four subclasses would buy type-safety the game never actually uses.
    /// </summary>
    public sealed class GearItem
    {
        public string Id, Name, Desc, Slot;
        public double Price;
        public int Level;
        public bool Consumable;
        /// <summary>Units added per purchase. Infinity for items that never run out.</summary>
        public double Stock = double.PositiveInfinity;

        // rod
        public double Power, Stiffness, Length, CastPower, Sensitivity;
        public string TipColor;
        // reel
        public double Drag, Retrieve, Ratio, DragSmooth;
        // line
        public double Test, Stretch, Visibility, Abrasion;
        // lure
        public double Sink, Action, Noise, Spook, SizeBias;

        public RodSpec AsRod() => new RodSpec
        {
            Id = Id, Power = Power, Stiffness = Stiffness,
            Length = Length, CastPower = CastPower, Sensitivity = Sensitivity,
        };

        public ReelSpec AsReel() => new ReelSpec
        {
            Id = Id, Drag = Drag, Retrieve = Retrieve, DragSmooth = DragSmooth,
        };

        public LineSpec AsLine() => new LineSpec
        {
            Id = Id, Test = Test, Stretch = Stretch,
            Abrasion = Abrasion, Visibility = Visibility,
        };

        public LureSpec AsLure() => new LureSpec
        {
            Id = Id, Sink = Sink, Action = Action,
            Noise = Noise, Spook = Spook, SizeBias = SizeBias,
        };
    }

    /// <summary>
    /// The loaded gear tables, from Resources/gear.json.
    /// </summary>
    public sealed class GearDb
    {
        public readonly List<GearItem> All = new List<GearItem>();
        public readonly Dictionary<string, GearItem> ById = new Dictionary<string, GearItem>();
        /// <summary>Items grouped by slot, in shop order (which is price order).</summary>
        public readonly Dictionary<string, List<GearItem>> BySlot = new Dictionary<string, List<GearItem>>();
        /// <summary>Everything the player starts with, free of charge.</summary>
        public readonly Dictionary<string, string> StarterKit = new Dictionary<string, string>();

        public static readonly string[] Slots = { "rod", "reel", "line", "lure" };

        public GearItem Get(string id) => id != null && ById.TryGetValue(id, out var g) ? g : null;

        public List<GearItem> Slot(string slot) =>
            BySlot.TryGetValue(slot, out var list) ? list : new List<GearItem>();

        /// <summary>
        /// Resolve four ids into the specs the physics wants. Any id that no longer
        /// exists falls back to the starter item rather than throwing — a renamed
        /// lure should not brick a save.
        /// </summary>
        public GearSet Resolve(string rodId, string reelId, string lineId, string lureId)
        {
            GearItem Pick(string id, string slot)
            {
                var item = Get(id);
                if (item != null && item.Slot == slot) return item;
                if (StarterKit.TryGetValue(slot, out string starter))
                {
                    var s = Get(starter);
                    if (s != null) return s;
                }
                var list = Slot(slot);
                return list.Count > 0 ? list[0] : new GearItem { Id = "?", Slot = slot };
            }

            return new GearSet
            {
                Rod = Pick(rodId, "rod").AsRod(),
                Reel = Pick(reelId, "reel").AsReel(),
                Line = Pick(lineId, "line").AsLine(),
                Lure = Pick(lureId, "lure").AsLure(),
            };
        }

        public static GearDb Load(string json)
        {
            var root = Json.Parse(json);
            var db = new GearDb();

            foreach (var kv in root["starterKit"].Fields)
            {
                db.StarterKit[kv.Key] = kv.Value.AsString();
            }

            foreach (var g in root["items"].Items)
            {
                var item = new GearItem
                {
                    Id = g["id"].AsString(),
                    Name = g["name"].AsString(),
                    Desc = g["desc"].AsString(""),
                    Slot = g["slot"].AsString(),
                    Price = g["price"].AsDouble(),
                    Level = g["level"].AsInt(1),
                    Consumable = g["consumable"].AsBool(false),
                    // AsDouble understands the exporter's "__Inf__" encoding, which
                    // is how a bottomless worm supply survives the round trip.
                    Stock = g.Has("stock") ? g["stock"].AsDouble(double.PositiveInfinity)
                                           : double.PositiveInfinity,

                    Power = g["power"].AsDouble(),
                    Stiffness = g["stiffness"].AsDouble(),
                    Length = g["length"].AsDouble(),
                    CastPower = g["castPower"].AsDouble(),
                    Sensitivity = g["sensitivity"].AsDouble(),
                    TipColor = g["tipColor"].AsString("#888888"),

                    Drag = g["drag"].AsDouble(),
                    Retrieve = g["retrieve"].AsDouble(),
                    Ratio = g["ratio"].AsDouble(),
                    DragSmooth = g["dragSmooth"].AsDouble(),

                    Test = g["test"].AsDouble(),
                    Stretch = g["stretch"].AsDouble(),
                    Visibility = g["visibility"].AsDouble(),
                    Abrasion = g["abrasion"].AsDouble(),

                    Sink = g["sink"].AsDouble(),
                    Action = g["action"].AsDouble(),
                    Noise = g["noise"].AsDouble(),
                    Spook = g["spook"].AsDouble(),
                    SizeBias = g["sizeBias"].AsDouble(),
                };

                if (string.IsNullOrEmpty(item.Id)) continue;
                db.All.Add(item);
                db.ById[item.Id] = item;
                if (!db.BySlot.TryGetValue(item.Slot, out var list))
                {
                    list = new List<GearItem>();
                    db.BySlot[item.Slot] = list;
                }
                list.Add(item);
            }

            return db;
        }
    }
}
