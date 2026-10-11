using System;
using System.Collections.Generic;
using System.Linq;

namespace MphRecomp.Online
{
    // The chat this device has received in the game it is in (docs/ONLINE_PLAY.md section 8, Social O7): the last lines the
    // server pushed (ChatEvent), kept in memory only, shown by the lobby page and the match overlay. A line from a player
    // muted here (PlayerMutes) is kept but not listed. One log per OnlineClient (OnlineClient.Chat); Add runs on the network
    // thread, the readers on the game thread.
    public sealed class ChatLog
    {
        public sealed record Line(long Seq, string GameId, string Code, string Name, int Preset, string Text, DateTime At)
        {
            public bool IsPreset => Preset >= 0;
            // what to show: the quick message's words, or the (masked) typed text
            public string Shown => IsPreset ? ChatPresets.Text(Preset) : Text;
        }

        public const int Keep = 100;
        private readonly object _lock = new();
        private readonly List<Line> _lines = new();
        private long _seq;

        // fires on the network thread for each new line, muted or not
        public event Action<Line>? Added;

        // the number of the newest line (0 = none yet): a reader remembers it to see what is new
        public long Latest
        {
            get
            {
                lock (_lock)
                {
                    return _seq;
                }
            }
        }

        public Line Add(ChatEvent e)
        {
            Line line;
            lock (_lock)
            {
                line = new Line(++_seq, e.GameId, e.Code, e.Name, e.Preset, e.Text ?? "", DateTime.UtcNow);
                _lines.Add(line);
                if (_lines.Count > Keep)
                {
                    _lines.RemoveAt(0);
                }
            }
            Added?.Invoke(line);
            return line;
        }

        // the newest lines that are shown (not from a muted player), oldest first; `me` = this player's own code (never muted).
        // `others` false (chat turned off: VoiceSettings.Chat, the menus' and the match's reads) shows only this player's lines.
        public List<Line> Recent(int max, PlayerMutes? mutes = null, string? me = null, long after = 0, bool others = true)
        {
            bool chatOn = mutes == null || others;
            lock (_lock)
            {
                return _lines.Where(l => l.Seq > after && (mutes == null || l.Code == me || chatOn && !mutes.Chat(l.Code))).TakeLast(max).ToList();
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _lines.Clear();
            }
        }
    }
}
