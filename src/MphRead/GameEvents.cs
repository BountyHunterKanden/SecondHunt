using System;

namespace MphRead
{
    // mph-recomp: the mod event bus. The engine calls these at the moments a mod might care about (a frame, a room, a hit,
    // a kill, a pickup...). Every call site is written `if (GameEvents.Any) GameEvents.X(...)`, so with no mod listening
    // the whole layer is one static bool read and nothing else: no allocation, no virtual call, no change to the sim.
    //
    // The bus only REPORTS what happened. Nothing a listener does here feeds back into the simulation, so recorded
    // replays, goldens and netcode are unaffected whether a mod is on or not. (Value filters that would change gameplay
    // are a separate, later layer; see docs/MOD_API.md.)
    //
    // Arguments are plain numbers and strings (slot numbers, entity ids, enum names) so they cross into Lua without any
    // object wrapping. A player is named by its slot (0-7), any other entity by its EntityId; -1 = none / unknown.
    public static class GameEvents
    {
        // The host sets this when at least one enabled mod listens for any event. Call sites check it first.
        public static bool Any;

        // "tick" fires every simulation frame, so it has its own flag: set only when a mod hooks tick itself.
        public static bool Tick;

        // (event name, args). One listener: the app's mod host. Never called while Any is false.
        public static Action<string, object[]>? Listener;

        public static void Raise(string name, params object[] args)
        {
            try { Listener?.Invoke(name, args); }
            catch { } // a listener must never be able to break a frame
        }

        // mod.on_message (docs/MOD_API.md §4.4): set only while a mod watches the level-scripting messages. Scene's
        // DispatchMessage calls it as each message is delivered: (message, sender id, target id, param 1, param 2), ids -1 =
        // none. Like Raise it only reports; a mod's msg.send is queued and sent at the end of the frame.
        public static Action<Message, int, int, object, object>? Messages;

        public static void RaiseMessage(Message message, int from, int to, object param1, object param2)
        {
            try { Messages?.Invoke(message, from, to, param1, param2); }
            catch { }
        }
    }
}
