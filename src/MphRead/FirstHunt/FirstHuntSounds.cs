using System.Collections.Generic;
using MphRead.Entities;

namespace MphRead
{
    // recomp: First Hunt's own sounds (docs/FIRST_HUNT_PORT.md P4; docs/first_hunt/content.md). The cart's banks
    // (sound/SFXDATA.BIN, MENUSFXDATA.BIN, GLOBALSFXDATA.BIN, BGMDATA.BIN) are plain samples, no scripts or DGN; a host
    // that plays sounds (the app's GameSfxPlayer) appends them after MPH's samples and sets the bases here. While a
    // First Hunt level runs, SoundSource turns the MPH ids the shared code plays into the cart's (Remap); a sound with
    // no First Hunt counterpart keeps MPH's. Without a host (the PC tools), the bases stay -1 and nothing changes.
    public static class FirstHuntSounds
    {
        public static int SfxBase { get; set; } = -1;
        public static int MenuBase { get; set; } = -1;
        public static int GlobalBase { get; set; } = -1;
        public static int BgmBase { get; set; } = -1;

        // MPH sample/script id -> SFXDATA index
        private static readonly Dictionary<int, int> _remap = new Dictionary<int, int>();

        // an SFXDATA entry with no sound (the cart calls some of them: the Power Beam's charge, door unlock's first half)
        public const int Silent = 27;

        // the sounds the shared player, weapon and world code plays, as the cart's (fh_research p4_sound.md; the ids
        // measured with FH_SOUNDS=1 -fhgame p1/p2). The First Hunt files pick theirs directly (Sfx(index, standIn)).
        static FirstHuntSounds()
        {
            // weapons (table 0x2079ac4: shot, charged shot, charge loop, impact); no Missile switch sound (0x2006a40)
            Map(SfxId.BEAM, 0);
            Map(SfxId.BEAM_HIT, 1);
            Map(SfxId.BEAM_CHARGE1, Silent); // (77, empty)
            Map(SfxId.MISSILE, 8);
            Map(SfxId.MISSILE_HIT, 9);
            Map(SfxId.LOB_GUN, 31);
            Map(SfxId.LOB_GUN_HIT, 32);
            Map(SfxId.BEAM_SWITCH, 7);
            Map(SfxId.MISSILE_SWITCH, Silent);
            Map(SfxId.BEAM_SWITCH_FAIL, 22);
            Map(SfxId.MISSILE_OPEN, 66);
            // the player
            Map(SfxId.JUMP, 6);
            Map(SfxId.MORPH_BALL, 2);
            Map(SfxId.MORPH_BALL_BOMB_PLACE, 3);
            Map(SfxId.MORPH_BALL_BOMB, 4);
            Map(SfxId.BOOST_BALL2, 19);
            foreach (SfxId step in new[] { SfxId.WALK_METAL1, SfxId.WALK_WET1, SfxId.WALK_ROCK1, SfxId.WALK_SNOW1,
                SfxId.DGN_WALK_METAL1, SfxId.DGN_WALK_WET1, SfxId.DGN_WALK_ROCK1, SfxId.DGN_WALK_SNOW1, SfxId.DGN_WALK_ICE1 })
            {
                Map(step, 12);
            }
            foreach (SfxId step in new[] { SfxId.WALK_METAL2, SfxId.WALK_WET2, SfxId.WALK_ROCK2, SfxId.WALK_SNOW2,
                SfxId.DGN_WALK_METAL2, SfxId.DGN_WALK_WET2, SfxId.DGN_WALK_ROCK2, SfxId.DGN_WALK_SNOW2, SfxId.DGN_WALK_ICE2 })
            {
                Map(step, 13);
            }
            foreach (SfxId land in new[] { SfxId.LAND_METAL, SfxId.LAND_WET, SfxId.LAND_ROCK, SfxId.LAND_SNOW,
                SfxId.DGN_LAND_METAL, SfxId.DGN_LAND_WET, SfxId.DGN_LAND_ROCK, SfxId.DGN_LAND_SNOW, SfxId.DGN_LAND_ICE })
            {
                Map(land, Silent); // (the cart has no landing sound)
            }
            foreach (SfxId hurt in new[] { SfxId.DAMAGE1, SfxId.DAMAGE2, SfxId.DAMAGE3, SfxId.DAMAGE4, SfxId.LAVA_DAMAGE })
            {
                Map(hurt, 25);
            }
            Map(SfxId.DIE, 26);
            Map(SfxId.DIE_MULTI1, 26);
            Map(SfxId.PLAYER_SPAWN, 29);
            // (the ball's roll stays MPH's: the cart's is a layered sound, speed-driven volume and pitch)
            // the world
            Map(SfxId.DOOR_OPEN, 53);
            Map(SfxId.DOOR_CLOSE, 52);
            Map(SfxId.DOOR_UNLOCK, 56);
            Map(SfxId.ITEM_SPAWN1, 24);
            Map(SfxId.JUMP_PAD, 54);
            // stand-ins in the First Hunt enemies
            Map(SfxId.ZOOMER_IDLE_LOOP, 42);
            Map(SfxId.MOCHTROID_FLY, 45);
            Map(SfxId.MOCHTROID_TELEPORT_IN, 46);
            Map(SfxId.MOCHTROID_TELEPORT_OUT, 47);
            Map(SfxId.METROID_FLY, 65);
            Map(SfxId.METROID_CHARGE, 62);
        }

        // a cart SFXDATA index as an id SoundSource plays (-1 = no host)
        public static int Sfx(int index) => SfxBase < 0 || index < 0 ? -1 : SfxBase + index;
        public static int Menu(int index) => MenuBase < 0 || index < 0 ? -1 : MenuBase + index;
        public static int Bgm(int index) => BgmBase < 0 || index < 0 ? -1 : BgmBase + index;

        // a First Hunt sound where there is one, else the MPH stand-in
        public static int Sfx(int index, SfxId standIn)
        {
            int id = Sfx(index);
            return id >= 0 ? id : (int)standIn;
        }

        // a positionless cart sound (the HUD's, the clock's); nothing without a host
        public static void PlayFree(int index)
        {
            int id = Sfx(index);
            if (id >= 0)
            {
                Sound.Sfx.Instance.PlaySample(id, source: null, loop: null, noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
            }
        }

        // the music (BGMDATA: the cart streams it; here it plays as a looping sample through the same mixer)
        private static int _musicHandle = -1;
        public static int MusicTrack { get; private set; } = -1;
        private static bool _musicLoop = false;

        public static void PlayMusic(int track, bool loop)
        {
            StopMusic();
            int id = Bgm(track);
            MusicTrack = track;
            _musicLoop = loop;
            if (id >= 0)
            {
                _musicHandle = Sound.Sfx.Instance.PlaySample(id, source: null, loop: loop, noUpdate: false, recency: -1, sourceOnly: false, cancellable: false);
            }
        }

        public static void StopMusic()
        {
            if (_musicHandle >= 0)
            {
                Sound.Sfx.Instance.StopSoundByHandle(_musicHandle);
            }
            _musicHandle = -1;
            MusicTrack = -1;
        }

        // the cart's music has its own mixer slot, so nothing else stops it; here a looping track stopped by anything but
        // StopMusic (a stop-all, a stolen mixer slot, a play that got no handle) starts again
        public static void KeepMusic()
        {
            if (_musicLoop && MusicTrack >= 0 && (_musicHandle < 0 || !Sound.Sfx.Instance.IsHandlePlaying(_musicHandle)))
            {
                PlayMusic(MusicTrack, loop: true);
            }
        }

        // a test's record of the MPH ids played in a First Hunt level (id -> times)
        public static Dictionary<int, int>? Heard { get; set; }

        public static int Remap(int id)
        {
            if (Heard != null && PlayerEntity.HostFirstHunt)
            {
                Heard[id] = Heard.GetValueOrDefault(id) + 1;
            }
            if (SfxBase >= 0 && PlayerEntity.HostFirstHunt && _remap.TryGetValue(id, out int index))
            {
                return SfxBase + index;
            }
            return id;
        }

        public static void Map(SfxId mph, int index)
        {
            _remap[(int)mph] = index;
        }
    }
}
