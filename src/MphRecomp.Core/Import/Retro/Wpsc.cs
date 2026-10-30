using System;
using System.Collections.Generic;
using System.IO;

namespace MphRecomp.Import.Retro
{
    // A Retro weapon's shot (WPSC, "WPSM"): its properties in the PART element grammar (Part.cs). Only what our shots use
    // is named (docs/MP3_HYPER_EFFECTS_FORMATS.md section 4, from our own survey of the disc); the rest are read as
    // whichever kind parses (mp3) and left unused.
    //   APSM: the shot's particle effect     ASW1-3: its swooshes     COLR: its hit table (CRSC)
    //   IVEC: speed (units a frame, Y = forward)     PSLT: frames it lives     HOMG: homes in     PSVM: its drift
    //   IORN: a random roll     B1TX / B1RT: a textured head and its size
    // Added for the Dark Samus Hyper Mode thread, 2026-10-12.
    public sealed class Wpsc
    {
        public const uint Magic = 0x5750534D;   // "WPSM"

        public readonly Dictionary<string, Part.Elem?> Props = new(StringComparer.Ordinal);
        public Part.Elem? this[string tag] => Props.TryGetValue(tag, out Part.Elem? e) ? e : null;
        public bool Flag(string tag) => this[tag]?.B ?? false;
        public uint AssetId(string tag) => this[tag] is Part.Elem e && e.Id != UInt32.MaxValue ? e.Id : 0;

        // echoes: Echoes' (Part.Load's echoes grammar); its shot looks are two billboards: B1 / B2 (TX texture, CL colour,
        // SE size, RB1A / RB2A additive), read here for Shooting Gallery's turret (EchoesTurret.ImportFx), 2026-10-16
        public static Wpsc Load(byte[] b, bool mp3 = false, bool echoes = false)
        {
            var r = new Part.Reader(b) { Mp3 = mp3, Echoes = echoes };
            if (r.U32() != Magic) throw new InvalidDataException("not a weapon shot (WPSM)");
            var w = new Wpsc();
            while (true)
            {
                string tag = r.Tag();
                if (tag == "_END") break;
                w.Props[tag] = tag switch
                {
                    "APSM" or "APS2" => r.Asset("PART"),
                    "ASW1" or "ASW2" or "ASW3" => r.Asset("SWHC"),
                    "COLR" => r.Asset("CRSC"),
                    "IVEC" or "IORN" or "IOFS" => r.Vector(),
                    "PSLT" => r.Int(),
                    "PSVM" => r.Mod(),
                    "B1RT" or "TRAT" or "FADD" or "FADS" => r.Real(),
                    "B1TX" => r.Texture(),
                    "B2TX" when echoes => r.Texture(),
                    "B1CL" or "B2CL" when echoes => r.Color(),
                    "B1SE" or "B2SE" or "FOFF" or "RNGE" when echoes => r.Real(),
                    "PJFX" when echoes => r.Int(),
                    _ when mp3 || echoes => r.Any(tag),
                    _ => throw new InvalidDataException($"weapon property {tag} at {r.O - 4:x} is not known"),
                };
            }
            return w;
        }
    }
}
