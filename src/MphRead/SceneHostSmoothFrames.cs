using OpenTK.Mathematics;

namespace MphRead
{
    // recomp: smooth frames (the campaign host's CampaignSmoothFrames.cs). The simulation steps 60 times a second, but a
    // 120 Hz or faster display draws pictures in between; the host draws those from a view carried on past the last
    // step. HostPresentView swaps that view in for the draw (view matrix, billboard rotations, camera position) and
    // hands back the step's own, which HostRestoreView puts back afterwards, so nothing the next step reads changes.
    public partial class Scene
    {
        public readonly struct HostViewState
        {
            public readonly Matrix4 View;
            public readonly Matrix4 InvRot;
            public readonly Matrix4 InvRotY;
            public readonly Vector3 Position;

            public HostViewState(Matrix4 view, Matrix4 invRot, Matrix4 invRotY, Vector3 position)
            {
                View = view;
                InvRot = invRot;
                InvRotY = invRotY;
                Position = position;
            }
        }

        public HostViewState HostPresentView(Matrix4 view)
        {
            var saved = new HostViewState(_viewMatrix, _viewInvRotMatrix, _viewInvRotYMatrix, _cameraPosition);
            _viewMatrix = view;
            // as TransformCamera works them out
            _viewInvRotMatrix = Matrix4.Transpose(view.ClearTranslation());
            _viewInvRotYMatrix = Matrix4.Identity;
            if (_viewInvRotMatrix.Row0.X != 0 || _viewInvRotMatrix.Row0.Z != 0)
            {
                _viewInvRotYMatrix.Row0.Xyz = new Vector3(_viewInvRotMatrix.Row0.X, 0, _viewInvRotMatrix.Row0.Z).Normalized();
                _viewInvRotYMatrix.Row2.Xyz = new Vector3(_viewInvRotMatrix.Row2.X, 0, _viewInvRotMatrix.Row2.Z).Normalized();
            }
            _cameraPosition = view.Inverted().Row3.Xyz;
            return saved;
        }

        public void HostRestoreView(in HostViewState saved)
        {
            _viewMatrix = saved.View;
            _viewInvRotMatrix = saved.InvRot;
            _viewInvRotYMatrix = saved.InvRotY;
            _cameraPosition = saved.Position;
        }
    }
}
