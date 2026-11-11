using System;
using System.Collections.Generic;
using MphRead.Formats;
using OpenTK.Mathematics;

namespace MphRead.Entities
{
    // recomp: fitted ice (owner 2026-11-09, the Death look thread's option B). A frozen body (a Judicator freeze alive, or a
    // charged Judicator kill's frozen ragdoll) wears vanilla's own ice shell (samus_ice / nox_ice: its chunky blocks, its
    // gaps, its material) refitted node by node to its own body, instead of the shell as the ROM sizes it for its own
    // hunter: "FittedIce/<body model>", built from the user's ROM by MphRecomp.Core FittedIce.cs. HostFittedIce = the host's LOOK > HUNTERS > ICE (0 VANILLA, the default; 1 FITTED; 2 FITTED with facet
    // glints) for the hunters' own bodies; a stand-in body (HostBodyModel) always wears fitted ice, since the shell's bones
    // don't match it. A body the host draws (HostOwnIce) gets its ice from the host. First Hunt keeps its shell. Draw only:
    // vanilla's shell is still posed underneath, so the shatter (CreateIceBreakEffectBiped) works as before.
    public partial class PlayerEntity
    {
        public static int HostFittedIce { get; set; }

        // recomp: the host draws this player's ice itself (an HD or native body with its own ice pass); default off
        public bool HostOwnIce { get; set; }

        // recomp: a dev launch's ice probe (the "iceprobe" extra): 1 = every seat looks frozen all the time, 2 = the same with
        // the odd seats in vanilla's shell and the even ones fitted (the menu's FITTED or FACETED), side by side; 3 / 4 = every
        // seat vanilla / fitted (comparison shots). Look only.
        public static int HostIceProbe { get; set; }

        // recomp: the look this seat's ice draws in (HostFittedIce's values), the probe's split included
        public int HostIceLook => HostIceProbe switch
        {
            2 => (SlotIndex & 1) == 1 ? 0 : Math.Max(HostFittedIce, 1),
            3 => 0,
            4 => Math.Max(HostFittedIce, 1),
            _ => HostFittedIce
        };

        // recomp: this player wears ice right now (alive frozen, or a frozen ragdoll on screen), for a host-drawn body's ice
        public bool HostIced => _health > 0 ? (_frozenGfxTimer > 0 || HostIceProbe != 0) && !IsAltForm && !IsMorphing
            : _ragdoll != null && _ragdoll.Iced && RagdollDrawn && DrawnWhileDead;

        // recomp: what a host-drawn body's ice pass needs: the ice texture's binding, its material's lighting (diffuse /
        // ambient, 0-1) and the lights this player is lit by
        public int HostIceBindingId => _bipedIceModel.Model.Materials[0].TextureBindingId;
        public Vector3 HostIceDiffuse => _bipedIceModel.Model.Materials[0].CurrentDiffuse;
        public Vector3 HostIceAmbient => _bipedIceModel.Model.Materials[0].CurrentAmbient;
        public LightInfo HostLights => GetLightInfo();

        private static readonly Dictionary<string, ModelInstance?> _fittedIceModels = new Dictionary<string, ModelInstance?>();

        // the fitted shell for a body model, or null (the vanilla shell): built once per body and look
        private ModelInstance? FittedIceFor(Model body)
        {
            bool host = _hostBody != null && body == _hostBody.Model;
            int look = HostIceLook;
            if (HostFirstHunt || look == 0 && !host)
            {
                return null;
            }
            return FittedIceNamed((look == 2 ? "FittedIceFacets/" : "FittedIce/") + body.Name, body);
        }

        // recomp: builds a stand-in body's fitted shell now, with the body (MphRecomp.App MatchSkins.LoadStandin, before the
        // match's scene), so a seat's first freeze in it doesn't: built there it held the match for 1.5 s on the Odin (11-09).
        // The look a seat without the ice probe wears it in
        public static void HostWarmFittedIce(Model body)
        {
            FittedIceNamed((HostFittedIce == 2 ? "FittedIceFacets/" : "FittedIce/") + body.Name, body);
        }

        private static ModelInstance? FittedIceNamed(string name, Model body)
        {
            if (!_fittedIceModels.TryGetValue(name, out ModelInstance? inst))
            {
                try
                {
                    inst = Read.GetModelInstance(name);
                }
                catch (Exception ex) when (ex is ProgramException || ex is InvalidOperationException || ex is ArgumentException)
                {
                    inst = null; // (no builder loaded, or a body it can't fit: the vanilla shell)
                }
                if (inst != null && inst.Model.Nodes.Count != body.Nodes.Count)
                {
                    inst = null;
                }
                _fittedIceModels[name] = inst;
            }
            return inst;
        }

        // the ice on a posed body: vanilla's shell posed (for the shatter) and drawn, or the body's fitted shell drawn
        private void DrawBodyIce(Model body)
        {
            Model ice = _bipedIceModel.Model;
            bool shellFits = ice.Nodes.Count <= body.Nodes.Count && ice.Nodes.Count <= _bipedIceTransforms.Length;
            if (shellFits)
            {
                for (int i = 0; i < ice.Nodes.Count; i++)
                {
                    ice.Nodes[i].Animation = body.Nodes[i].Animation;
                    _bipedIceTransforms[i] = body.Nodes[i].Animation;
                }
            }
            if (HostOwnIce)
            {
                // (its material set up all the same: the host's pass reads the texture binding and lighting from it, and
                // nothing else sets them up when no seat wears vanilla's shell)
                UpdateMaterials(_bipedIceModel, recolor: 0);
                return;
            }
            ModelInstance? fitted = FittedIceFor(body);
            if (fitted != null)
            {
                Model fit = fitted.Model;
                for (int i = 0; i < fit.Nodes.Count; i++)
                {
                    fit.Nodes[i].Animation = body.Nodes[i].Animation;
                }
                fit.UpdateMatrixStack();
                UpdateMaterials(fitted, recolor: 0);
                GetDrawItems(fitted, fit.Nodes[0], alpha: 1, recolor: 0);
            }
            else if (shellFits && ice.Nodes.Count >= 2 && (_hostBody == null || body != _hostBody.Model))
            {
                ice.UpdateMatrixStack();
                UpdateMaterials(_bipedIceModel, recolor: 0);
                GetDrawItems(_bipedIceModel, ice.Nodes[0], alpha: 1, recolor: 0);
            }
        }
    }
}
