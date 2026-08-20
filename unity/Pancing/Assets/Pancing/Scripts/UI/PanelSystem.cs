using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Pancing.Controls;
using Pancing.Core;
using Pancing.Sim;

namespace Pancing.UI
{
    public enum PanelTab { None, Shop, Bag, Travel }

    /// <summary>
    /// The shop and the bag: the two screens where the player spends what they
    /// caught and decides what to fight the next fish with.
    ///
    /// Both are views over PlayerState — every button calls Buy() or EquipItem()
    /// and then does nothing else. The list rebuilds off the resulting event
    /// rather than off the click, so the UI can never show a purchase that the
    /// simulation refused, and a level-up that unlocks four items updates the shop
    /// whether or not the shop was the thing that caused it.
    ///
    /// Rows are absolutely positioned inside a scroll view rather than driven by
    /// layout groups. With fixed-height rows that is fewer moving parts, and it
    /// avoids the standing argument between ContentSizeFitter and ScrollRect about
    /// who owns the content height.
    /// </summary>
    public sealed class PanelSystem : MonoBehaviour
    {
        private const float ShopRowH = 82f;
        private const float BagRowH = 64f;
        private const float TravelRowH = 104f;
        private const float HeaderH = 34f;

        private static readonly string[] SlotOrder = { "rod", "reel", "line", "lure" };
        private static readonly Dictionary<string, string> SlotNames = new Dictionary<string, string>
        {
            { "rod", "JORAN" }, { "reel", "REEL" }, { "line", "TALI" }, { "lure", "UMPAN" },
        };

        private InputService _input;
        private Hud _hud;

        private GameObject _overlay;
        private GameObject _openBar;
        private Text _title, _moneyText, _hintText;
        private RectTransform _listContent;
        private ScrollRect _scroll;
        private Image _shopTab, _bagTab, _travelTab;
        private Text _shopTabText, _bagTabText, _travelTabText;

        private PanelTab _tab = PanelTab.None;
        private readonly List<GameObject> _rows = new List<GameObject>();

        public bool IsOpen => _tab != PanelTab.None;

        public static PanelSystem Create(Transform parent, InputService input, Hud hud)
        {
            var go = new GameObject("Panels");
            go.transform.SetParent(parent, false);
            var p = go.AddComponent<PanelSystem>();
            p._input = input;
            p._hud = hud;
            p.Build();
            return p;
        }

        /* --- construction ------------------------------------------------------ */

        private void Build()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above the HUD: a modal that the tension meter draws on top of is not
            // a modal.
            canvas.sortingOrder = 20;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();

            BuildOpenButtons();
            BuildWindow();
            Close();
        }

        /// <summary>Always-visible tabs, so the shop is reachable without a keyboard.</summary>
        private void BuildOpenButtons()
        {
            var bar = UiKit.Rect("OpenBar", transform, new Vector2(1, 1), new Vector2(1, 1),
                                 new Vector2(-344, -100), new Vector2(-16, -54));
            _openBar = bar.gameObject;

            UiKit.Button("OpenShop", bar, "KEDAI (B)", 14,
                new Vector2(0f, 0), new Vector2(0.333f, 1), new Vector2(0, 0), new Vector2(-3, 0),
                () => Open(PanelTab.Shop), out _);
            UiKit.Button("OpenBag", bar, "BEG (I)", 14,
                new Vector2(0.333f, 0), new Vector2(0.667f, 1), new Vector2(3, 0), new Vector2(-3, 0),
                () => Open(PanelTab.Bag), out _);
            UiKit.Button("OpenTravel", bar, "JALAN (T)", 14,
                new Vector2(0.667f, 0), new Vector2(1f, 1), new Vector2(3, 0), new Vector2(0, 0),
                () => Open(PanelTab.Travel), out _);
        }

        private void BuildWindow()
        {
            _overlay = UiKit.Rect("Overlay", transform, Vector2.zero, Vector2.one,
                                  Vector2.zero, Vector2.zero).gameObject;
            // A real Image with raycastTarget on: this is what stops a tap meant for
            // a BUY button from also charging a cast behind the window.
            var dim = _overlay.AddComponent<Image>();
            dim.color = new Color(0f, 0f, 0f, 0.62f);

            var win = UiKit.Rect("Window", _overlay.transform,
                                 new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                                 new Vector2(-470, -300), new Vector2(470, 300));
            var winBg = win.gameObject.AddComponent<Image>();
            winBg.color = UiKit.PanelSolid;

            _title = UiKit.Label("Title", win, "KEDAI", 24, TextAnchor.MiddleLeft,
                new Vector2(0, 1), new Vector2(0.5f, 1), new Vector2(22, -54), new Vector2(0, -14));

            _moneyText = UiKit.Label("Money", win, "RM 0", 22, TextAnchor.MiddleRight,
                new Vector2(0.5f, 1), new Vector2(1, 1), new Vector2(0, -54), new Vector2(-78, -14));
            _moneyText.color = UiKit.Gold;

            UiKit.Button("Close", win, "✕", 22,
                new Vector2(1, 1), new Vector2(1, 1), new Vector2(-62, -56), new Vector2(-18, -14),
                Close, out _);

            _shopTab = UiKit.Button("TabShop", win, "KEDAI", 16,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(22, -94), new Vector2(142, -60),
                () => Open(PanelTab.Shop), out _shopTabText);
            _bagTab = UiKit.Button("TabBag", win, "BEG", 16,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(150, -94), new Vector2(270, -60),
                () => Open(PanelTab.Bag), out _bagTabText);
            _travelTab = UiKit.Button("TabTravel", win, "JALAN", 16,
                new Vector2(0, 1), new Vector2(0, 1), new Vector2(278, -94), new Vector2(398, -60),
                () => Open(PanelTab.Travel), out _travelTabText);

            _listContent = UiKit.ScrollList("List", win,
                new Vector2(0, 0), new Vector2(1, 1),
                new Vector2(18, 46), new Vector2(-18, -100), out _scroll);

            _hintText = UiKit.Label("Hint", win, "", 13, TextAnchor.MiddleLeft,
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(22, 12), new Vector2(-22, 40));
            _hintText.color = UiKit.InkDim;
        }

        private void Start()
        {
            // Rebuild off the simulation's events, never off the click that caused
            // them — see the class comment.
            Game.Bus.On(EV.GearBuy, _ => Refresh());
            Game.Bus.On(EV.GearEquip, _ => Refresh());
            Game.Bus.On(EV.Money, _ => Refresh());
            Game.Bus.On(EV.LevelUp, _ => Refresh());
            Game.Bus.On(EV.LureOut, _ => Refresh());
            Game.Bus.On(EV.SpotChange, _ => Refresh());
            Game.Bus.On(EV.Unlock, _ => Refresh());
        }

        /* --- open / close -------------------------------------------------------- */

        public void Open(PanelTab tab)
        {
            // Not with a fish on. Equipping reconfigures the rod, the reel and the
            // line underneath a live tension solve, so a player could swap to a
            // 300 N clutch halfway through losing a Toman and simply crank it in.
            // Refusing is both simpler and more honest than trying to make the
            // swap physically meaningful.
            if (Game.Fishing != null && Game.Fishing.Phase == GameState.Fight)
            {
                _hud?.Toast("Ada ikan di hujung tali — selesaikan dahulu.", "warn");
                return;
            }

            _tab = tab;
            _overlay.SetActive(true);
            _openBar.SetActive(false);
            _input.Blocked = true;
            // Releasing the cast button is an edge the world input watches for; if a
            // panel opens mid-charge the charge would hang forever otherwise.
            _input.TouchCastHeld = false;
            _input.TouchReelHeld = false;

            _title.text = tab switch
            {
                PanelTab.Shop => "KEDAI",
                PanelTab.Bag => "BEG",
                _ => "JALAN",
            };
            _hintText.text = tab switch
            {
                PanelTab.Shop => "Umpan boleh dibeli berulang kali. Alat yang lebih kuat perlukan tahap lebih tinggi.",
                PanelTab.Bag => "Pilih alat untuk dipakai. Umpan habis akan bertukar kembali kepada cacing.",
                _ => "Air lebih dalam bermakna ikan lebih besar — dan tali anda perlu tahan.",
            };

            Color on = new Color(0.20f, 0.36f, 0.34f, 0.98f);
            Color off = new Color(0.11f, 0.17f, 0.19f, 0.95f);
            _shopTab.color = tab == PanelTab.Shop ? on : off;
            _bagTab.color = tab == PanelTab.Bag ? on : off;
            _travelTab.color = tab == PanelTab.Travel ? on : off;

            Refresh();
        }

        public void Close()
        {
            _tab = PanelTab.None;
            if (_overlay != null) _overlay.SetActive(false);
            if (_openBar != null) _openBar.SetActive(true);
            if (_input != null) _input.Blocked = false;
        }

        public void Toggle(PanelTab tab)
        {
            if (_tab == tab) Close(); else Open(tab);
        }

        private void Update()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.B)) Toggle(PanelTab.Shop);
            if (UnityEngine.Input.GetKeyDown(KeyCode.I)) Toggle(PanelTab.Bag);
            if (UnityEngine.Input.GetKeyDown(KeyCode.T)) Toggle(PanelTab.Travel);
            if (IsOpen && UnityEngine.Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        /* --- list building -------------------------------------------------------- */

        private void Refresh()
        {
            if (!IsOpen) return;

            foreach (var row in _rows) Destroy(row);
            _rows.Clear();

            _moneyText.text = $"RM {Game.State.Money:0}";

            float y = 0f;

            if (_tab == PanelTab.Travel)
            {
                foreach (var spot in Game.Spots.All) AddTravelRow(spot, ref y);
            }
            else
            {
                if (_tab == PanelTab.Bag) AddAssistRow(ref y);

                foreach (var slot in SlotOrder)
                {
                    var items = ItemsFor(slot);
                    if (items.Count == 0) continue;

                    AddHeader(SlotNames[slot], ref y);
                    foreach (var item in items)
                    {
                        if (_tab == PanelTab.Shop) AddShopRow(item, ref y);
                        else AddBagRow(item, ref y);
                    }
                    y += 8f;
                }
            }

            UiKit.SetContentHeight(_listContent, y);
        }

        private List<GearItem> ItemsFor(string slot)
        {
            var all = Game.Gear.Slot(slot);
            if (_tab == PanelTab.Shop) return all;

            var owned = new List<GearItem>();
            foreach (var item in all) if (Game.State.Owns(item.Id)) owned.Add(item);
            return owned;
        }

        private void AddHeader(string text, ref float y)
        {
            var rt = UiKit.Rect("Header", _listContent, new Vector2(0, 1), new Vector2(1, 1),
                                new Vector2(0, -y - HeaderH), new Vector2(0, -y));
            var t = UiKit.Label("HeaderText", rt, text, 14, TextAnchor.LowerLeft,
                Vector2.zero, Vector2.one, new Vector2(10, 0), new Vector2(-10, -6));
            t.color = UiKit.InkDim;
            _rows.Add(rt.gameObject);
            y += HeaderH;
        }

        private void AddShopRow(GearItem item, ref float y)
        {
            var state = Game.State;
            bool owned = state.Owns(item.Id);
            bool locked = item.Level > state.Level;
            // The starter worm is consumable but has infinite stock, so it is owned
            // and can never be restocked. Without this it offered "BELI ×∞" — and
            // (int)double.PositiveInfinity is not a number you want on a button.
            bool bottomless = double.IsPositiveInfinity(item.Stock);
            bool restock = item.Consumable && owned && !bottomless;
            double cost = item.Price;
            bool affordable = cost <= state.Money;
            // A non-consumable you already own is not for sale again.
            bool sellable = !locked && (!owned || restock);

            var row = MakeRow(ShopRowH, ref y, locked ? 0.30f : 1f);

            UiKit.Label("Name", row, item.Name, 18, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.62f, 1), new Vector2(12, -30), new Vector2(0, -6));

            UiKit.Paragraph("Desc", row, item.Desc, 13, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.62f, 1), new Vector2(12, -52), new Vector2(0, -30))
                .color = UiKit.InkDim;

            UiKit.Label("Stats", row, StatLine(item), 13, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.72f, 1), new Vector2(12, -74), new Vector2(0, -52))
                .color = UiKit.Accent;

            string priceText = item.Price <= 0 ? "PERCUMA" : $"RM {item.Price:0}";
            var price = UiKit.Label("Price", row, priceText, 17, TextAnchor.MiddleRight,
                new Vector2(0.62f, 1), new Vector2(1, 1), new Vector2(0, -52), new Vector2(-140, -12));
            price.color = affordable ? UiKit.Gold : UiKit.Danger;

            string buyLabel;
            System.Action onClick = null;
            if (locked) buyLabel = $"Tahap {item.Level}";
            else if (restock) { buyLabel = $"BELI ×{(int)item.Stock}"; onClick = () => TryBuy(item); }
            else if (owned) buyLabel = "DIMILIKI";
            else { buyLabel = "BELI"; onClick = () => TryBuy(item); }

            var btn = UiKit.Button("Buy", row, buyLabel, 15,
                new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                new Vector2(-132, -20), new Vector2(-12, 20),
                sellable && affordable ? onClick : null, out var btnText);

            if (!sellable) { btn.color = new Color(0.10f, 0.12f, 0.13f, 0.9f); btnText.color = UiKit.InkDim; }
            else if (!affordable) { btn.color = new Color(0.22f, 0.11f, 0.11f, 0.9f); btnText.color = UiKit.Danger; }
        }

        private void AddBagRow(GearItem item, ref float y)
        {
            var state = Game.State;
            bool equipped = state.Equipped.TryGetValue(item.Slot, out string id) && id == item.Id;
            double stock = state.StockOf(item.Id);
            bool empty = item.Consumable && stock <= 0;

            var row = MakeRow(BagRowH, ref y, empty ? 0.45f : 1f);

            UiKit.Label("Name", row, item.Name, 17, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.66f, 1), new Vector2(12, -28), new Vector2(0, -6));

            UiKit.Label("Stats", row, StatLine(item), 13, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.78f, 1), new Vector2(12, -50), new Vector2(0, -28))
                .color = UiKit.Accent;

            if (item.Consumable)
            {
                string stockText = double.IsPositiveInfinity(stock) ? "∞" : $"×{stock:0}";
                var s = UiKit.Label("Stock", row, stockText, 17, TextAnchor.MiddleRight,
                    new Vector2(0.66f, 0), new Vector2(1, 1), new Vector2(0, 0), new Vector2(-140, 0));
                s.color = empty ? UiKit.Danger : UiKit.Ink;
            }

            var btn = UiKit.Button("Equip", row, equipped ? "DIPAKAI" : "PAKAI", 15,
                new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                new Vector2(-132, -18), new Vector2(-12, 18),
                (equipped || empty) ? (System.Action)null : () => TryEquip(item), out var btnText);

            if (equipped) { btn.color = new Color(0.18f, 0.36f, 0.28f, 0.95f); btnText.color = UiKit.Accent; }
            else if (empty) { btn.color = new Color(0.10f, 0.12f, 0.13f, 0.9f); btnText.color = UiKit.InkDim; }
        }

        /// <summary>
        /// The one difficulty setting: a wider hookset window.
        ///
        /// It lives at the top of the bag rather than behind a settings menu because
        /// a player who is missing every fish needs to find it while they are still
        /// annoyed, not after they have gone looking for options.
        /// </summary>
        private void AddAssistRow(ref float y)
        {
            AddHeader("BANTUAN", ref y);
            bool on = Game.State.EasyHookset;
            var row = MakeRow(BagRowH, ref y, 1f);

            UiKit.Label("Name", row, "Tempoh sentap lebih panjang", 17, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.72f, 1), new Vector2(12, -28), new Vector2(0, -6));

            UiKit.Label("Sub", row,
                on ? $"Hidup — tempoh {PlayerState.EasyHooksetScale:0.0}× lebih panjang"
                   : "Mati — tempoh sebenar, Toman hanya 0.32 s",
                13, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.78f, 1), new Vector2(12, -50), new Vector2(0, -28))
                .color = on ? UiKit.Accent : UiKit.InkDim;

            var btn = UiKit.Button("Assist", row, on ? "HIDUP" : "MATI", 15,
                new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                new Vector2(-132, -18), new Vector2(-12, 18),
                () =>
                {
                    Game.State.EasyHookset = !Game.State.EasyHookset;
                    _hud?.Toast(Game.State.EasyHookset
                        ? "Bantuan sentap dihidupkan."
                        : "Bantuan sentap dimatikan.", "good");
                    Refresh();
                }, out var btnText);

            if (on) { btn.color = new Color(0.18f, 0.36f, 0.28f, 0.95f); btnText.color = UiKit.Accent; }
            else { btn.color = new Color(0.10f, 0.12f, 0.13f, 0.9f); btnText.color = UiKit.InkDim; }

            y += 8f;
        }

        private void AddTravelRow(Spot spot, ref float y)
        {
            var state = Game.State;
            bool here = state.SpotId == spot.Id;
            bool unlocked = state.UnlockedSpots.Contains(spot.Id);
            bool affordable = spot.EntryFee <= state.Money;
            bool canGo = unlocked && affordable && !here;

            var row = MakeRow(TravelRowH, ref y, unlocked ? 1f : 0.30f);

            UiKit.Label("Name", row, spot.Name, 19, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.66f, 1), new Vector2(12, -32), new Vector2(0, -6));

            UiKit.Paragraph("Tagline", row, spot.Tagline, 13, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.66f, 1), new Vector2(12, -54), new Vector2(0, -32))
                .color = UiKit.InkDim;

            // The numbers that decide what tackle to bring. Depth sets which fish
            // are reachable, clarity drives how much a visible line costs you, and
            // snag density is how often a big fish gets to break you off.
            UiKit.Label("Stats", row, StatLineFor(spot), 13, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.78f, 1), new Vector2(12, -76), new Vector2(0, -54))
                .color = UiKit.Accent;

            UiKit.Label("Fish", row, "Ikan: " + HeadlineSpecies(spot), 13, TextAnchor.UpperLeft,
                new Vector2(0, 1), new Vector2(0.82f, 1), new Vector2(12, -98), new Vector2(0, -76))
                .color = UiKit.InkDim;

            string feeText = spot.EntryFee <= 0 ? "PERCUMA" : $"RM {spot.EntryFee:0}";
            var fee = UiKit.Label("Fee", row, feeText, 16, TextAnchor.MiddleRight,
                new Vector2(0.66f, 1), new Vector2(1, 1), new Vector2(0, -62), new Vector2(-140, -18));
            fee.color = !unlocked ? UiKit.InkDim : (affordable ? UiKit.Gold : UiKit.Danger);

            string label;
            System.Action onClick = null;
            if (here) label = "DI SINI";
            else if (!unlocked) label = $"Tahap {spot.Level}";
            else if (!affordable) label = "Duit tak cukup";
            else { label = "PERGI"; onClick = () => TryTravel(spot); }

            var btn = UiKit.Button("Go", row, label, 15,
                new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                new Vector2(-132, -20), new Vector2(-12, 20),
                onClick, out var btnText);

            if (here) { btn.color = new Color(0.18f, 0.36f, 0.28f, 0.95f); btnText.color = UiKit.Accent; }
            else if (!canGo) { btn.color = new Color(0.10f, 0.12f, 0.13f, 0.9f); btnText.color = UiKit.InkDim; }
        }

        private static string StatLineFor(Spot spot)
        {
            string clarity = spot.WaterClarity > 0.65 ? "jernih"
                           : spot.WaterClarity > 0.4 ? "sederhana" : "keruh";
            string snags = spot.SnagDensity > 0.45 ? "banyak reba"
                         : spot.SnagDensity > 0.25 ? "ada reba" : "sedikit reba";
            return $"Dalam {spot.MaxDepth:0.0} m · Air {clarity} · {snags}";
        }

        /// <summary>
        /// The four best fish here: rarest first, commonness only breaking ties.
        ///
        /// Ranking by commonness instead — which is what the catch table weights
        /// actually describe — made every location advertise its most forgettable
        /// residents, and put Ikan Keli at the top of all three. Nobody pays a
        /// RM 180 entry fee for Ikan Keli. They pay it because Toman live there,
        /// so that is what the row has to say. Junk is filtered out entirely.
        /// </summary>
        private static string HeadlineSpecies(Spot spot)
        {
            var scored = new List<KeyValuePair<string, double>>();
            foreach (var kv in spot.Pool)
            {
                var sp = Game.Species.Get(kv.Key);
                if (sp == null || sp.RarityId == "junk") continue;
                int order = Game.Species.RarityOf(sp)?.Order ?? 0;
                // Rarity dominates; the spot-weighted commonness only separates
                // species of the same tier.
                double rank = order * 1000.0 + sp.Weight * kv.Value * 0.001;
                scored.Add(new KeyValuePair<string, double>(sp.Name, rank));
            }
            scored.Sort((a, b) => b.Value.CompareTo(a.Value));

            var names = new List<string>();
            for (int i = 0; i < scored.Count && i < 4; i++) names.Add(scored[i].Key);
            return names.Count > 0 ? string.Join(", ", names) : "—";
        }

        private RectTransform MakeRow(float height, ref float y, float alpha)
        {
            var rt = UiKit.Rect("Row", _listContent, new Vector2(0, 1), new Vector2(1, 1),
                                new Vector2(4, -y - height), new Vector2(-4, -y - 4));
            var bg = rt.gameObject.AddComponent<Image>();
            bg.color = new Color(1f, 1f, 1f, 0.05f * alpha + 0.02f);
            bg.raycastTarget = false;
            _rows.Add(rt.gameObject);
            y += height;
            return rt;
        }

        /// <summary>
        /// The numbers that actually decide a fight, in the units the physics uses.
        ///
        /// Deliberately not a star rating: "Klac 84 N" against a line marked
        /// "13.6 kg" is what lets a player reason about whether their clutch can
        /// out-pull their line, which is the single decision the whole tension
        /// model is built around. A five-star bar would hide exactly that.
        /// </summary>
        private static string StatLine(GearItem item)
        {
            switch (item.Slot)
            {
                case "rod":
                    return $"Kuasa {item.Power:0} N · Panjang {item.Length:0.0} m · Lontar {item.CastPower:0.00}";
                case "reel":
                    return $"Klac {item.Drag:0} N · Karau {item.Retrieve:0.00} m/s · Licin {item.DragSmooth:0.00}";
                case "line":
                    return $"Kuat {item.Test:0.0} kg · Regang {item.Stretch * 100:0}% · Nampak {item.Visibility:0.00}";
                case "lure":
                    return $"Tenggelam {item.Sink:0.00} · Gerak {item.Action:0.00} · Bising {item.Noise:0.00}";
                default:
                    return "";
            }
        }

        /* --- actions -------------------------------------------------------------- */

        private void TryBuy(GearItem item)
        {
            var r = Game.State.Buy(item.Id);
            if (r.Ok)
            {
                _hud?.Toast($"Dibeli: {item.Name}", "good");
                return;
            }
            _hud?.Toast(r.Reason switch
            {
                "money" => $"Duit tak cukup — perlu RM {r.Need:0}.",
                "level" => $"Perlu Tahap {r.Need:0}.",
                _ => "Tidak boleh dibeli.",
            }, "warn");
        }

        private void TryEquip(GearItem item)
        {
            if (Game.State.EquipItem(item.Id)) _hud?.Toast($"Dipakai: {item.Name}", "good");
            else _hud?.Toast("Tidak boleh dipakai.", "warn");
        }

        private void TryTravel(Spot spot)
        {
            // Wind in first. The lake is rebuilt around the player on arrival, so a
            // lure still in the air would land in water that no longer exists — and
            // BedDepth would be describing the pond we just left.
            if (Game.Fishing != null && !Game.Fishing.Abort())
            {
                _hud?.Toast("Ada ikan di hujung tali — selesaikan dahulu.", "warn");
                return;
            }

            var r = Game.State.Travel(spot.Id);
            if (r.Ok)
            {
                _hud?.Toast($"Tiba di {spot.Name}.", "good");
                Close();
                return;
            }
            _hud?.Toast(r.Reason switch
            {
                "money" => $"Yuran masuk RM {r.Need:0} — duit tak cukup.",
                "locked" => $"Perlu Tahap {r.Need:0}.",
                _ => "Tidak boleh ke sana.",
            }, "warn");
        }
    }
}
