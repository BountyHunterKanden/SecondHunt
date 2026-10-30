using System;
using OpenTK.Mathematics;

// How the 3D view is framed on a widescreen. MPH's camera is 78 degrees top to bottom; on the DS's 4:3 screen that is
// ~94 degrees side to side, and a flat (rectilinear) picture that wide draws things near the sides stretched (2.2x wider
// than at the centre on the DS). MphRead keeps the 78 degrees and widens the sides with the screen, so 16:9 shows ~110
// degrees side to side and stretches its sides 3.1x: the "fish-eye" that swells things as a turn slides them to the edge.
//   Wide        MphRead's own (the default): 78 degrees top to bottom, the sides widen with the screen
//   DsFraming   the DS's side-to-side view (~94 degrees at any width); the top-to-bottom view narrows (~62 on 16:9)
//   Prime       Metroid Prime's own camera: 55 degrees top to bottom (CTweakGame FieldOfView, read from GM8E01 rev 0
//               Tweaks.Pak "Game" +0x14), i.e. 85.6 side to side on 16:9, edges 1.86x. Every modern game does it this
//               way: the same flat projection as Wide, just a narrower view
//   Panini      Wide's side-to-side view, redrawn with a Panini projection (Sharpless, Postle & German 2010): vertical
//               lines stay straight and the sides are squeezed back instead of swelling. Cropped to fit, so the centre
//               is magnified and the top-to-bottom view at the centre narrows (~61 on 16:9); off-centre horizontal
//               lines bow. No flat picture shows 110 degrees without stretching something (Panini trades edge swell
//               for bowing)
// The Panini math is the cylindrical Panini of Unity HDRP's PaniniProjection (Panini_Generic, crop to fit).
// The gun keeps its vanilla size in every mode: the host draws it with VanillaProjection, its own projection, the way
// modern games draw their viewmodels (so a view setting never resizes the gun).
namespace MphRecomp.Framing
{
    public enum ViewMode { Wide, DsFraming, Prime, Panini }

    public static class ViewFraming
    {
        public const float DsAspect = 4f / 3;
        public const float MphFovY = 78; // every hunter's NormalFov (39) x 2
        public const float PrimeFovY = 55;

        // Scene.HostFramingAspect keeps the side-to-side view a screen of this aspect gets at MPH's 78 degrees: Prime's
        // 85.6 degrees (its 55 on a 16:9 screen) comes out as aspect 1.143
        public static readonly float PrimeFramingAspect = MathF.Tan(PrimeFovY * MathF.PI / 360) * (16f / 9)
            / MathF.Tan(MphFovY * MathF.PI / 360);

        public static readonly ViewMode[] Modes = { ViewMode.Wide, ViewMode.DsFraming, ViewMode.Prime, ViewMode.Panini };

        public static string Label(ViewMode mode) => mode switch
        {
            ViewMode.DsFraming => "DS FRAMING (the DS's 94 deg side to side)",
            ViewMode.Prime => "PRIME (Metroid Prime's 55 deg: 86 deg side to side)",
            ViewMode.Panini => "PANINI (110 deg wide, edges straightened, lines bow)",
            _ => "WIDE (current / MphRead)"
        };

        public static ViewMode? Parse(string? s) => s?.ToLowerInvariant() switch
        {
            "wide" => ViewMode.Wide,
            "ds" => ViewMode.DsFraming,
            "prime" => ViewMode.Prime,
            "panini" => ViewMode.Panini,
            _ => null
        };

        // Panini strength: 0 = rectilinear, 1 = the standard Panini (projection centre one radius behind the axis)
        public static float PaniniD(ViewMode mode) => mode == ViewMode.Panini ? 1f : 0f;

        // Scene.HostFramingAspect for a mode (0 = MphRead's own)
        public static float FramingAspect(ViewMode mode) => mode switch
        {
            ViewMode.DsFraming => DsAspect,
            ViewMode.Prime => PrimeFramingAspect,
            _ => 0
        };

        // the projection MphRead draws with in Wide, rebuilt from the one in use (framing changes only the two FOV terms,
        // M11 and M22; near/far stay): the gun's own projection, so it keeps its vanilla size in every mode.
        // fovYDeg = the player's CameraInfo.Fov (zoom included)
        public static Matrix4 VanillaProjection(Matrix4 current, float fovYDeg, float aspect)
        {
            float tanY = MathF.Tan(fovYDeg * MathF.PI / 360);
            current.M22 = 1 / tanY;
            current.M11 = 1 / (tanY * aspect);
            return current;
        }

        // Panini image plane -> rectilinear tangent coords (x/z, y/z). The ray from the projection centre (0, 0, -d)
        // through (X, Y, 1) meets the unit cylinder about the vertical axis at Q; the view ray points at Q from the axis.
        public static Vector2 PaniniToRect(Vector2 p, float d)
        {
            float viewDist = 1 + d;
            float hypSq = p.X * p.X + viewDist * viewDist;
            float isectD = p.X * d;
            float discrim = hypSq - isectD * isectD;
            float qz = (-isectD * p.X + viewDist * MathF.Sqrt(discrim)) / hypSq; // Q.z
            float t = (qz + d) / viewDist; // Q = P + t * ((X, Y, 1) - P)
            return new Vector2(p.X * t / qz, p.Y * t / qz);
        }

        // rectilinear tangent coords -> Panini image plane (the forward map; HUD markers)
        public static Vector2 RectToPanini(Vector2 r, float d)
        {
            float hyp = MathF.Sqrt(1 + r.X * r.X); // the ray's horizontal length at z = 1
            float cosPhi = 1 / hyp;
            float k = (1 + d) / (d + cosPhi);
            return new Vector2(k * r.X / hyp, k * r.Y / hyp);
        }

        // crop to fit: the output's extents as a fraction of the rectilinear render's. The output's corners land on the
        // render's corners and its sides (at the middle row) keep the render's side-to-side angle; the same for x and y.
        public static float CropScale(float tanHalfX, float d)
        {
            float cosH = 1 / MathF.Sqrt(1 + tanHalfX * tanHalfX);
            return (1 + d) * cosH / (d + cosH);
        }
    }

    // one frame's Panini warp from the rectilinear render (half-angle tangents TanX, TanY) to the output screen
    public readonly struct PaniniWarp
    {
        public readonly float TanX, TanY, D, Scale;

        public PaniniWarp(float tanX, float tanY, float d)
        {
            TanX = tanX;
            TanY = tanY;
            D = d;
            Scale = ViewFraming.CropScale(tanX, d);
        }

        // from the perspective matrix the render used (OpenTK: M11 = 1 / (aspect * tanY), M22 = 1 / tanY)
        public static PaniniWarp FromProjection(Matrix4 proj, float d) => new(1 / proj.M11, 1 / proj.M22, d);

        // output NDC -> render NDC (what the shader does per pixel)
        public Vector2 OutputToRender(Vector2 ndc)
        {
            Vector2 rect = ViewFraming.PaniniToRect(new Vector2(ndc.X * TanX * Scale, ndc.Y * TanY * Scale), D);
            return new Vector2(rect.X / TanX, rect.Y / TanY);
        }

        // render NDC -> output NDC (Scene.HostScreenWarp: the reticle, locator icons and scan brackets)
        public Vector2 RenderToOutput(Vector2 ndc)
        {
            Vector2 p = ViewFraming.RectToPanini(new Vector2(ndc.X * TanX, ndc.Y * TanY), D);
            return new Vector2(p.X / (TanX * Scale), p.Y / (TanY * Scale));
        }
    }
}
