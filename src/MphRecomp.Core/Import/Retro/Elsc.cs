using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MphRecomp.Import.Retro
{
    // A Retro electric effect (ELSC, "ELSM": lightning bolts, e.g. Corruption's PhazonBallSpikes around Dark Samus's
    // Hyper Ball): properties in the PART element grammar (Part.cs), read from our own survey of the disc and the public
    // format notes (docs/MP3_HYPER_EFFECTS_FORMATS.md section 3). Only parsed here; Core Campaign/RetroElectric.cs runs it.
    //   LIFE / SLIF int: the system's / each bolt's frames      GRAT real: new bolts a frame     SCNT int: most alive
    //   SSEG int: segments a bolt     IEMT / FEMT emitter: its start / end (their speeds bend it)
    //   AMPL / AMPD real: the jitter (scaled by segment length / direct)     LWD1-3 real / LCL1-3 colour: three lines
    //   ZERY bool: flat on Y = 0     SSWH / GPSM / EPSM: a swoosh / particles on its ends (not drawn)
    //   Corruption adds BLDM int (blend: 1 = additive) and TEXR (a texture along the lines).
    // Added for the Dark Samus Hyper Mode thread, 2026-10-12.
    public sealed class Elsc
    {
        public const uint Magic = 0x454C534D;   // "ELSM"

        public readonly Dictionary<string, Part.Elem?> Props = new(StringComparer.Ordinal);
        public Part.Elem? this[string tag] => Props.TryGetValue(tag, out Part.Elem? e) ? e : null;
        public bool Flag(string tag) => this[tag]?.B ?? false;
        public uint Texture => this["TEXR"]?.Id ?? 0;

        public static Elsc Load(byte[] b, bool mp3 = false)
        {
            var r = new Part.Reader(b) { Mp3 = mp3 };
            if (r.U32() != Magic) throw new InvalidDataException("not an electric effect (ELSM)");
            var e = new Elsc();
            while (true)
            {
                string tag = r.Tag();
                if (tag == "_END") break;
                e.Props[tag] = tag switch
                {
                    "LIFE" or "SLIF" or "SCNT" or "SSEG" or "BLDM" => r.Int(),
                    "GRAT" or "AMPL" or "AMPD" or "LWD1" or "LWD2" or "LWD3" => r.Real(),
                    "COLR" or "LCL1" or "LCL2" or "LCL3" => r.Color(),
                    "IEMT" or "FEMT" => r.Emitter(),
                    "ZERY" => r.Bool(),
                    "TEXR" => r.Texture(),
                    "SSWH" => r.Asset("SWHC"),
                    "GPSM" or "EPSM" => r.Asset("PART"),
                    _ when mp3 => r.Any(tag),
                    _ => throw new InvalidDataException($"electric property {tag} at {r.O - 4:x} is not known"),
                };
            }
            return e;
        }

        public IEnumerable<(string Type, uint Id)> Dependencies()
        {
            foreach ((string tag, Part.Elem? v) in Props)
            {
                if (v == null || v.Id == 0 || v.Id == UInt32.MaxValue) continue;
                if (v.Cat == Part.Cat.Texture) yield return ("TXTR", v.Id);
                else if (v.Cat == Part.Cat.Asset) yield return (v.Kind, v.Id);
            }
        }
    }
}
