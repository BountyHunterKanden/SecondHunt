using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Retro
{
    // A Retro AI state machine: Prime 1's AFSM and Echoes' FSM2 (Echoes also still ships some AFSM). Read from our own
    // survey of the discs (every AFSM/FSM2 on mp1.iso and echoes_retail.rvz parses to its end; MphRead.Tools -fsmdump),
    // checked against the public format notes (Metaforce's CStateMachine). Only parsed here; nothing runs it yet
    // (docs/ENEMY_IMPORT.md section 11.2 item 1).
    //   AFSM: u32 states, that many state names; u32 conditions in all; then per state: u32 transitions, and per
    //     transition: u32 conditions, each a name ("!" in front = negated) and an f32 argument, with the u32 target
    //     state right after the FIRST condition. 0xff padding to 32 bytes.
    //   FSM2 v1: "FSM2", u32 version, u32 counts of states / transitions / actions / groups, then those items in order.
    //     Every item is a name and a list of links (a label, usually empty, and a u32: high byte = the item kind,
    //     low 24 bits = its index). Transitions add an f32 argument before the links and a u8 flag after them;
    //     groups (e.g. "Main": transitions that apply in every state) add a trailing u32.
    // Added for the enemy-import thread, 2026-10-30.
    public sealed class Fsm
    {
        public const uint Fsm2Magic = 0x46534D32;   // "FSM2"

        public enum Kind : byte { Transition = 0, State = 1, Action = 2, Group = 3 }
        public readonly record struct Link(string Label, Kind Kind, int Index);
        public sealed record Condition(string Name, float Arg, bool Not);
        public sealed record Transition(List<Condition> Conditions, int Target);
        public sealed record State(string Name, List<Transition> Transitions, List<Link> Links);
        public sealed record Node(string Name, float Arg, List<Link> Links, uint Extra);

        public bool IsFsm2;
        public int Version;
        public readonly List<State> States = new();
        // FSM2 only: transitions (named conditions with links on), actions and groups
        public readonly List<Node> Transitions = new(), Actions = new(), Groups = new();
        public int End;   // where the data ended (the rest is padding)

        public static Fsm Load(byte[] b)
        {
            Fsm f = b.Length >= 4 && Be.U32(b, 0) == Fsm2Magic ? LoadFsm2(b) : LoadAfsm(b);
            for (int i = f.End; i < b.Length; i++)
                if (b[i] != 0xff) throw new InvalidDataException($"state machine: unread data at {f.End:x}");
            return f;
        }

        static Fsm LoadAfsm(byte[] b)
        {
            var f = new Fsm();
            int o = 0, n = (int)Be.U32(b, o); o += 4;
            var names = new string[n];
            for (int i = 0; i < n; i++) { names[i] = Be.CString(b, o, out int e); o = e + 1; }
            int total = (int)Be.U32(b, o), seen = 0; o += 4;
            for (int i = 0; i < n; i++)
            {
                var ts = new List<Transition>();
                int tc = (int)Be.U32(b, o); o += 4;
                for (int t = 0; t < tc; t++)
                {
                    int cc = (int)Be.U32(b, o), target = -1; o += 4;
                    var conds = new List<Condition>();
                    for (int c = 0; c < cc; c++)
                    {
                        string name = Be.CString(b, o, out int e); o = e + 1;
                        float arg = Be.F32(b, o); o += 4;
                        if (c == 0) { target = (int)Be.U32(b, o); o += 4; }
                        bool not = name.StartsWith('!');
                        conds.Add(new Condition(not ? name[1..] : name, arg, not));
                    }
                    if (target >= n) throw new InvalidDataException($"AFSM: target {target} of {n} states");
                    seen += cc;
                    ts.Add(new Transition(conds, target));
                }
                f.States.Add(new State(names[i], ts, new List<Link>()));
            }
            if (seen != total) throw new InvalidDataException($"AFSM: {seen} conditions, header says {total}");
            f.End = o;
            return f;
        }

        static Fsm LoadFsm2(byte[] b)
        {
            var f = new Fsm { IsFsm2 = true, Version = (int)Be.U32(b, 4) };
            if (f.Version != 1) throw new InvalidDataException($"FSM2 version {f.Version} is not known");
            int o = 24;
            int ns = (int)Be.U32(b, 8), nt = (int)Be.U32(b, 12), na = (int)Be.U32(b, 16), ng = (int)Be.U32(b, 20);
            string Name() { string s = Be.CString(b, o, out int e); o = e + 1; return s; }
            List<Link> Links()
            {
                int n = (int)Be.U32(b, o); o += 4;
                var l = new List<Link>(n);
                for (int i = 0; i < n; i++)
                {
                    string label = Name();
                    uint v = Be.U32(b, o); o += 4;
                    l.Add(new Link(label, (Kind)(v >> 24), (int)(v & 0xffffff)));
                }
                return l;
            }
            for (int i = 0; i < ns; i++) { string nm = Name(); f.States.Add(new State(nm, new List<Transition>(), Links())); }
            for (int i = 0; i < nt; i++)
            {
                string nm = Name();
                float arg = Be.F32(b, o); o += 4;
                List<Link> l = Links();
                f.Transitions.Add(new Node(nm, arg, l, b[o++]));
            }
            for (int i = 0; i < na; i++) { string nm = Name(); f.Actions.Add(new Node(nm, 0, Links(), 0)); }
            for (int i = 0; i < ng; i++)
            {
                string nm = Name();
                List<Link> l = Links();
                f.Groups.Add(new Node(nm, 0, l, Be.U32(b, o))); o += 4;
            }
            f.End = o;
            return f;
        }

        public string NameOf(Link l) => l.Kind switch
        {
            Kind.State => l.Index < States.Count ? States[l.Index].Name : $"state#{l.Index}",
            Kind.Transition => l.Index < Transitions.Count ? Transitions[l.Index].Name : $"trans#{l.Index}",
            Kind.Action => l.Index < Actions.Count ? Actions[l.Index].Name : $"action#{l.Index}",
            _ => l.Index < Groups.Count ? Groups[l.Index].Name : $"group#{l.Index}",
        };

        // every condition / trigger name the machine uses (what a port must implement for this enemy)
        public IEnumerable<string> ConditionNames()
        {
            foreach (State s in States)
                foreach (Transition t in s.Transitions)
                    foreach (Condition c in t.Conditions) yield return c.Name;
            foreach (Node t in Transitions) yield return t.Name;
        }
    }
}
