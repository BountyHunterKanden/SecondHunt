using NCSFCommon;
using NCSFPlayer.Engine;

namespace NCSFPlayer;

// Spec Part 5: the sequence program interpreter (one lane step, one command, operand reads).
public sealed partial class Player
{
    enum ValueKind
    {
        None,
        Byte,
        Word,
        Vlq,
        Var,
        Rand,
    }

    static int ReadU8(Track lane) => lane.Data.Span[lane.Pos++];

    static int ReadU16(Track lane)
    {
        int lo = ReadU8(lane);
        int hi = ReadU8(lane);
        return lo | (hi << 8);
    }

    static int ReadU24(Track lane)
    {
        int b0 = ReadU8(lane);
        int b1 = ReadU8(lane);
        int b2 = ReadU8(lane);
        return b0 | (b1 << 8) | (b2 << 16);
    }

    static int ReadVlq(Track lane)
    {
        int value = 0;
        int b;
        do
        {
            b = ReadU8(lane);
            value = (value << 7) | (b & 0x7F);
        }
        while ((b & 0x80) != 0);
        return value;
    }

    /// <summary>Spec 5.1 value read.</summary>
    int ReadValue(Track lane, ValueKind kind)
    {
        switch (kind)
        {
        case ValueKind.Byte:
            return ReadU8(lane);
        case ValueKind.Word:
            return ReadU16(lane);
        case ValueKind.Vlq:
            return ReadVlq(lane);
        case ValueKind.Var:
            return this.vars[ReadU8(lane)];
        case ValueKind.Rand:
        {
            int lo = (short)ReadU16(lane);
            int hi = (short)ReadU16(lane);
            int n = hi - lo + 1;
            int r = SharedRandom.Draw();
            return unchecked((r * n) >> 16) + lo;
        }
        default:
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>Spec 5.3 laneStep; false = the lane ended.</summary>
    bool LaneStep(Track lane)
    {
        foreach (int n in lane.Owned)
        {
            var v = this.voices[n];
            if (v.Length > 0)
                v.Length -= 1;
            if (!v.AutoSweep && v.SweepCount < v.SweepLength)
                v.SweepCount += 1;
        }

        if (lane.FinishWait)
        {
            if (lane.Owned.Count != 0)
                return true;
            lane.FinishWait = false;
        }

        if (lane.Wait > 0)
        {
            lane.Wait -= 1;
            if (lane.Wait > 0)
                return true;
        }

        while (lane.Wait == 0 && !lane.FinishWait)
            if (this.RunCommand(lane))
                return false;

        return true;
    }

    /// <summary>Runs one command; true = an executed 0xFF (the lane ends).</summary>
    bool RunCommand(Track lane)
    {
        int c = ReadU8(lane);
        bool run = true;
        var kind = ValueKind.None;
        if (c == 0xA2)
        {
            c = ReadU8(lane);
            run = lane.Cond;
        }
        if (c == 0xA0)
        {
            c = ReadU8(lane);
            kind = ValueKind.Rand;
        }
        if (c == 0xA1)
        {
            c = ReadU8(lane);
            kind = ValueKind.Var;
        }

        ValueKind Or(ValueKind fallback) => kind == ValueKind.None ? fallback : kind;

        if (c < 0x80)
        {
            byte vel = (byte)ReadU8(lane);
            int len = this.ReadValue(lane, Or(ValueKind.Vlq));
            if (run)
            {
                byte key = (byte)Math.Clamp(c + lane.Transpose, 0, 127);
                this.PlayNote(lane, key, vel, len > 0 ? len : -1);
                lane.PortaKey = key;
                if (lane.NoteWait)
                {
                    lane.Wait = len;
                    if (len == 0)
                        lane.FinishWait = true;
                }
            }
            return false;
        }

        switch (c & 0xF0)
        {
        case 0x80:
        {
            int n = this.ReadValue(lane, Or(ValueKind.Vlq));
            if (run)
            {
                if (c == 0x80)
                    lane.Wait = n;
                else if (c == 0x81 && n < 65536)
                    lane.Program = (ushort)n;
            }
            break;
        }
        case 0x90:
            this.RunJumpCommand(lane, c, run);
            break;
        case 0xC0:
        case 0xD0:
        {
            byte n = (byte)this.ReadValue(lane, Or(ValueKind.Byte));
            if (run)
                this.RunByteCommand(lane, c, n);
            break;
        }
        case 0xE0:
        {
            short n = (short)this.ReadValue(lane, Or(ValueKind.Word));
            if (run)
            {
                if (c == 0xE0)
                    lane.ModDelay = (ushort)n;
                else if (c == 0xE1)
                    this.tempo = (ushort)n;
                else if (c == 0xE3)
                    lane.Sweep = n;
            }
            break;
        }
        case 0xB0:
        {
            int i = ReadU8(lane);
            short n = (short)this.ReadValue(lane, Or(ValueKind.Word));
            if (run)
                this.RunVarCommand(lane, c, i, n);
            break;
        }
        case 0xF0:
            if (run)
                return RunFlowCommand(lane, c);
            break;
        default:
            // 0xA0-0xAF left over after the prefixes: no operands, nothing
            break;
        }
        return false;
    }

    void RunJumpCommand(Track lane, int c, bool run)
    {
        switch (c)
        {
        case 0x93:
        {
            int s = ReadU8(lane);
            int off = ReadU24(lane);
            if (run && s <= 15 && this.slots[s] is Track other && !ReferenceEquals(other, lane))
            {
                this.LaneStop(other);
                other.Data = lane.Data;
                other.Pos = off;
            }
            break;
        }
        case 0x94:
        {
            int off = ReadU24(lane);
            if (run)
                lane.Pos = off;
            break;
        }
        case 0x95:
        {
            int off = ReadU24(lane);
            if (run && lane.Depth < 3)
            {
                lane.StackPos[lane.Depth] = lane.Pos;
                lane.Depth += 1;
                lane.Pos = off;
            }
            break;
        }
        }
    }

    void RunByteCommand(Track lane, int c, byte n)
    {
        switch (c)
        {
        case 0xC0:
            lane.Pan = (sbyte)(n - 64);
            break;
        case 0xC1:
            lane.Volume = n;
            break;
        case 0xC2:
            this.masterVolume = n;
            break;
        case 0xC3:
            lane.Transpose = (sbyte)n;
            break;
        case 0xC4:
            lane.Bend = (sbyte)n;
            break;
        case 0xC5:
            lane.BendRange = n;
            break;
        case 0xC6:
            lane.LanePriority = n;
            break;
        case 0xC7:
            lane.NoteWait = n != 0;
            break;
        case 0xC8:
            lane.Tie = n != 0;
            this.LaneReleaseVoices(lane, -1);
            this.LaneFreeVoices(lane);
            break;
        case 0xC9:
            lane.PortaKey = (byte)(n + lane.Transpose);
            lane.Portamento = true;
            break;
        case 0xCA:
            lane.ModDepth = n;
            break;
        case 0xCB:
            lane.ModSpeed = n;
            break;
        case 0xCC:
            lane.ModTarget = n;
            break;
        case 0xCD:
            lane.ModRange = n;
            break;
        case 0xCE:
            lane.Portamento = n != 0;
            break;
        case 0xCF:
            lane.PortaTime = n;
            break;
        case 0xD0:
            lane.AttackOverride = n;
            break;
        case 0xD1:
            lane.DecayOverride = n;
            break;
        case 0xD2:
            lane.SustainOverride = n;
            break;
        case 0xD3:
            lane.ReleaseOverride = n;
            break;
        case 0xD4:
            if (lane.Depth < 3)
            {
                lane.StackPos[lane.Depth] = lane.Pos;
                lane.StackCount[lane.Depth] = n;
                lane.Depth += 1;
            }
            break;
        case 0xD5:
            lane.Expression = n;
            break;
        }
    }

    void RunVarCommand(Track lane, int c, int i, short n)
    {
        short x = this.vars[i];
        switch (c)
        {
        case 0xB0:
            x = n;
            break;
        case 0xB1:
            x = (short)(x + n);
            break;
        case 0xB2:
            x = (short)(x - n);
            break;
        case 0xB3:
            x = unchecked((short)(x * n));
            break;
        case 0xB4:
            if (n != 0)
                x = (short)(x / n);
            break;
        case 0xB5:
            x = n >= 0 ? (short)(x << n) : (short)(x >> -n);
            break;
        case 0xB6:
        {
            bool neg = n < 0;
            if (neg)
                n = (short)-n;
            int r = unchecked(SharedRandom.Draw() * (n + 1)) >> 16;
            if (neg)
                r = -r;
            x = (short)r;
            break;
        }
        case 0xB8:
            lane.Cond = x == n;
            break;
        case 0xB9:
            lane.Cond = x >= n;
            break;
        case 0xBA:
            lane.Cond = x > n;
            break;
        case 0xBB:
            lane.Cond = x <= n;
            break;
        case 0xBC:
            lane.Cond = x < n;
            break;
        case 0xBD:
            lane.Cond = x != n;
            break;
        }
        this.vars[i] = x;
    }

    /// <summary>0xF0-0xFF (run only); true = 0xFF, the lane ends.</summary>
    static bool RunFlowCommand(Track lane, int c)
    {
        switch (c)
        {
        case 0xFD:
            if (lane.Depth != 0)
            {
                lane.Depth -= 1;
                lane.Pos = lane.StackPos[lane.Depth];
            }
            break;
        case 0xFC:
            if (lane.Depth != 0)
            {
                int k = lane.StackCount[lane.Depth - 1];
                if (k != 0)
                {
                    k -= 1;
                    if (k == 0)
                    {
                        lane.Depth -= 1;
                        break;
                    }
                }
                lane.StackCount[lane.Depth - 1] = (byte)k;
                lane.Pos = lane.StackPos[lane.Depth - 1];
            }
            break;
        case 0xFF:
            return true;
        }
        return false;
    }
}
