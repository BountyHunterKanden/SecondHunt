using System;
using MphRecomp.Import.Retro;

namespace MphRecomp.Campaign.Retro
{
    // Runs an Echoes FSM2 state machine (Import/Retro/Fsm.cs reads it from the disc; docs/ENEMY_IMPORT.md E1). Our reading
    // of the format, checked against Splinter's 6f7f09cd and what its RSO's triggers do:
    //   - each tick the current state's links are tried in order: a Transition link is a condition (its name is the
    //     enemy's trigger, its f32 the argument; Extra 1 = negated); when it holds, its own links are followed in order:
    //     a State link is the target, a Transition link is a further condition that must also hold (an AND chain)
    //   - a State link straight on a state is an unconditional go (the "Null" and "Start" states are only these and
    //     conditions, and are passed through in the same tick)
    //   - an Action link (Grenchler's 0e17fe4b has two; Splinter's none) is done (Do) and its own links followed like a
    //     condition that holds
    // The enemy supplies the triggers (Condition) and actions (Do) and hears every change (Entered).
    public sealed class Fsm2Runner
    {
        public readonly Fsm Machine;
        public int State { get; private set; } = -1;
        public float TimeInState { get; private set; }
        public string StateName => State >= 0 ? Machine.States[State].Name : "";

        // (trigger name, argument) -> holds; the enemy's own triggers by name
        public Func<string, float, bool> Condition = (_, _) => false;
        // an action by name, done when the machine passes through it
        public Action<string>? Do;
        // (from, to): a state change, after TimeInState is reset
        public Action<int, int>? Entered;

        public Fsm2Runner(Fsm machine)
        {
            if (!machine.IsFsm2 || machine.States.Count == 0) throw new ArgumentException("not an FSM2 state machine");
            Machine = machine;
        }

        public void Start(int state = 0)
        {
            Go(state);
            Settle();
        }

        public void Tick(float dt)
        {
            if (State < 0) return;
            TimeInState += dt;
            if (Next(State) is int to) { Go(to); Settle(); }
        }

        // force a state (the enemy's own reactions outside the machine: hurt, death)
        public void Force(int state)
        {
            Go(state);
            Settle();
        }

        public int Find(string name, int after = -1)
        {
            for (int i = after + 1; i < Machine.States.Count; i++)
                if (Machine.States[i].Name == name) return i;
            return -1;
        }

        void Settle()
        {
            for (int hop = 0; hop < 8 && IsPassThrough(State); hop++)
            {
                if (Next(State) is not int to) break;
                Go(to);
            }
        }

        bool IsPassThrough(int s) => Machine.States[s].Name is "Null" or "Start";

        void Go(int to)
        {
            int from = State;
            State = to;
            TimeInState = 0;
            Entered?.Invoke(from, to);
        }

        int? Next(int s)
        {
            foreach (Fsm.Link l in Machine.States[s].Links)
            {
                if (l.Kind == Fsm.Kind.State) return l.Index;
                if (l.Kind == Fsm.Kind.Transition && Resolve(l.Index, 0) is int to) return to;
                if (l.Kind == Fsm.Kind.Action && RunAction(l.Index, 0) is int ta) return ta;
            }
            return null;
        }

        int? RunAction(int a, int depth)
        {
            if (depth > 8 || a >= Machine.Actions.Count) return null;
            Fsm.Node n = Machine.Actions[a];
            Do?.Invoke(n.Name);
            return Follow(n, depth);
        }

        int? Follow(Fsm.Node n, int depth)
        {
            foreach (Fsm.Link l in n.Links)
            {
                if (l.Kind == Fsm.Kind.State) return l.Index;
                if (l.Kind == Fsm.Kind.Transition && Resolve(l.Index, depth + 1) is int to) return to;
                if (l.Kind == Fsm.Kind.Action && RunAction(l.Index, depth + 1) is int ta) return ta;
            }
            return null;
        }

        int? Resolve(int t, int depth)
        {
            if (depth > 8 || t >= Machine.Transitions.Count) return null;
            Fsm.Node n = Machine.Transitions[t];
            bool holds = Condition(n.Name, n.Arg);
            if (n.Extra == 1) holds = !holds;
            if (!holds) return null;
            return Follow(n, depth);
        }
    }
}
