using System;
using MphRead.Entities;
using MphRead.Entities.FirstHunt;

namespace MphRead
{
    public enum FhMode : byte
    {
        Morphball = 0,
        Regulator = 1,
        Survivor = 2,
        Multiplayer = 3,
        None = 255 // a level outside the menus' (TestLevel, e3)
    }

    public enum FhResult : byte
    {
        Complete = 0,
        Failed = 1,
        Disconnected = 2
    }

    // recomp: First Hunt's match rules (docs/FIRST_HUNT_PORT.md P2; fh_research p2_missions.md; cart game state
    // 0x202e460, match block 0x209b92c). The three training games:
    //   - Morphball (Game C): 55 s; reach the last point module = COMPLETE, time out = FAILED; deaths respawn
    //   - Regulator (Game A): 10:00; dying ends it as COMPLETE, time out = FAILED (no level entity sends Complete)
    //   - Survivor (Game B): no clock; the first death ends it as COMPLETE, straight to the results (no banner)
    // Score: a combo (count, value) per player, banked as count * value when it breaks and at the end; a Regulator
    // kill adds 1 and 10, a Survivor kill 1 and 1, a point module 1 and 10. The end banner shows 150 frames; the
    // results and hi-score screens come with the front end (P4), the HUD with P3.
    public static class FirstHuntMission
    {
        private const int LevelTableAddress = 0x207292c;
        private const int LevelSize = 0x30;

        public static FhMode Mode { get; private set; } = FhMode.None;
        public static FhResult Result { get; private set; } = FhResult.Failed;
        public static bool Ending { get; private set; }
        public static bool Over { get; private set; }
        // frames left (60 Hz), -1 = no clock
        public static int Clock { get; private set; } = -1;
        public static int Elapsed { get; private set; }
        public static bool IsTraining => Mode <= FhMode.Survivor;
        // the main player has spawned in this level (the clock runs from then)
        public static bool Begun => _begun;
        // a deathmatch level's time limit (level table word 6: MP1 5:00, MP2 8:00, MP3 5:00), -1 = none; the match
        // start (Core FirstHuntStart.cs StartFirstHuntMatch) gives it to GameState, which runs the clock
        public static float TimeLimitSeconds { get; private set; } = -1;

        public static readonly int[] Score = new int[PlayerEntity.SlotCapacity];
        public static readonly int[] ComboCount = new int[PlayerEntity.SlotCapacity];
        public static readonly int[] ComboValue = new int[PlayerEntity.SlotCapacity];
        public static readonly int[] Kills = new int[PlayerEntity.SlotCapacity];
        public static readonly int[] Deaths = new int[PlayerEntity.SlotCapacity];
        // a deathmatch's kills [killer, victim] (cart 0x209b938, self-kills too): the results page's table
        public static readonly int[,] KillTable = new int[4, 4];

        // the host: the match is over (after the banner); and the banner lines, for a HUD
        public static Action? HostOver { get; set; }

        private static bool _completePosted = false;
        private static int _completeDelay = 0;
        private static bool _mainAlive = false;
        private static bool _begun = false;
        private static int _level = -1;
        private static int _musicCues = 0;

        // FirstHuntRoom.Loading, before the room's entities: this level's mode and clock
        public static void Start(string roomName)
        {
            (FhMode mode, int level) = roomName switch
            {
                "Level SP Morphball" => (FhMode.Morphball, 3),
                "Level SP Regulator" => (FhMode.Regulator, 4),
                "Level SP Survivor" => (FhMode.Survivor, 5),
                "Level MP1" => (FhMode.Multiplayer, 0),
                "Level MP2" => (FhMode.Multiplayer, 1),
                "Level MP3" => (FhMode.Multiplayer, 2),
                "Level MP5" => (FhMode.Multiplayer, 7),
                "Level FhTestLevel" => (FhMode.None, 6),
                "Level MP1b" => (FhMode.None, 8),
                _ => (FhMode.None, 9) // E3 level
            };
            Mode = mode;
            _level = level;
            _musicCues = 0;
            FirstHuntSounds.StopMusic();
            Result = FhResult.Failed;
            Ending = false;
            Over = false;
            Elapsed = 0;
            Array.Clear(Score);
            Array.Clear(ComboCount);
            Array.Clear(ComboValue);
            Array.Clear(Kills);
            Array.Clear(Deaths);
            Array.Clear(KillTable);
            _completePosted = false;
            _completeDelay = 0;
            _mainAlive = false;
            _begun = false;
            int time = FirstHuntRules.ReadArm9Int(LevelTableAddress + level * LevelSize + 0x18) ?? -1;
            // a deathmatch's clock is GameState's (StepMatch; the lobby has no time or kill options, P5)
            Clock = mode == FhMode.Multiplayer || mode == FhMode.None || time < 0 ? -1 : time * 2; // todo: FPS stuff
            TimeLimitSeconds = mode == FhMode.Multiplayer && time > 0 ? time / 30f : -1;
            FirstHuntMessages.Clear();
            PointModuleEntity.ResetCurrent();
            FhEnemyEntity.Killed = OnKilled;
        }

        public static void Stop()
        {
            Mode = FhMode.None;
            Clock = -1;
            FhEnemyEntity.Killed = null;
            FirstHuntSounds.StopMusic();
        }

        // the Complete message (0x12, one frame late): the result first, then the clock goes to 0
        public static void PostComplete(FhResult result)
        {
            if (Ending || _completePosted)
            {
                return;
            }
            Result = result;
            _completePosted = true;
            _completeDelay = 2; // todo: FPS stuff
        }

        public static void BankCombo(int slot)
        {
            Score[slot] += ComboCount[slot] * ComboValue[slot];
            ComboCount[slot] = 0;
            ComboValue[slot] = 0;
        }

        // the main player's Process, every frame it runs (paused frames don't run it, like the cart's clock)
        public static void Step(PlayerEntity main)
        {
            if (Mode == FhMode.None && Clock < 0 || Over)
            {
                return;
            }
            // the clock starts with the first spawn (MphRead holds it for "press FIRE to begin"; todo: the cart's
            // spawn movie and start, P4)
            if (!_begun)
            {
                if (main.Health == 0)
                {
                    return;
                }
                _begun = true;
                StartMusic();
            }
            if (Mode == FhMode.Multiplayer)
            {
                StepMatch(main);
                return;
            }
            int slot = main.SlotIndex;
            // deaths (0x200a650 banks the combo; Regulator 0x200bc74, Survivor rule 2 0x2031454 end the run)
            if (main.Health > 0)
            {
                _mainAlive = true;
            }
            else if (_mainAlive)
            {
                _mainAlive = false;
                Deaths[slot]++;
                BankCombo(slot);
                if (Mode == FhMode.Regulator || Mode == FhMode.Survivor && Deaths[slot] >= 1)
                {
                    PostComplete(FhResult.Complete);
                }
            }
            if (_completePosted && --_completeDelay <= 0)
            {
                _completePosted = false;
                if (!Ending)
                {
                    Clock = 0;
                }
            }
            if (!Ending)
            {
                if (Clock == 0)
                {
                    if (IsTraining)
                    {
                        for (int i = 0; i < Score.Length; i++)
                        {
                            BankCombo(i);
                        }
                    }
                    Ending = true;
                    Clock = Mode == FhMode.Survivor ? 0 : 150 * 2; // the banner, 5 s; todo: FPS stuff
                    // the end (0x202e460): Survivor fades its music out, the rest play COMPLETE (11) or FAILED (5) once
                    if (Mode == FhMode.Survivor)
                    {
                        FirstHuntSounds.StopMusic(); // todo: the 15-frame fade
                    }
                    else
                    {
                        FirstHuntSounds.PlayMusic(Result == FhResult.Complete ? 11 : 5, loop: false);
                    }
                    Console.WriteLine($"First Hunt {Mode}: {(Result == FhResult.Complete ? "COMPLETE" : "FAILED")}, score {Score[slot]}, kills {Kills[slot]}, {Elapsed / 60f:0.0} s");
                }
                else
                {
                    FirstHuntSounds.KeepMusic();
                    Elapsed++;
                    if (Clock > 0)
                    {
                        Clock--;
                        // the last 10 s beep each second, each half second in the last 5 (SFX 0x37)
                        if (Clock % 2 == 0) // todo: FPS stuff
                        {
                            int ds = Clock / 2;
                            if (ds > 0 && (ds <= 10 * 30 && ds % 30 == 0 || ds <= 5 * 30 && ds % 15 == 0))
                            {
                                FirstHuntSounds.PlayFree(0x37);
                            }
                        }
                    }
                }
            }
            if (Ending)
            {
                if (Clock > 0)
                {
                    Clock--;
                }
                if (Clock == 0)
                {
                    Over = true;
                    // todo: results / hi-score (training_hi_score, P4)
                    HostOver?.Invoke();
                }
            }
        }

        // a deathmatch (P5; fh_research p5_multiplayer.md): GameState keeps the clock, the kills and the deaths (a LAN
        // client's come in the host's snapshots) and ends the match; here the HUD's clock and the countdown beeps, and at
        // the end (MatchOver) the jingle: 11 for the winner, 5 for the rest (the 5 s banner is GameState's Ending)
        private static void StepMatch(PlayerEntity main)
        {
            if (Ending)
            {
                return;
            }
            if (GameState.MatchState == MatchState.InProgress && GameState.MatchTime != 0)
            {
                FirstHuntSounds.KeepMusic();
                Elapsed++;
                int clock = GameState.MatchTime < 0 ? -1 : (int)MathF.Ceiling(GameState.MatchTime * 60); // todo: FPS stuff
                if (clock >= 0 && Clock > clock && Clock - clock < 60)
                {
                    // the last 10 s beep each second, each half second in the last 5 (SFX 0x37), as the training clock;
                    // every DS frame passed since the last step (a client's clock moves in snapshot steps)
                    for (int ds = (Clock - 1) / 2; ds >= Math.Max(clock / 2, 1); ds--)
                    {
                        if (ds <= 10 * 30 && ds % 30 == 0 || ds <= 5 * 30 && ds % 15 == 0)
                        {
                            FirstHuntSounds.PlayFree(0x37);
                            break;
                        }
                    }
                }
                Clock = clock;
            }
        }

        // GameState's time out in a deathmatch (the players stop processing then, so not from StepMatch)
        public static void MatchOver(PlayerEntity main)
        {
            if (Mode != FhMode.Multiplayer || Ending)
            {
                return;
            }
            Ending = true;
            Clock = 0;
            Result = FhResult.Complete;
            int slot = main.SlotIndex;
            int rank = Rank(slot);
            FirstHuntSounds.PlayMusic(rank == 0 ? 11 : 5, loop: false);
            Console.WriteLine($"First Hunt deathmatch over: player {slot + 1} rank {rank + 1}, kills {GameState.Kills[slot]}, "
                + $"deaths {GameState.Deaths[slot]}, {Elapsed / 60f:0.0} s");
        }

        // a player killed by a player in a deathmatch (PlayerEntity's death, on the host and on each match client from the
        // host's attacker): the results' table (0x200ba24)
        public static void PlayerKilled(int attacker, int victim)
        {
            if (Mode == FhMode.Multiplayer && !Ending && attacker >= 0 && attacker < 4 && victim >= 0 && victim < 4)
            {
                KillTable[attacker, victim]++;
            }
        }

        // a deathmatch rank (0x2030b6c): how many players score more (ties share a rank)
        public static int Rank(int slot)
        {
            int rank = 0;
            for (int i = 0; i < PlayerEntity.MaxPlayers; i++)
            {
                if (i != slot && PlayerEntity.Players[i].LoadFlags.TestFlag(LoadFlags.Active) && GameState.Points[i] > GameState.Points[slot])
                {
                    rank++;
                }
            }
            return rank;
        }

        // each level's music (chosen in code, 0x202ec4c, by level index): MP1 7, MP2 8, MP3 6, Morph Ball 8, Regulator 10
        // (and its cues), Survivor 9, anything else 1
        private static void StartMusic()
        {
            int track = _level switch
            {
                0 => 7,
                1 => 8,
                2 => 6,
                3 => 8,
                4 => 10,
                5 => 9,
                _ => 1
            };
            FirstHuntSounds.PlayMusic(track, loop: true);
        }

        // Regulator's music cues (0x2039828, each once a level): 4 the spawn of a player with three tanks -> 9 (0x200a830,
        // inside the +0x42c branch: never the main player, so the cart's Regulator stays on 10 from the start: a BizHawk
        // capture loops its 9.09 s BGM 10 for the first 25 s), 1 the first door opened -> SFX 71 and 1, 2 a door
        // unlocked (or 8) -> 10
        public static void MusicCue(int mask)
        {
            if (_level != 4 || (_musicCues & mask) != 0 || Ending)
            {
                return;
            }
            _musicCues |= mask;
            if (mask == 4)
            {
                FirstHuntSounds.PlayMusic(9, loop: true);
            }
            else if (mask == 1)
            {
                FirstHuntSounds.PlayFree(71);
                FirstHuntSounds.PlayMusic(1, loop: true);
            }
            else
            {
                FirstHuntSounds.PlayMusic(10, loop: true);
            }
        }

        // a kill (cart 0x201297c, modes 1-2): the combo grows
        private static void OnKilled(FhEnemyEntity enemy, PlayerEntity? killer)
        {
            if (killer == null || Ending)
            {
                return;
            }
            int slot = killer.SlotIndex;
            if (Mode == FhMode.Regulator || Mode == FhMode.Survivor)
            {
                ComboCount[slot]++;
                ComboValue[slot] += Mode == FhMode.Regulator ? 10 : 1;
                Kills[slot]++;
            }
            // todo: a shot that hits nothing breaks the combo (probable, Impact 0x201297c)
        }

        // a point module reached (player 0x200c15c): skipped ones break the combo
        public static void PointModule(PlayerEntity player, bool skipped, bool last)
        {
            int slot = player.SlotIndex;
            if (skipped)
            {
                BankCombo(slot);
            }
            ComboCount[slot]++;
            ComboValue[slot] += 10;
            if (last)
            {
                PostComplete(FhResult.Complete);
            }
        }
    }
}
