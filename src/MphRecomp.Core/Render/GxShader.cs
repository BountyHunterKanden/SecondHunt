using System;
using System.Numerics;
using MphRecomp.Assets;

namespace MphRecomp.Render
{
    // The GX pipeline of a Brawl trophy material, run as data (shared by the device renderer and the PC comparison
    // tool, so what is checked on the PC is exactly what the Odin draws). Everything but the scene's lights comes
    // from the material as Mdl0Gx reads it -- checked field by field against BrawlLib on all 13 trophies:
    //   * two LIGHT CHANNELS per vertex: colour = material colour (register or vertex colour) x clamp(ambient (register
    //     or vertex) x scene ambient + sum over lights of light colour x diffuse term x attenuation), or just the
    //     material colour when lighting is off; the attenuation is either none/spot (a directional light: 1) or
    //     SPECULAR (the half-angle highlight, x^2 / (k0 + k1 x + k2 x^2) with the light's shininess);
    //   * TEXTURE COORDINATES per texture layer: the mesh UVs, or camera-space normals (a reflection map:
    //     s = 0.5 + 0.5 nx, t = 0.5 - 0.5 ny), then the layer's own transform (Maya: rotate about the centre, move,
    //     scale about the bottom-left) -- verified against BrawlLib's own matrices;
    //   * the TEV stages with their swap tables, register and konst colours; the two-part alpha test.
    // Not modelled (no trophy here uses them): indirect textures, TEV compare ops, projection map mode. Light-space
    // (2 Noxus layers) and specular (2 SamusR1 layers) map modes use the camera-space map.
    // Beyond GX: a GxJson material may carry a tangent-space NORMAL MAP (Metroid Prime 4's panel lines, ridges, tubes);
    // then light channel 0 and the reflection texcoords are worked out per pixel from the bent normal.
    public static class GxShader
    {
        public const int MaxStages = 8, MaxLights = 4;

        public const string Vert =
            "#version 300 es\n" +
            "layout(location=0) in vec3 aPos;\n" +
            "layout(location=1) in vec3 aNormal;\n" +
            "layout(location=2) in vec2 aUv;\n" +
            "layout(location=3) in vec3 aColor;\n" +
            "layout(location=4) in vec4 aBones;\n" +
            "layout(location=5) in vec4 aWeights;\n" +
            "uniform mat4 uMvp; uniform mat3 uViewRot;\n" +
            "uniform int uSkin; uniform mat4 uModel; uniform mat4 uBones[32];\n" +
            "uniform ivec4 uColCtrl[2];\n" +     // lit, material from vertex, ambient from vertex, diffuse fn
            "uniform ivec4 uAlpCtrl[2];\n" +
            "uniform ivec2 uAttn[2];\n" +        // attenuation fn: colour, alpha (0/2 none, 1 specular, 3 spot)
            "uniform vec4 uChanMat[2]; uniform vec4 uChanAmb[2];\n" +
            "uniform vec3 uSceneAmb; uniform int uNumLights;\n" +
            "uniform vec3 uLightDir[4]; uniform vec3 uLightCol[4]; uniform vec3 uLightSpecCol[4]; uniform vec3 uLightSpecK[4];\n" +
            "uniform int uTgMode[8];\n" +         // per texcoord: 0 mesh UV, 1 camera-space normal
            "uniform mat3 uTexMtx[8];\n" +
            "uniform int uRawEnvNormal;\n" +       // 1 = BrawlCrate's way (the scaled normal, unnormalised) -- comparison only
            "out vec4 vChan0; out vec4 vChan1;\n" +
            "out vec2 vTc0, vTc1, vTc2, vTc3, vTc4, vTc5, vTc6, vTc7;\n" +
            "out vec3 vP; out vec3 vN;\n" +       // position + normal (one space: uViewRot takes the normal to the camera) for normal maps
            "void skin(inout vec3 p, inout vec3 nrm){\n" +
            "  mat4 B = uBones[int(aBones.x + 0.5)] * aWeights.x + uBones[int(aBones.y + 0.5)] * aWeights.y\n" +
            "         + uBones[int(aBones.z + 0.5)] * aWeights.z + uBones[int(aBones.w + 0.5)] * aWeights.w;\n" +
            "  p = (uModel * (B * vec4(aPos, 1.0))).xyz;\n" +
            "  vec3 sn = mat3(uModel) * (mat3(B) * aNormal);\n" +
            "  nrm = dot(sn, sn) > 1e-12 ? normalize(sn) : sn;\n" +
            "}\n" +
            Term +
            "vec4 chan(int c, vec3 N){\n" +
            "  ivec4 cc = uColCtrl[c]; ivec4 ac = uAlpCtrl[c]; vec4 vc = vec4(aColor.rg, floor(aColor.b) / 255.0, fract(aColor.b) / 0.999);\n" +
            "  vec3 mat = cc.y == 1 ? vc.rgb : uChanMat[c].rgb;\n" +
            "  float matA = ac.y == 1 ? vc.a : uChanMat[c].a;\n" +
            "  vec3 col = mat; float a = matA; bool sp;\n" +
            "  if (cc.x == 1) {\n" +
            "    vec3 il = (cc.z == 1 ? vc.rgb : uChanAmb[c].rgb) * uSceneAmb;\n" +
            "    for (int i = 0; i < 4; i++) { if (i >= uNumLights) break; float t = term(i, N, cc.w, uAttn[c].x, sp); il += (sp ? uLightSpecCol[i] : uLightCol[i]) * t; }\n" +
            "    col = mat * clamp(il, 0.0, 1.0);\n" +
            "  }\n" +
            "  if (ac.x == 1) {\n" +
            "    float ila = ac.z == 1 ? vc.a : uChanAmb[c].a;\n" +
            "    for (int i = 0; i < 4; i++) { if (i >= uNumLights) break; ila += term(i, N, ac.w, uAttn[c].y, sp); }\n" +
            "    a = matA * clamp(ila, 0.0, 1.0);\n" +
            "  }\n" +
            "  return vec4(col, a);\n" +
            "}\n" +
            "vec2 tg(int i, vec3 Ne){\n" +
            "  vec3 src = uTgMode[i] == 0 ? vec3(aUv, 1.0) : vec3(0.5 + 0.5 * Ne.x, 0.5 - 0.5 * Ne.y, 1.0);\n" +
            "  return (uTexMtx[i] * src).xy;\n" +
            "}\n" +
            "void main(){\n" +
            "  vec3 p = aPos; vec3 nrm = aNormal; if (uSkin == 1) skin(p, nrm);\n" +
            "  gl_Position = uMvp * vec4(p, 1.0);\n" +
            "  vP = p; vN = nrm;\n" +
            "  vec3 Ne = normalize(uViewRot * nrm);\n" +
            "  vChan0 = chan(0, Ne); vChan1 = chan(1, Ne);\n" +
            // GX normalises the normal for these maps (the texgen's normalize flag); BrawlCrate uses it as scaled
            "  vec3 Nt = uRawEnvNormal == 1 ? uViewRot * (uSkin == 1 ? nrm : aNormal) : Ne;\n" +
            "  vTc0 = tg(0, Nt); vTc1 = tg(1, Nt); vTc2 = tg(2, Nt); vTc3 = tg(3, Nt);\n" +
            "  vTc4 = tg(4, Nt); vTc5 = tg(5, Nt); vTc6 = tg(6, Nt); vTc7 = tg(7, Nt);\n" +
            "}\n";

        // One light's diffuse x attenuation term (camera space; specular = the half-angle highlight towards the viewer),
        // shared by the per-vertex channels and the per-pixel ones of a normal-mapped material.
        const string Term =
            "float term(int i, vec3 N, int dif, int attn, out bool spec){\n" +
            "  vec3 L = uLightDir[i]; float ndl = dot(N, L);\n" +
            "  float d = dif == 0 ? 1.0 : (dif == 1 ? ndl : max(ndl, 0.0));\n" +
            "  spec = attn == 1;\n" +
            "  if (!spec) return d;\n" +
            "  vec3 H = normalize(L + vec3(0.0, 0.0, 1.0));\n" +
            "  float x = ndl > 0.0 ? max(dot(N, H), 0.0) : 0.0; vec3 k = uLightSpecK[i];\n" +
            "  return d * (x * x) / max(k.x + k.y * x + k.z * x * x, 1e-6);\n" +
            "}\n";

        public const string Frag =
            "#version 300 es\n" +
            "precision highp float;\n" +
            "precision highp int;\n" +
            "in vec4 vChan0; in vec4 vChan1;\n" +
            "in vec2 vTc0, vTc1, vTc2, vTc3, vTc4, vTc5, vTc6, vTc7;\n" +
            "out vec4 o;\n" +
            "uniform sampler2D uTex0, uTex1, uTex2, uTex3, uTex4, uTex5, uTex6, uTex7;\n" +
            "uniform int uNumStages;\n" +
            "uniform ivec4 uCabcd[8]; uniform ivec4 uCmod[8]; uniform ivec4 uAabcd[8]; uniform ivec4 uAmod[8];\n" +
            "uniform ivec4 uDst[8];\n" +     // colour dest, alpha dest, texmap (-1 none), texcoord
            "uniform ivec4 uKsel[8];\n" +    // konst colour sel, konst alpha sel, raster swap table, texture swap table
            "uniform int uRasChan[8];\n" +   // 0 channel 0, 1 channel 1, 7 zero
            "uniform ivec4 uSwap[4];\n" +
            "uniform vec4 uReg[4]; uniform vec4 uKonst[4];\n" +
            "uniform ivec4 uAlphaTest; uniform vec2 uAlphaRef;\n" +   // comp0, comp1, logic, on
            "uniform int uBlendAlpha; uniform float uExposure;\n" +
            // a recolour for the first-person gun's lights (MPH's per-weapon colour): the brightest channel times the tint.
            // w = 0 leaves the material alone (the default); w = 1 recolours the whole material (Retro's light materials);
            // w = 2 recolours only what texture unit uTintUnit reads (Beyond's glow map, which shares its material with
            // the plating it lights); w = 3+k (k < 1) a match seat's team colour on a native body: the colour pulled k of
            // the way toward the tint by its brightness (blacks lifted a little); w = 5+k the same on its unsaturated
            // armour only, its lights keep their colour (a same-character duplicate); w = 7+k as 3+k with the bright parts
            // lit well above the rest (the troopers; LookAlikes.BodyTint); w = 9+k two tones (a same-look copy): its
            // armour toward uTint by brightness, its lights (saturated and bright) toward uTint2
            "uniform vec4 uTint; uniform int uTintUnit; uniform vec4 uTint2;\n" +
            // w = 11+k Samus's HD suit in a team / same-hunter copy's colours (SuitColours), on its textures before lighting
            // (environment maps aside): its colour groups -- uTintHue main, second, third (with the second) and lights hues --
            // to uTint (main), uTint2 (second), uTint3 (lights); uTintRef: the main, second and grey groups' mean texel
            // brightness on its paint, w where its greys go (0 stay, 1 main, 2 second); uTint4: its helmet/chest group (a
            // Varia's red: MPH Samus's helmet/chest colour, a deeper main), w its hue (-1 none); uTint3.w its brightness
            "uniform vec4 uTint3; uniform vec4 uTintHue; uniform vec4 uTintRef; uniform vec4 uTint4;\n" +
            // ... and its colour-group maps (SuitClasses: R second, G helmet/chest, B lights, A recoloured), one per texmap that
            // has one (uClsMask's bits; none: the groups by hue, as before)
            "uniform sampler2D uCls0, uCls1, uCls2, uCls3, uCls4, uCls5, uCls6, uCls7; uniform int uClsMask;\n" +
            // ... the texmaps (bits) bound already recoloured, per texel at their own size and then mipmapped (CampaignSuit.Bake)
            "uniform int uBaked;\n" +
            // a normal-mapped material (GxJson "nmap"): channel 0 and the reflection texcoords again, per pixel
            "in vec3 vP; in vec3 vN;\n" +
            "uniform int uNmap; uniform int uNmapTc;\n" +   // the normal map's texmap unit + 1 (0 = none, as an unset uniform is) and texcoord
            "uniform mat3 uViewRot;\n" +
            "uniform ivec4 uColCtrl[2]; uniform ivec2 uAttn[2]; uniform vec4 uChanMat[2]; uniform vec4 uChanAmb[2];\n" +
            "uniform vec3 uSceneAmb; uniform int uNumLights;\n" +
            "uniform vec3 uLightDir[4]; uniform vec3 uLightCol[4]; uniform vec3 uLightSpecCol[4]; uniform vec3 uLightSpecK[4];\n" +
            "uniform int uTgMode[8]; uniform mat3 uTexMtx[8];\n" +
            // MPH's double damage on a custom body (DoubleDamageLook): uDD 1 on, its texture, the turning env-map matrix
            // (its texture on unit 15, read through uCls7: no colour-group maps under double damage, and a 17th sampler
            // broke the link on Adreno, max 16: every HD suit fell back on the Odin, 2026-10-26)
            "uniform int uDD; uniform mat3 uDDMtx;\n" +
            Term +
            "vec3 gC[4]; float gA[4]; vec4 gTex; vec4 gRas; int gKc; int gKa;\n" +
            "bool gPix; vec3 gNe;\n" +                      // per pixel: the bent camera-space normal
            "vec2 tcv(int i){ if(i==0) return vTc0; if(i==1) return vTc1; if(i==2) return vTc2; if(i==3) return vTc3;\n" +
            "  if(i==4) return vTc4; if(i==5) return vTc5; if(i==6) return vTc6; return vTc7; }\n" +
            "vec2 tc(int i){ if(gPix && uTgMode[i]==1) return (uTexMtx[i] * vec3(0.5 + 0.5 * gNe.x, 0.5 - 0.5 * gNe.y, 1.0)).xy; return tcv(i); }\n" +
            // a trooper's coloured parts: coloured or bright as drawn (ts, tl: his plates take their yellow or white from the
            // material, not the texture), but bright-only counts only where the texture itself is bright (the black armour's shine
            // lit it bright enough to colour, owner 2026-10-29)
            // a trooper with his glow layer e: where e glows, the seat colour at full strength at the body+glow brightness (the
            // body's own paint under the glow, white on Ice, washed the colour pale: every trooper type now shows one shade per
            // seat, owner 2026-10-29 "I want the shades identical"); the rest of the body as drawn without it
            "vec3 trg(vec3 fc, vec3 e){ float m=max(uTint.r,max(uTint.g,uTint.b)), x=max(e.r,max(e.g,e.b)); vec3 b=max(fc,vec3(0.0));\n" +
            "  return mix(b, uTint.rgb/max(m,0.01)*min(max(b.r,max(b.g,b.b))+x,1.0), smoothstep(0.03,0.3,x)); }\n" +
            // a trooper's body texture (his 1st and 3rd stage) carries painted stripes in his colour (Wave purple, Plasma red,
            // Ice white): a copy turns only those texels to the seat colour at the same brightness, the black ones kept
            "vec3 trs(vec3 t, float k){ float l=max(t.r,max(t.g,t.b)); float s=l>0.0?(l-min(t.r,min(t.g,t.b)))/l:0.0; float m=max(uTint.r,max(uTint.g,uTint.b));\n" +
            "  return mix(t, uTint.rgb/max(m,0.01)*l, k*max(smoothstep(0.3,0.5,s)*smoothstep(0.04,0.1,l), smoothstep(0.55,0.75,l))); }\n" +
            "float trp(vec4 t, float ts, float tl){ float a=smoothstep(0.3,0.5,ts)*smoothstep(0.1,0.25,tl), b=smoothstep(0.42,0.6,tl);\n" +
            "  if(t.a<0.0) return max(a,b); float x=max(t.r,max(t.g,t.b)); return max(a*smoothstep(0.04,0.1,x), b*smoothstep(0.5,0.7,x)); }\n" +
            "vec4 smp(int m, vec2 c){ if(m==0) return texture(uTex0,c); if(m==1) return texture(uTex1,c); if(m==2) return texture(uTex2,c);\n" +
            "  if(m==3) return texture(uTex3,c); if(m==4) return texture(uTex4,c); if(m==5) return texture(uTex5,c);\n" +
            "  if(m==6) return texture(uTex6,c); return texture(uTex7,c); }\n" +
            // the same texel at a coarse mip (about 32 texels across at 256): the surrounding hue, for the suit recolour's group choice
            "vec4 smpB(sampler2D t, vec2 c){ ivec2 z=textureSize(t,0); return textureLod(t,c,clamp(log2(float(min(z.x,z.y)))-3.0,0.0,5.0)); }\n" +
            "vec4 smpL(int m, vec2 c){ if(m==0) return smpB(uTex0,c); if(m==1) return smpB(uTex1,c); if(m==2) return smpB(uTex2,c);\n" +
            "  if(m==3) return smpB(uTex3,c); if(m==4) return smpB(uTex4,c); if(m==5) return smpB(uTex5,c);\n" +
            "  if(m==6) return smpB(uTex6,c); return smpB(uTex7,c); }\n" +
            "vec4 smpK(int m, vec2 c){ if(((uClsMask>>m)&1)==0) return vec4(-1.0); if(m==0) return texture(uCls0,c); if(m==1) return texture(uCls1,c);\n" +
            "  if(m==2) return texture(uCls2,c); if(m==3) return texture(uCls3,c); if(m==4) return texture(uCls4,c); if(m==5) return texture(uCls5,c);\n" +
            "  if(m==6) return texture(uCls6,c); return texture(uCls7,c); }\n" +
            // the normal map read in the surface's own frame, built from screen derivatives (no tangents in the mesh)
            "void bend(){\n" +
            "  vec2 uv = tcv(uNmapTc); vec3 N0 = normalize(vN);\n" +
            "  vec3 q1 = dFdx(vP), q2 = dFdy(vP); vec2 t1 = dFdx(uv), t2 = dFdy(uv);\n" +
            "  vec3 r1 = cross(q2, N0), r2 = cross(N0, q1);\n" +
            "  vec3 T = r1 * t1.x + r2 * t2.x, B = r1 * t1.y + r2 * t2.y;\n" +
            "  float m = max(dot(T, T), dot(B, B)); if (m <= 0.0) return;\n" +
            "  m = inversesqrt(m); vec3 nt = smp(uNmap - 1, uv).xyz * 2.0 - 1.0;\n" +
            "  gNe = normalize(uViewRot * (T * (m * nt.x) + B * (m * nt.y) + N0 * nt.z)); gPix = true;\n" +
            "}\n" +
            // channel 0 lit by the bent normal (register material + ambient, as GxJson writes them; vertex-coloured
            // channels keep the per-vertex result)
            "vec4 chanPix(vec4 v){\n" +
            "  ivec4 cc = uColCtrl[0]; if (cc.x != 1 || cc.y == 1 || cc.z == 1) return v;\n" +
            "  vec3 il = uChanAmb[0].rgb * uSceneAmb; bool sp;\n" +
            "  for (int i = 0; i < 4; i++) { if (i >= uNumLights) break; float t = term(i, gNe, cc.w, uAttn[0].x, sp); il += (sp ? uLightSpecCol[i] : uLightCol[i]) * t; }\n" +
            "  return vec4(uChanMat[0].rgb * clamp(il, 0.0, 1.0), v.a);\n" +
            "}\n" +
            "vec4 swz(vec4 c, ivec4 s){ return vec4(c[s.x], c[s.y], c[s.z], c[s.w]); }\n" +
            "vec3 konstC(){ int s=gKc;\n" +
            "  if(s<=7) return vec3(float(8-s)/8.0);\n" +
            "  if(s>=12 && s<=15) return uKonst[s-12].rgb;\n" +
            "  if(s>=16 && s<=19) return vec3(uKonst[s-16].r);\n" +
            "  if(s>=20 && s<=23) return vec3(uKonst[s-20].g);\n" +
            "  if(s>=24 && s<=27) return vec3(uKonst[s-24].b);\n" +
            "  if(s>=28 && s<=31) return vec3(uKonst[s-28].a);\n" +
            "  return vec3(0.0); }\n" +
            "float konstA(){ int s=gKa;\n" +
            "  if(s<=7) return float(8-s)/8.0;\n" +
            "  if(s>=16 && s<=19) return uKonst[s-16].r;\n" +
            "  if(s>=20 && s<=23) return uKonst[s-20].g;\n" +
            "  if(s>=24 && s<=27) return uKonst[s-24].b;\n" +
            "  if(s>=28 && s<=31) return uKonst[s-28].a;\n" +
            "  return 0.0; }\n" +
            "vec3 cin(int s){\n" +
            "  if(s==0) return gC[0]; if(s==1) return vec3(gA[0]);\n" +
            "  if(s==2) return gC[1]; if(s==3) return vec3(gA[1]);\n" +
            "  if(s==4) return gC[2]; if(s==5) return vec3(gA[2]);\n" +
            "  if(s==6) return gC[3]; if(s==7) return vec3(gA[3]);\n" +
            "  if(s==8) return gTex.rgb; if(s==9) return vec3(gTex.a);\n" +
            "  if(s==10) return gRas.rgb; if(s==11) return vec3(gRas.a);\n" +
            "  if(s==12) return vec3(1.0); if(s==13) return vec3(0.5);\n" +
            "  if(s==14) return konstC(); return vec3(0.0); }\n" +
            "float ain(int s){\n" +
            "  if(s==0) return gA[0]; if(s==1) return gA[1];\n" +
            "  if(s==2) return gA[2]; if(s==3) return gA[3];\n" +
            "  if(s==4) return gTex.a; if(s==5) return gRas.a;\n" +
            "  if(s==6) return konstA(); return 0.0; }\n" +
            "float bias(int b){ return b==1?0.5:(b==2?-0.5:0.0); }\n" +
            "float scl(int s){ return s==1?2.0:(s==2?4.0:(s==3?0.5:1.0)); }\n" +
            "bool cmp(int f, float a, float r){ if(f==0) return false; if(f==1) return a<r; if(f==2) return abs(a-r)<0.002;\n" +
            "  if(f==3) return a<=r; if(f==4) return a>r; if(f==5) return abs(a-r)>=0.002; if(f==6) return a>=r; return true; }\n" +
            "float tHue(vec3 c, float mx, float mn){ float d=mx-mn; if(d<=0.0) return 0.0; float h;\n" +
            "  if(mx==c.r) h=mod((c.g-c.b)/d,6.0); else if(mx==c.g) h=(c.b-c.r)/d+2.0; else h=(c.r-c.g)/d+4.0; return h*60.0; }\n" +
            "float tHd(float a, float b){ if(b<0.0) return 999.0; float d=mod(abs(a-b),360.0); return min(d,360.0-d); }\n" +
            // Prime 4 Sylux (uTint2.w>0.5, only ever her): only her saturated blue armour takes the colour t; her grey
            // underlayer (chest core, waist, thighs) stays dark grey with a hint of the colour, as vanilla's (owner 2026-10-29)
            "vec3 tSyl(vec3 t, float ts, float tl){ if(uTint2.w<0.5) return t; float m=max(uTint.r,max(uTint.g,uTint.b));\n" +
            "  return mix(tl*mix(vec3(0.85),uTint.rgb/max(m,0.01),0.3), t, smoothstep(0.38,0.5,ts)); }\n" +
            // a suit texel in the seat's colours; ro 0 paint (lit: a group's shading kept at 65% around its new colour), 1 a mask
            // (shine: hue only, its greys stay), 2 a glow
            "vec3 suitc(vec3 c, vec3 cb, vec4 k, int ro){ float v=max(c.r,max(c.g,c.b)), n=min(c.r,min(c.g,c.b)); \n" +
            // the face behind the visor: the glass's colour, flat and dark so lighting barely shades it and no features
            // show through it; a blur kept PED's eyes and brows, and at half brightness the eye sockets still read (owner 2026-10-24)
            "  if(k.r>k.a+0.5){ float ml0=max(uTint3.r,max(uTint3.g,uTint3.b)); return min(uTint3.rgb*(0.2/max(ml0,0.01)),vec3(1.0)); }\n" +
            "  if(v<0.02) return c;\n" +
            "  float s=(v-n)/v, h=tHue(c,v,n), hb=tHue(cb,max(cb.r,max(cb.g,cb.b)),min(cb.r,min(cb.g,cb.b)));\n" +
            "  float dm=tHd(h,uTintHue.x), ds=min(tHd(h,uTintHue.y),tHd(h,uTintHue.z)), dl=tHd(h,uTintHue.w), dc=tHd(h,uTint4.w);\n" +
            "  float bm=tHd(hb,uTintHue.x), bs=min(tHd(hb,uTintHue.y),tHd(hb,uTintHue.z)), bc=tHd(hb,uTint4.w);\n" +
            "  float mm=max(uTint.r,max(uTint.g,uTint.b)), ms=max(uTint2.r,max(uTint2.g,uTint2.b)), ml=max(uTint3.r,max(uTint3.g,uTint3.b)), mc=max(uTint4.r,max(uTint4.g,uTint4.b));\n" +
            "  float fm=ro==0 ? 0.35+0.65*v/uTintRef.x : v/max(mm,0.01), fs=ro==0 ? 0.35+0.65*v/uTintRef.y : v/max(ms,0.01);\n" +
            "  float fch=ro==0 ? 0.35+0.65*v/uTint3.w : v/max(mc,0.01);\n" +
            "  vec3 cm=min(uTint.rgb*fm,vec3(1.0)), cs=min(uTint2.rgb*fs,vec3(1.0)), cc=min(uTint4.rgb*fch,vec3(1.0));\n" +
            "  vec3 hc; float hw, lh;\n" +
            // the texel's group from its map (whole patches, no bleed across seams) ...
            "  if(k.a>=0.0){ hc=(cm*max(k.a-k.r-k.g,0.0)+cs*k.r+cc*k.g)/max(k.a,0.004); hw=k.a; lh=k.b; }\n" +
            // ... or, without one, from the surrounding hue
            "  else { hc=mix(mix(cm, cs, smoothstep(-5.0,5.0,bm-bs)), cc, smoothstep(-4.0,4.0,min(bm,bs)-bc));\n" +
            "    hw=1.0-smoothstep(25.0,38.0,min(min(dm,ds),dc)); lh=1.0-smoothstep(25.0,40.0,dl); }\n" +
            "  vec3 r=mix(c,hc,hw);\n" +
            // (a map's glass, R = 1/255 on lights: lights colour whole, its pale highlight too, SuitClasses.Glass)
            "  bool gl=k.a>0.5 && k.b>0.5 && k.r>0.002 && k.r<0.02 && k.g<0.002;\n" +
            "  float lw=gl ? 1.0 : ro==2 ? max(lh,1.0-hw) : lh*smoothstep(0.45,0.6,s)*smoothstep(0.35,0.5,v);\n" +
            // the visor/cannon take their brightness mostly from the blurred sample: its gradient stays, the face behind the glass doesn't show
            // (not a seam: the blur smeared Beta Phazon's thin seams dark, owner 2026-10-29)
            "  float vl=mix(v, max(cb.r,max(cb.g,cb.b)), (k.a==0.0 && k.b>0.5) ? 0.0 : 0.8);\n" +
            // a mapped glow is no brighter to the eye than the suit's own (Phazon's dim red veins went bright yellow-green
            // blobs, owner 2026-10-24); the Dark Suit dots and the unmapped path keep their strength
            "  vec3 lc=min(uTint3.rgb*(vl/max(ml,0.01)),vec3(1.0));\n" +
            // (glass: the blurred sample's brightness, so its pale highlight strip isn't a lighter line)
            "  if(k.a>0.5){ float lo=gl ? dot(cb,vec3(0.299,0.587,0.114))*(vl/max(max(cb.r,max(cb.g,cb.b)),0.01)) : dot(c,vec3(0.299,0.587,0.114))*(vl/max(v,0.01)),ln=dot(lc,vec3(0.299,0.587,0.114)); if(ln>lo) lc*=mix(1.0,lo/max(ln,0.001),0.75); }\n" +
            "  r=mix(r, lc, lw);\n" +
            // greys: kept (a mask/glow, a suit whose greys stay, or a map's kept patch: the dark under-suit), tinted with the
            // second or helmet/chest group where the map puts them there (weathering in that paint), else the greys' group
            "  vec3 g=(ro!=0 || (k.a>=0.0 && k.a<0.5)) ? c\n" +
            "    : (k.a>=0.5 && k.r>0.5) ? min(uTint2.rgb*(0.35+0.65*v/uTintRef.y),vec3(1.0)) : (k.a>=0.5 && k.g>0.5) ? min(uTint4.rgb*(0.35+0.65*v/uTint3.w),vec3(1.0))\n" +
            "    : uTintRef.w<0.5 ? c : min((uTintRef.w>1.5 ? uTint2.rgb : uTint.rgb)*(0.35+0.65*v/uTintRef.z),vec3(1.0));\n" +
            "  return gl ? r : mix(g, r, smoothstep(0.12,0.25,s)); }\n" +
            "void main(){\n" +
            "  for(int i=0;i<4;i++){ gC[i]=uReg[i].rgb; gA[i]=uReg[i].a; }\n" +
            "  gPix = false; gNe = vec3(0.0, 0.0, 1.0);\n" +
            "  if (uNmap > 0) bend();\n" +
            "  vec4 ch0 = gPix ? chanPix(vChan0) : vChan0;\n" +
            "  bool anyFs=false;\n" +
            // the first texture's own colour, before lighting: a trooper copy colours only his coloured texels (the lit
            // colour made his black armour's shine bright enough to colour, owner 2026-10-29)
            "  vec4 gT0=vec4(-1.0);\n" +
            // a trooper's glow layer: his 2nd stage adds a texture (x his KONST colour on Power) onto the lit black body; that
            // is exactly his coloured parts, so a copy swaps only it (owner 2026-10-29 "isolate the colored parts")
            "  vec3 gE=vec3(-1.0), gB=vec3(0.0), gS=vec3(0.0);\n" +
            "  for(int s=0;s<8;s++){\n" +
            "    if(s>=uNumStages) break;\n" +
            "    int tm=uDst[s].z;\n" +
            "    gTex = tm>=0 ? swz(smp(tm, tc(uDst[s].w)), uSwap[uKsel[s].w]) : vec4(0.0);\n" +
            "    if(uTint.w>1.5 && uTint.w<2.5 && tm==uTintUnit) gTex.rgb=uTint.rgb*max(gTex.r,max(gTex.g,gTex.b));\n" +
            "    if(tm>=0 && gT0.a<-0.5) gT0=vec4(gTex.rgb,1.0);\n" +
            "    if(uTint2.w<-0.5 && (s==0 || s==2) && tm>=0 && uTint.w>6.5 && uTint.w<10.5) gTex.rgb=trs(gTex.rgb, clamp(uTint.w>8.5 ? uTint.w-9.0 : uTint.w-7.0, 0.0, 1.0));\n" +
            "    bool fs=false; int roS=-1;\n" +
            "    if(uTint.w>10.5 && tm>=0 && uTgMode[uDst[s].w]==0){ ivec4 ci=uCabcd[s]; vec2 uv=tc(uDst[s].w); vec4 kk=smpK(tm, uv); fs=kk.r>kk.a+0.5;\n" +
            "      bool ras=any(equal(ci,ivec4(10))), reg=uDst[s].x!=0 || any(bvec4(ci.x>=2&&ci.x<=7, ci.y>=2&&ci.y<=7, ci.z>=2&&ci.z<=7, ci.w>=2&&ci.w<=7));\n" +
            "      int ro=ras ? 0 : (reg ? 1 : 2); roS=ro;\n" +
            // (a texture already recoloured texel by texel (uBaked: CampaignSuit's piece-by-piece CPU bake) is sampled as it is)
            "      vec3 rr=((uBaked>>tm)&1)!=0 ? gTex.rgb : suitc(gTex.rgb, smpL(tm, uv).rgb, kk, ro);\n" +
            "      if(((uBaked>>tm)&1)==0) gTex.rgb=mix(gTex.rgb, rr, uTint.w-11.0); }\n" +
            "    int rc = uRasChan[s];\n" +
            "    gRas = rc==0 ? ch0 : (rc==1 ? vChan1 : vec4(0.0));\n" +
            // the face's vertex colours carry baked eye-socket and lid shading (Prime 3), which drew the eyes through a
            // flat recoloured face: on face texels the raster colour is one flat grey (owner 2026-10-24)
            "    if(fs) gRas.rgb=mix(gRas.rgb, vec3(0.6), uTint.w-11.0);\n" +
            "    anyFs=anyFs||fs;\n" +
            "    gRas = swz(gRas, uSwap[uKsel[s].z]);\n" +
            "    gKc=uKsel[s].x; gKa=uKsel[s].y;\n" +
            "    vec3 a=cin(uCabcd[s].x), b=cin(uCabcd[s].y), c=cin(uCabcd[s].z), d=cin(uCabcd[s].w);\n" +
            "    vec3 lp=a*(vec3(1.0)-c)+b*c; vec3 cr=(uCmod[s].x==1)?d-lp:d+lp;\n" +
            "    cr=(cr+vec3(bias(uCmod[s].y)))*scl(uCmod[s].z);\n" +
            "    cr=(uCmod[s].w==1)?clamp(cr,0.0,1.0):clamp(cr,-4.0,4.0);\n" +
            "    float aa=ain(uAabcd[s].x), ab=ain(uAabcd[s].y), ac=ain(uAabcd[s].z), ad=ain(uAabcd[s].w);\n" +
            "    float lpa=aa*(1.0-ac)+ab*ac; float ar=(uAmod[s].x==1)?ad-lpa:ad+lpa;\n" +
            "    ar=(ar+bias(uAmod[s].y))*scl(uAmod[s].z); ar=(uAmod[s].w==1)?clamp(ar,0.0,1.0):clamp(ar,-4.0,4.0);\n" +
            "    if(s==1 && uDst[s].x==0 && uCmod[s].x==0 && uCabcd[s].x==15 && uCabcd[s].y==8 && uCabcd[s].w==0){ gE=max(lp,vec3(0.0)); gB=d; gS=cr; }\n" +
            "    gC[uDst[s].x]=cr; gA[uDst[s].y]=ar;\n" +
            "  }\n" +
            "  float outa=clamp(gA[0],0.0,1.0);\n" +
            // a blended face overlay (Prime 3's lashes and lid lines) draws after the visor glass, so the eyes showed
            // untinted through a recoloured visor: it is dropped under a full recolour (owner 2026-10-24)
            "  if(anyFs && uBlendAlpha==1 && uTint.w>11.5) discard;\n" +
            "  if(uAlphaTest.w==1){ bool p0=cmp(uAlphaTest.x,outa,uAlphaRef.x), p1=cmp(uAlphaTest.y,outa,uAlphaRef.y);\n" +
            "    bool pass = uAlphaTest.z==0 ? (p0&&p1) : (uAlphaTest.z==1 ? (p0||p1) : (uAlphaTest.z==2 ? (p0!=p1) : (p0==p1)));\n" +
            "    if(!pass) discard; }\n" +
            "  vec3 fc=clamp(gC[0],0.0,1.0);\n" +
            // the face's later stages (lighting, specular, untextured raster passes) still shaded its cheeks, nose and eye
            // sockets through the glass: under a full recolour a face pixel is one flat colour, the glass's, dark (owner 2026-10-24)
            "  if(anyFs && uTint.w>11.5){ float mf=max(uTint3.r,max(uTint3.g,uTint3.b)); fc=min(uTint3.rgb*(0.2/max(mf,0.01)),vec3(1.0)); }\n" +
            "  if(uTint.w>0.5 && uTint.w<1.5) fc=uTint.rgb*max(fc.r,max(fc.g,fc.b));\n" +
            "  if(uTint.w>2.5 && uTint.w<8.5){ bool glo=uTint.w>6.5; bool arm=uTint.w>4.5 && !glo; float tk=uTint.w-(glo?7.0:(arm?5.0:3.0));\n" +
            "    float tl=max(fc.r,max(fc.g,fc.b)); float ts=tl>0.0?(tl-min(fc.r,min(fc.g,fc.b)))/tl:0.0;\n" +
            "    vec3 tt=uTint.rgb*(0.16+0.84*tl);\n" +
            "    if(glo) tt=mix(tt*0.8, uTint.rgb*0.8+vec3(0.35), smoothstep(0.45,0.85,tl));\n" +
            "    tt=tSyl(tt,ts,tl);\n" +
            // uTint2.w -1, a trooper: only his coloured parts (saturated, or bright like the Ice trooper's white) take
            // the colour, his black armour kept (owner 2026-10-29)
            "    if(uTint2.w<-0.5 && gE.r>=0.0) tt=trg(gB+gC[0]-gS,gE); else if(uTint2.w<-0.5) tk*=trp(gT0,ts,tl);\n" +
            "    fc=mix(fc, tt, arm ? tk*(1.0-smoothstep(0.25,0.6,ts)) : tk); }\n" +
            "  if(uTint.w>8.5 && uTint.w<10.5){ float tk=uTint.w-9.0; float tl=max(fc.r,max(fc.g,fc.b)); float ts=tl>0.0?(tl-min(fc.r,min(fc.g,fc.b)))/tl:0.0;\n" +
            "    float lm=smoothstep(0.35,0.6,ts)*smoothstep(0.35,0.6,tl);\n" +
            "    if(uTint2.w>0.5) lm=smoothstep(0.3,0.5,ts)*smoothstep(0.4,0.65,tl)*(1.0-smoothstep(18.0,30.0,tHd(tHue(fc,tl,min(fc.r,min(fc.g,fc.b))),uTint2.w-1.0)));\n" +
            "    if(uTint2.w<-0.5){ lm=0.0; tk*=trp(gT0,ts,tl); }\n" +
            "    vec3 ta=tSyl(uTint.rgb*(0.28+1.0*tl),ts,tl); vec3 tb=min(uTint2.rgb*(0.55+0.75*tl)+vec3(0.10*tl),vec3(1.0));\n" +
            "    if(uTint2.w<-0.5 && gE.r>=0.0){ ta=trg(gB+gC[0]-gS,gE); tk=uTint.w-9.0; }\n" +
            "    fc=mix(fc, mix(ta,tb,lm), tk); }\n" +
            // MPH's double damage (PlayerDraw's binding, emission and texcoord overrides): a lit material shows the pickup's
            // texture as an environment map off the camera-space normal (plus its mesh UV), turning with time, lit with
            // MPH's grey emission (0x35AD) over the lights; no team colour, as in MPH (the damage flash still reddens it)
            "  if(uDD==1 && uColCtrl[0].x==1){ vec3 Nd=normalize(uViewRot*vN); vec2 du=(uDDMtx*Nd).xy+(uTgMode[0]==0?vTc0:vec2(0.0));\n" +
            "    vec3 il=uSceneAmb; for(int i=0;i<4;i++){ if(i>=uNumLights) break; il+=uLightCol[i]*max(dot(Nd,uLightDir[i]),0.0); }\n" +
            "    fc=texture(uCls7,du).rgb*min(il+vec3(0.42),vec3(1.0));\n" +
            "    if(uTint.w>0.5 && uTint.w<1.5) fc=uTint.rgb*max(fc.r,max(fc.g,fc.b)); }\n" +
            "  o = vec4(fc*uExposure, uBlendAlpha==1?outa:1.0);\n" +
            "}\n";

        // Everything a material hands the program, packed as the uniform arrays above (upload order is the caller's).
        public sealed class Params
        {
            public int NumStages;
            public int[] Cabcd = new int[32], Cmod = new int[32], Aabcd = new int[32], Amod = new int[32], Dst = new int[32], Ksel = new int[32];
            public int[] RasChan = new int[8], Swap = new int[16];
            public float[] Reg = new float[16], Konst = new float[16];
            public int[] ColCtrl = new int[8], AlpCtrl = new int[8], Attn = new int[4];
            public float[] ChanMat = new float[8], ChanAmb = new float[8];
            public int[] TgMode = new int[8];
            public float[] TexMtx = new float[72];   // 8 x mat3, column-major
            public int[] AlphaTest = new int[4]; public float[] AlphaRef = new float[2];
            public bool Blend; public int BlendSrc, BlendDst; public bool BlendSubtract; public bool DepthWrite = true;
            public bool DepthTest = true; public int DepthFunc = 3;   // the material's z compare (trophies use <=: coincident layers draw over)
            public int Cull;                          // 0 none, 1 front, 2 back, 3 all
            // per texmap unit 0..7: the layer bound there (sampler state + texture name), or null
            public GxLayer?[] Units = new GxLayer?[8];
            public bool Scrolls;                      // some layer scrolls: the caller adds time x speed to its matrix
            public int NmapUnit = -1, NmapCoord;      // a normal map's texmap unit (-1 none) and texcoord: upload uNmap = NmapUnit + 1, uNmapTc
        }

        public static Params Pack(GxMaterial m)
        {
            var p = new Params();
            int n = Math.Min(m.Stages.Length, MaxStages);
            p.NumStages = n;
            for (int i = 0; i < n; i++)
            {
                var s = m.Stages[i];
                Set4(p.Cabcd, i, s.Ca, s.Cb, s.Cc, s.Cd);
                Set4(p.Cmod, i, s.Cop, s.Cbias, s.Cscale, s.Cclamp);
                Set4(p.Aabcd, i, s.Aa, s.Ab, s.Ac, s.Ad);
                Set4(p.Amod, i, s.Aop, s.Abias, s.Ascale, s.Aclamp);
                Set4(p.Dst, i, s.Cdest & 3, s.Adest & 3, s.TexMap, s.TexCoord & 7);
                Set4(p.Ksel, i, s.KcSel == 0xFF ? 0 : s.KcSel, s.KaSel == 0xFF ? 0 : s.KaSel, s.RasSwap & 3, s.TexSwap & 3);
                p.RasChan[i] = (s.RasChan & 7) switch { 0 => 0, 1 => 1, _ => 7 };
            }
            for (int t = 0; t < 4; t++) for (int c = 0; c < 4; c++) p.Swap[t * 4 + c] = m.Swap[t, c];
            for (int i = 0; i < 4; i++)
            {
                SetF4(p.Reg, i, m.Reg[i]); SetF4(p.Konst, i, m.Konst[i]);
            }
            for (int c = 0; c < 2; c++)
            {
                var ch = m.Chan[c];
                uint cc = ch.ColorCtrl, ac = ch.AlphaCtrl;
                Set4(p.ColCtrl, c, GxChannel.Lit(cc) ? 1 : 0, GxChannel.VtxMat(cc) ? 1 : 0, GxChannel.VtxAmb(cc) ? 1 : 0, GxChannel.DiffFn(cc));
                Set4(p.AlpCtrl, c, GxChannel.Lit(ac) ? 1 : 0, GxChannel.VtxMat(ac) ? 1 : 0, GxChannel.VtxAmb(ac) ? 1 : 0, GxChannel.DiffFn(ac));
                p.Attn[c * 2] = GxChannel.Attn(cc); p.Attn[c * 2 + 1] = GxChannel.Attn(ac);
                SetF4(p.ChanMat, c, ch.Mat); SetF4(p.ChanAmb, c, ch.Amb);
            }
            for (int t = 0; t < 8; t++)
            {
                var L = m.Layers[t];
                int mode = L == null ? 0 : (L.MapMode == 0 && m.TexGenSrc[t] == 1 ? 1 : L.MapMode switch { 1 or 3 or 4 => 1, _ => 0 });
                p.TgMode[t] = mode;
                var mt = L == null ? Identity3 : L.UvMtx ?? MayaSrt(L.Scale, L.RotDeg, L.Trans);
                if (L != null && L.ScrollPerSec != Vector2.Zero) p.Scrolls = true;
                Array.Copy(mt, 0, p.TexMtx, t * 9, 9);
                if (L != null && L.TexMapId >= 0 && L.TexMapId < 8) p.Units[L.TexMapId] = L;
            }
            p.AlphaTest[0] = m.AlphaComp0; p.AlphaTest[1] = m.AlphaComp1; p.AlphaTest[2] = m.AlphaLogic; p.AlphaTest[3] = m.AlphaTest ? 1 : 0;
            p.AlphaRef[0] = m.AlphaRef0 / 255f; p.AlphaRef[1] = m.AlphaRef1 / 255f;
            p.Blend = m.BlendEnable; p.BlendSrc = m.BlendSrc; p.BlendDst = m.BlendDst; p.BlendSubtract = m.BlendSubtract; p.DepthWrite = m.DepthWrite;
            p.DepthTest = m.DepthTest; p.DepthFunc = m.DepthFunc;
            p.Cull = m.CullMode & 3;
            if (m.NormalMap >= 0 && m.NormalMap < 8 && p.Units[m.NormalMap] != null) { p.NmapUnit = m.NormalMap; p.NmapCoord = m.NormalMapCoord & 7; }
            return p;
        }

        static readonly float[] Identity3 = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };

        // The texture matrices at `seconds`: each scrolling layer moved by its speed x time (wrapped: textures repeat)
        public static float[] ScrolledTexMtx(GxMaterial m, Params p, double seconds)
        {
            var t = (float[])p.TexMtx.Clone();
            for (int i = 0; i < 8; i++)
            {
                var L = m.Layers[i];
                if (L == null || L.ScrollPerSec == Vector2.Zero) continue;
                t[i * 9 + 6] += (float)(L.ScrollPerSec.X * seconds % 1.0);
                t[i * 9 + 7] += (float)(L.ScrollPerSec.Y * seconds % 1.0);
            }
            return t;
        }

        // The vertex layout has room for rgb only; the GX program also needs the vertex ALPHA (Weavel's blade fades by
        // it), so blue carries it: whole part = blue 0..255, fraction = alpha x 0.999 (decoded in Vert).
        public static float PackBlueAlpha(float b, float a) => MathF.Round(Math.Clamp(b, 0f, 1f) * 255f) + Math.Clamp(a, 0f, 1f) * 0.999f;

        // Maya texture transform on (s, t, 1) in GX texture space (t down): rotate by the angle about the centre,
        // move by (-tx, +ty), scale about the bottom-left corner (0, 1). Returned as a column-major mat3.
        public static float[] MayaSrt(Vector2 scale, float rotDeg, Vector2 trans)
        {
            float r = rotDeg * MathF.PI / 180f, c = MathF.Cos(r), s = MathF.Sin(r);
            // s1 = c(s-.5) + s(t-.5) + .5 ; t1 = -s(s-.5) + c(t-.5) + .5 ; s2 = s1 - tx ; t2 = t1 + ty ; s3 = Ss s2 ; t3 = St (t2 - 1) + 1
            float a = scale.X * c, b = scale.X * s, e = scale.X * (-0.5f * c - 0.5f * s + 0.5f - trans.X);
            float d = -scale.Y * s, f = scale.Y * c, g = scale.Y * (0.5f * s - 0.5f * c + 0.5f + trans.Y - 1f) + 1f;
            return new[] { a, d, 0, b, f, 0, e, g, 1 };   // columns: (a,d,0) (b,f,0) (e,g,1)
        }

        // GX blend factor -> index into the caller's GL enum table (same order as GX: zero, one, src/dst colour,
        // inverse, src alpha, inverse, dst alpha, inverse); colour factors flip meaning between source and destination
        public static (int Src, int Dst) BlendFactors(Params p) => (p.BlendSrc, p.BlendDst);

        static void Set4(int[] a, int i, int x, int y, int z, int w) { a[i * 4] = x; a[i * 4 + 1] = y; a[i * 4 + 2] = z; a[i * 4 + 3] = w; }
        static void SetF4(float[] a, int i, Vector4 v) { a[i * 4] = v.X; a[i * 4 + 1] = v.Y; a[i * 4 + 2] = v.Z; a[i * 4 + 3] = v.W; }
    }
}
