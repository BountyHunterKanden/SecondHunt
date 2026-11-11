namespace MphRead.Entities
{
    // recomp: an analog move stick (owner 2026-11-08, "subtle movements are subtle and quick movements are quick"). The DS
    // d-pad is all or nothing, and so was our stick past its dead zone: the lightest push gave the whole traction and the
    // whole top speed. The host sets how far the stick is pushed (CampaignInput.MovePush, shaped by Input/StickCurve.cs;
    // 1 on keys, the d-pad, a full tilt, CLASSIC, and for bots); walking and every alt form scale their traction and
    // their speed cap by it. At 1 nothing changes: vanilla's turning, top speed and feel stay as they were. A jump pad's
    // launch, air momentum above the cap and the slide off a slope are untouched (the cap only keeps a part push from
    // speeding up past its share). Online the push rides in NetInput's move byte; replays store it per frame (format 88).
    // The stick's direction rides in NetInput's touch byte (LAN 26 / online 22) and replays store it (format 89).
    public partial class PlayerEntity
    {
        public float HostMovePush { get; set; } = 1;
        private float MovePushScale => IsBot ? 1 : IsAltForm && GameState.Mode == GameMode.SinglePlayer ? AltPush(HostMovePush) : HostMovePush;

        // campaign alt forms (owner 2026-11-11, "smaller movements on the stick to be just a bit more subtle still"): light
        // pushes roll gentler still, easing back in to unchanged from half a push up
        private static float AltPush(float p) => p >= 0.5f ? p : p * (0.6f + 0.4f * p / 0.5f);

        // 360-degree movement (owner 2026-11-08, "360 movement when you are not using dpad"): the d-pad steers in eight
        // directions, its right/left and up/down each a whole press or none. With a stick direction (Core StickCurve.Axes)
        // each axis takes its part of the press instead, -1..1: both whole at 45 degrees like the d-pad's diagonal, so the
        // d-pad's eight directions move exactly as vanilla and the ones between steer between them. The d-pad's bits still
        // pick the animations. Off for bots, keys, the d-pad and CLASSIC (HostMoveAnalog false)
        public bool HostMoveAnalog { get; set; }
        public float HostMoveRight { get; set; }
        public float HostMoveForward { get; set; }

        private bool MoveAnalog => HostMoveAnalog && !IsBot;

        // right (+) / left (-): the d-pad's bits as vanilla reads them (right before left), or the stick's part
        private float MoveRightAxis => MoveAnalog ? HostMoveRight
            : Controls.MoveRight.IsDown ? 1 : Controls.MoveLeft.IsDown ? -1 : 0;

        // forward (+) / back (-), the same (up before down)
        private float MoveForwardAxis => MoveAnalog ? HostMoveForward
            : Controls.MoveUp.IsDown ? 1 : Controls.MoveDown.IsDown ? -1 : 0;
    }
}
