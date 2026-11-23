namespace MphRecomp.Campaign
{
    // how an effect's colour meets the screen's: Default = the effect's own Additive flag (Prime 1/2 data)
    public enum RetroBlend : byte { Default, Alpha, Add, PremulAlpha, AddOne, Opaque }

    public static class RetroBlends
    {
        // Corruption's effect blend modes (SBDM: swooshes, PBDM: particles, as main.dol's one setter 0x803a5a3c reads them,
        // a jump table at 0x8059cb68): the GX blend, and for 4-6 and 8-10 a constant destination alpha (GXSetDstAlpha 0xff,
        // 0x80, 0x40) that marks the pixels for the game's bloom: Glow 1, 0.5, 0.25. 11 and 12 write alpha 0 (no bloom).
        // Blend: 0 src alpha / inverse src alpha, 1 src alpha / one, 2 one / inverse src alpha, 3 one / one, 7 one / zero
        public static (RetroBlend Blend, float Glow) Mp3(int mode) => mode switch
        {
            1 or 11 => (RetroBlend.Add, 0),
            2 => (RetroBlend.PremulAlpha, 0),
            3 => (RetroBlend.AddOne, 0),
            4 => (RetroBlend.Add, 1),
            5 => (RetroBlend.Add, 0.5f),
            6 => (RetroBlend.Add, 0.25f),
            7 => (RetroBlend.Opaque, 0),
            8 => (RetroBlend.Alpha, 1),
            9 => (RetroBlend.Alpha, 0.5f),
            10 => (RetroBlend.Alpha, 0.25f),
            _ => (RetroBlend.Alpha, 0),
        };

        public static bool Adds(this RetroBlend b) => b is RetroBlend.Add or RetroBlend.AddOne;
    }
}
