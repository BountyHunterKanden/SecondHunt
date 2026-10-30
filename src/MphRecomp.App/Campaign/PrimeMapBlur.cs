namespace MphRecomp.App;

// The PRIME map's backdrop (Core Frontend/PrimeMap.cs): the paused game's frame, blurred as Prime's pause screens blur
// the world. The screen is halved five times with linear blits (down to 1/32: a 32 x 32 box; owner 2026-10-14: the first
// 8 x 8 "needs to be twice as strong or more"), then blitted back up to 1/8 a level at a time so the blocks melt into a
// smooth blur (each linear step up is a tent filter); PrimeMap draws that over the frame at its Blur alpha. Copied every
// frame the map shows (the paused game is drawn again each frame anyway; eight small blits). GL thread only.
internal sealed class PrimeMapBlur
{
    const int Levels = 5; // the deepest level: 1/2^Levels of the screen (the blur's strength)
    const int Shown = 2; // the level drawn: 1/8 of the screen
    readonly int[] _fbo = new int[Levels], _tex = new int[Levels];
    int _w, _h;

    // the GL objects belonged to the old context
    public void Reset()
    {
        _w = _h = 0;
        for (int i = 0; i < Levels; i++) _fbo[i] = _tex[i] = 0;
    }

    // the screen (framebuffer 0, as Render left it) -> the blurred texture's GL id (rows bottom up), 0 if it failed
    public int Capture(int width, int height)
    {
        if (width < 16 || height < 16) return 0;
        if (width != _w || height != _h) Allocate(width, height);
        int srcW = width, srcH = height, src = 0;
        for (int i = 0; i < Levels; i++)
        {
            int w = Math.Max(1, width >> (i + 1)), h = Math.Max(1, height >> (i + 1));
            GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, src);
            GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, _fbo[i]);
            GLES30.GlBlitFramebuffer(0, 0, srcW, srcH, 0, 0, w, h, GLES30.GlColorBufferBit, GLES30.GlLinear);
            (src, srcW, srcH) = (_fbo[i], w, h);
        }
        // back up, a level at a time
        for (int i = Levels - 1; i > Shown; i--)
        {
            int w = Math.Max(1, width >> (i + 1)), h = Math.Max(1, height >> (i + 1));
            int upW = Math.Max(1, width >> i), upH = Math.Max(1, height >> i);
            GLES30.GlBindFramebuffer(GLES30.GlReadFramebuffer, _fbo[i]);
            GLES30.GlBindFramebuffer(GLES30.GlDrawFramebuffer, _fbo[i - 1]);
            GLES30.GlBlitFramebuffer(0, 0, w, h, 0, 0, upW, upH, GLES30.GlColorBufferBit, GLES30.GlLinear);
        }
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
        return _tex[Shown];
    }

    void Allocate(int width, int height)
    {
        if (_fbo[0] != 0)
        {
            GLES30.GlDeleteFramebuffers(Levels, _fbo, 0);
            GLES30.GlDeleteTextures(Levels, _tex, 0);
        }
        GLES30.GlGenFramebuffers(Levels, _fbo, 0);
        GLES30.GlGenTextures(Levels, _tex, 0);
        for (int i = 0; i < Levels; i++)
        {
            int w = Math.Max(1, width >> (i + 1)), h = Math.Max(1, height >> (i + 1));
            GLES30.GlBindTexture(GLES30.GlTexture2d, _tex[i]);
            GLES30.GlTexImage2D(GLES30.GlTexture2d, 0, GLES30.GlRgba, w, h, 0, GLES30.GlRgba, GLES30.GlUnsignedByte, (byte[]?)null);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMinFilter, GLES30.GlLinear);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureMagFilter, GLES30.GlLinear);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapS, GLES30.GlClampToEdge);
            GLES30.GlTexParameteri(GLES30.GlTexture2d, GLES30.GlTextureWrapT, GLES30.GlClampToEdge);
            GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, _fbo[i]);
            GLES30.GlFramebufferTexture2D(GLES30.GlFramebuffer, GLES30.GlColorAttachment0, GLES30.GlTexture2d, _tex[i], 0);
        }
        GLES30.GlBindFramebuffer(GLES30.GlFramebuffer, 0);
        _w = width;
        _h = height;
    }
}
