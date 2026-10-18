using System.Collections.Generic;
using MphRead.Entities;

namespace MphRead
{
    // recomp: First Hunt's messages (docs/FIRST_HUNT_PORT.md P2; cart 0x2031198-0x2031450). A message to an entity is
    // delivered like MphRead's (the FhMessage id carried in Message); a message with no target is a global event that
    // stays in the queue one frame, where systems poll it by id (0x2031204): platforms poll Activate with their group,
    // the mission polls Complete.
    public static class FirstHuntMessages
    {
        private readonly struct Event
        {
            public readonly FhMessage Message;
            public readonly EntityBase Sender;
            public readonly int Param1;
            public readonly ulong Frame;

            public Event(FhMessage message, EntityBase sender, int param1, ulong frame)
            {
                Message = message;
                Sender = sender;
                Param1 = param1;
                Frame = frame;
            }
        }

        private static readonly List<Event> _events = new List<Event>();

        public static void Clear()
        {
            _events.Clear();
        }

        // delay in DS frames
        public static void Send(Scene scene, FhMessage message, EntityBase sender, EntityBase? target, int param1, int delay = 0)
        {
            if (message == FhMessage.None)
            {
                return;
            }
            if (target == null)
            {
                Prune(scene);
                _events.Add(new Event(message, sender, param1, scene.FrameCount + (ulong)(delay < 1 ? 1 : delay) * 2)); // todo: FPS stuff
                return;
            }
            if (target.Type == EntityType.PlayerSpawn && message == FhMessage.SetActive)
            {
                // First Hunt's checkpoints: 17 with param 1 = this spawn point on, 0 = off (0x2020ea8)
                scene.SendMessage(Message.SetActive, sender, target, param1, 0, delay * 2);
                return;
            }
            scene.SendMessage((Message)(uint)message, sender, target, param1, 0, delay * 2); // todo: FPS stuff
        }

        // the global events of this id delivered this frame
        public static IEnumerable<(EntityBase Sender, int Param1)> Poll(Scene scene, FhMessage message)
        {
            Prune(scene);
            foreach (Event e in _events)
            {
                if (e.Message == message && e.Frame == scene.FrameCount)
                {
                    yield return (e.Sender, e.Param1);
                }
            }
        }

        private static void Prune(Scene scene)
        {
            _events.RemoveAll(e => e.Frame < scene.FrameCount);
        }

        public static FhMessage Of(MessageInfo info)
        {
            return (FhMessage)(uint)info.Message;
        }
    }
}
