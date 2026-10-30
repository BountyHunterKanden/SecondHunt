using System;
using System.Linq;
using MphRead.Entities;
using OpenTK.Windowing.Common;

namespace MphRead
{
    // Loops one of a DS hunter's own animation clips on the whole body in MphRead's unmodified viewer, to compare
    // by eye with the Odin's clip mode (--es animview <Hunter> --es animclip <N>, --es animframe <N> to hold a frame).
    // Run: MphRead.Tools.dll -dsview <Hunter> <clip name or number> [--info]   e.g. -dsview Samus ChargeShoot
    // (--info prints the model's bones and meshes and exits without opening the window)
    // MphRead's own keys still work: Enter pause/resume, . (period) one frame while paused, R reset camera,
    // mouse drag orbit, scroll zoom. The window title shows the frame number (same numbering as animframe).
    internal static class DsView
    {
        public static void Run(string[] args)
        {
            if (args.Length < 3 || !Enum.TryParse(args[1], ignoreCase: true, out Hunter hunter)
                || !Metadata.HunterModels.ContainsKey(hunter)
                || !Enum.TryParse(args[2], ignoreCase: true, out PlayerAnimation clip) || clip < 0)
            {
                Console.WriteLine("usage: -dsview <Hunter> <clip name or number>   e.g. -dsview Samus ChargeShoot");
                Console.WriteLine("hunters: Samus Kanden Trace Sylux Noxus Spire Weavel Guardian");
                Console.WriteLine("clips: " + String.Join(" ", Enum.GetValues<PlayerAnimation>().Where(a => a >= 0)));
                return;
            }
            if (!ToolPaths.UseDesktopExtraction())
            {
                return;
            }
            // what MphRead's M key steps through: the model's bones (nodes), then its meshes, named by material
            Model m = Read.GetModelInstance(Metadata.HunterModels[hunter][0]).Model;
            Console.WriteLine($"{m.Nodes.Count} bones: {String.Join(" ", m.Nodes.Select(n => n.Name))}");
            Console.WriteLine($"{m.Meshes.Count} meshes: {String.Join("  ", m.Meshes.Select((s, i) => $"[{i}] {m.Materials[s.MaterialId].Name}"))}");
            if (args.Contains("--info"))
            {
                return;
            }
            using var window = new DsViewWindow(hunter, clip);
            window.Run();
        }
    }

    internal sealed class DsViewWindow : RenderWindow
    {
        private readonly ModelInstance _inst;
        private readonly int _clip;
        private readonly string _label;
        private bool _started;
        private int _tick;
        private int _lastFrame = -1;

        public DsViewWindow(Hunter hunter, PlayerAnimation clip)
        {
            _inst = Scene.AddModel(Metadata.HunterModels[hunter][0]).GetModels()[0];
            _clip = (int)clip;
            if (_clip >= _inst.Model.AnimationGroups.Node.Count || _inst.Model.AnimationGroups.Node[_clip].Count == 0)
            {
                throw new ProgramException($"{hunter} has no {clip} clip.");
            }
            _label = $"{hunter} {clip} ({_clip})";
            Console.WriteLine($"{_label}, {_inst.Model.AnimationGroups.Node[_clip].FrameCount} frames, looping");
            Console.WriteLine("Enter = pause/resume, . = step one frame while paused, R = reset camera, drag = orbit, scroll = zoom");
        }

        // MphRead's scene only advances entity animations when a room is loaded (Scene.OnUpdateFrame), so a model
        // shown on its own stays on one frame. Advance it here the way EntityBase.UpdateAnimFrames does: every
        // second 60 Hz frame (the game's 30 fps), or one animation frame per . press while paused.
        protected override void OnRenderFrame(FrameEventArgs args)
        {
            if (!_started)
            {
                _inst.SetAnimation(_clip);
                _started = true;
            }
            else if (Scene.ProcessFrame && (Scene.FrameAdvance || ++_tick % 2 == 0))
            {
                _inst.UpdateAnimFrames();
            }
            base.OnRenderFrame(args);
            int frame = _inst.AnimInfo.Frame[0];
            if (frame != _lastFrame)
            {
                _lastFrame = frame;
                Title = $"{_label}  frame {frame} of 0-{_inst.AnimInfo.FrameCount[0] - 1}";
            }
        }
    }
}
