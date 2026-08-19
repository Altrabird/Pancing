using System;
using System.Collections.Generic;

namespace Pancing.Sim
{
    /// <summary>
    /// Canonical event names, so a typo fails at compile time instead of silently
    /// never firing.
    /// </summary>
    public static class EV
    {
        // cast / line
        public const string CastStart = "cast:start";
        public const string CastLand = "cast:land";
        public const string LureSettled = "lure:settled";
        public const string ReelIn = "reel:in";
        public const string LineSnap = "line:snap";
        public const string RodOverload = "rod:overload";
        public const string Snagged = "line:snag";

        // bite FSM
        public const string Interest = "bite:interest";
        public const string Nibble = "bite:nibble";
        public const string BiteOn = "bite:on";
        public const string BiteMissed = "bite:missed";
        public const string Spooked = "bite:spooked";
        public const string Hooked = "bite:hooked";
        public const string HooksetEarly = "bite:early";

        // fight
        public const string FightStart = "fight:start";
        public const string FightStateChanged = "fight:state";
        public const string FishJump = "fight:jump";
        public const string HookLost = "fight:hooklost";
        public const string Landed = "fight:landed";
        public const string FightEnd = "fight:end";

        // progression
        public const string XpGain = "prog:xp";
        public const string LevelUp = "prog:levelup";
        public const string Money = "prog:money";
        public const string Record = "prog:record";
        public const string Unlock = "prog:unlock";
        public const string QuestDone = "prog:quest";
        public const string GearEquip = "prog:equip";
        public const string GearBuy = "prog:buy";
        public const string LureOut = "prog:lureout";

        // world / ui
        public const string SpotChange = "world:spot";
        public const string WeatherChange = "world:weather";
        public const string TimePhaseChanged = "world:phase";
        public const string Ripple = "fx:ripple";
        public const string Splash = "fx:splash";
        public const string Toast = "ui:toast";
        public const string Save = "ui:save";
    }

    /// <summary>
    /// Minimal synchronous event bus — a port of web/src/core/events.js.
    ///
    /// Everything that crosses a system boundary goes through here: the physics
    /// layer never references the renderer, the renderer never references the
    /// fight AI. That is what keeps the simulation headless-testable, and it is
    /// why this assembly can have no engine reference at all.
    ///
    /// Payloads are `object` rather than a generic parameter on purpose. The
    /// alternative — one C# event per message — means the Sim assembly declaring
    /// a delegate type for every renderer concern, which is exactly the coupling
    /// the bus exists to prevent. These fire a few times a second, not per vertex,
    /// so the boxing is not worth designing around.
    /// </summary>
    public sealed class EventBus
    {
        private readonly Dictionary<string, List<Action<object>>> _handlers
            = new Dictionary<string, List<Action<object>>>();
        private readonly List<Action<string, object>> _any = new List<Action<string, object>>();

        /// <summary>Scratch buffer for dispatch. Handlers commonly unsubscribe
        /// themselves mid-dispatch, so we always iterate a copy.</summary>
        private readonly List<Action<object>> _dispatch = new List<Action<object>>();

        /// <summary>Subscribe. The returned action unsubscribes.</summary>
        public Action On(string type, Action<object> fn)
        {
            if (!_handlers.TryGetValue(type, out var list))
            {
                list = new List<Action<object>>();
                _handlers[type] = list;
            }
            list.Add(fn);
            return () => Off(type, fn);
        }

        /// <summary>Subscribe to a strongly-typed payload; messages of other shapes are ignored.</summary>
        public Action On<T>(string type, Action<T> fn) =>
            On(type, payload => { if (payload is T typed) fn(typed); });

        public Action Once(string type, Action<object> fn)
        {
            Action off = null;
            off = On(type, payload => { off?.Invoke(); fn(payload); });
            return off;
        }

        public void Off(string type, Action<object> fn)
        {
            if (_handlers.TryGetValue(type, out var list)) list.Remove(fn);
        }

        /// <summary>Subscribe to every event; used by the debug overlay and the harness.</summary>
        public Action OnAny(Action<string, object> fn)
        {
            _any.Add(fn);
            return () => _any.Remove(fn);
        }

        public void Emit(string type, object payload = null)
        {
            if (_handlers.TryGetValue(type, out var list) && list.Count > 0)
            {
                _dispatch.Clear();
                _dispatch.AddRange(list);
                for (int i = 0; i < _dispatch.Count; i++)
                {
                    try { _dispatch[i](payload); }
                    catch (Exception e) { Log?.Invoke($"[events] handler for \"{type}\" threw: {e}"); }
                }
            }

            if (_any.Count > 0)
            {
                var copy = _any.ToArray();
                for (int i = 0; i < copy.Length; i++)
                {
                    try { copy[i](type, payload); }
                    catch (Exception e) { Log?.Invoke($"[events] wildcard handler threw: {e}"); }
                }
            }
        }

        /// <summary>
        /// Where a handler exception goes. The Sim cannot call Debug.LogError — no
        /// engine reference — so the host wires this to Debug.LogError on startup
        /// and the parity harness wires it to Console.Error.
        /// </summary>
        public Action<string> Log;

        public void Clear()
        {
            _handlers.Clear();
            _any.Clear();
        }
    }
}
