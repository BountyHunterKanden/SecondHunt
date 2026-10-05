using Android.Opengl;
using Android.Util;
using System;
using System.Collections.Generic;
using System.IO;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace MphRecomp.App;

// Linked GL programs, kept for the GL context and saved as the driver's own binary (glGetProgramBinary) in the app's
// private files, so a big program -- the GX-TEV one the HD suit and the Prime gun draw with takes ~90 ms to compile and
// link on the Odin (2026-10-03) -- links once per driver instead of in a frame of every start. Android's own shader
// cache lives in code_cache, which every APK update empties. Keyed by both sources and the driver (GL_RENDERER +
// GL_VERSION); a binary the driver refuses is compiled again and replaced. Programs are shared by key and never deleted
// by their users (a suit change reuses the program already linked); a new context starts over (Reset).
internal static class CampaignGlCache
{
    const string Tag = "MPHCampaign";
    const int ProgramBinaryRetrievableHint = 0x8257; // GL_PROGRAM_BINARY_RETRIEVABLE_HINT

    static readonly Dictionary<string, int> _programs = new();

    // the GL context was recreated: every program name is gone
    public static void Reset() => _programs.Clear();

    // GL thread: the linked program for these sources (0 if they do not compile / link)
    public static int Program(string vs, string fs, string name)
    {
        string driver = GLES30.GlGetString(GLES30.GlRenderer) + "|" + GLES30.GlGetString(GLES30.GlVersion);
        string key = Hash(vs + "\0" + fs + "\0" + driver);
        if (_programs.TryGetValue(key, out int p)) return p;
        var sw = Stopwatch.StartNew();
        string? file = null;
        try { file = Path.Combine(Android.App.Application.Context.FilesDir!.AbsolutePath, "glprog", key + ".bin"); }
        catch (Exception ex) { Log.Warn(Tag, "GL program cache: no files dir: " + ex.Message); }
        string how = "from its saved binary";
        p = file != null ? FromBinary(file) : 0;
        if (p == 0)
        {
            how = "compiled";
            p = Link(vs, fs, name);
            if (p != 0 && file != null) how += Save(p, file) ? " (binary saved)" : " (binary not saved)";
        }
        Log.Info(Tag, $"GL program {name}: {how} in {sw.Elapsed.TotalMilliseconds:0.0} ms");
        if (p != 0) _programs[key] = p;
        return p;
    }

    static string Hash(string s)
    {
        byte[] h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h, 0, 16);
    }

    // file: the binary format (int32 LE), then the binary
    static int FromBinary(string file)
    {
        try
        {
            if (!File.Exists(file)) return 0;
            byte[] data = File.ReadAllBytes(file);
            if (data.Length <= 4) return 0;
            int format = BitConverter.ToInt32(data, 0);
            var bb = Java.Nio.ByteBuffer.AllocateDirect(data.Length - 4)!;
            bb.Put(data, 4, data.Length - 4);
            bb.Position(0);
            int p = GLES30.GlCreateProgram();
            GLES30.GlProgramBinary(p, format, bb, data.Length - 4);
            var st = new int[1];
            GLES30.GlGetProgramiv(p, GLES30.GlLinkStatus, st, 0);
            if (st[0] != 0) return p;
            GLES30.GlDeleteProgram(p);
            Log.Info(Tag, $"GL program cache: {Path.GetFileName(file)} refused by the driver, compiling again");
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "GL program cache: read failed: " + ex.Message);
        }
        return 0;
    }

    static bool Save(int p, string file)
    {
        try
        {
            var len = new int[1];
            GLES30.GlGetProgramiv(p, GLES30.GlProgramBinaryLength, len, 0);
            if (len[0] <= 0) return false;
            var bb = Java.Nio.ByteBuffer.AllocateDirect(len[0])!;
            int[] got = new int[1], format = new int[1];
            GLES30.GlGetProgramBinary(p, len[0], got, 0, format, 0, bb);
            if (got[0] <= 0) return false;
            var data = new byte[4 + got[0]];
            BitConverter.GetBytes(format[0]).CopyTo(data, 0);
            bb.Position(0);
            bb.Get(data, 4, got[0]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file + ".tmp", data);
            File.Move(file + ".tmp", file, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "GL program cache: save failed: " + ex.Message);
            return false;
        }
    }

    static int Link(string vs, string fs, string name)
    {
        int v = Compile(GLES30.GlVertexShader, vs, name), f = Compile(GLES30.GlFragmentShader, fs, name);
        if (v == 0 || f == 0)
        {
            if (v != 0) GLES30.GlDeleteShader(v);
            if (f != 0) GLES30.GlDeleteShader(f);
            return 0;
        }
        int p = GLES30.GlCreateProgram();
        GLES30.GlAttachShader(p, v);
        GLES30.GlAttachShader(p, f);
        GLES30.GlProgramParameteri(p, ProgramBinaryRetrievableHint, 1);
        GLES30.GlLinkProgram(p);
        GLES30.GlDeleteShader(v);
        GLES30.GlDeleteShader(f);
        var st = new int[1];
        GLES30.GlGetProgramiv(p, GLES30.GlLinkStatus, st, 0);
        if (st[0] != 0) return p;
        Log.Error(Tag, $"GL program {name}: link: " + GLES30.GlGetProgramInfoLog(p));
        GLES30.GlDeleteProgram(p);
        return 0;
    }

    static int Compile(int type, string src, string name)
    {
        int s = GLES30.GlCreateShader(type);
        GLES30.GlShaderSource(s, src);
        GLES30.GlCompileShader(s);
        var st = new int[1];
        GLES30.GlGetShaderiv(s, GLES30.GlCompileStatus, st, 0);
        if (st[0] != 0) return s;
        Log.Error(Tag, $"GL program {name}: compile: " + GLES30.GlGetShaderInfoLog(s));
        GLES30.GlDeleteShader(s);
        return 0;
    }
}
