using System;
using MphRead.Hud;
using MphRead.Text;

namespace MphRead.Entities
{
    // recomp (P8, 8-player matches): the scoreboard (pause hold / GAME OVER) for a match of 5-8 seats. The game's rows
    // are 28 px each (portrait, name over the rank box), so 8 of them run off the 192 px screen. Compact instead (owner
    // pick 2026-10-10, over the game's rows 4 at a time): one 16 px line per player, name left of the rank box, the value
    // columns where the game has them, no portraits, centred on the screen.
    // A 1-4 player match never gets here (PlayerHud DrawScoreboard checks MaxPlayers first).
    public partial class PlayerEntity
    {
        private const float _score8RowSpace = 16;
        private const float _score8TeamLineSpace = 14;

        private float GetScoreboardHeight8()
        {
            float height = _scoreStartSpace;
            if (GameState.MatchState == MatchState.Ending)
            {
                height *= 2;
            }
            int curTeam = -1;
            for (int i = 0; i < GameState.ActivePlayers; i++)
            {
                PlayerEntity player = Players[GameState.ResultSlots[i]];
                if (!player.LoadFlags.TestFlag(LoadFlags.Active))
                {
                    continue;
                }
                // (no -_scoreTeamHeaderSpace before the 2nd team: the game's rows have room for it, these lines don't)
                if (GameState.Teams && player.TeamIndex != curTeam)
                {
                    height += _score8TeamLineSpace;
                    curTeam = player.TeamIndex;
                }
                height += _score8RowSpace;
            }
            return height;
        }

        private string Scoreboard8Value1(GameMode mode, float time, int points)
        {
            if (mode == GameMode.Survival || mode == GameMode.SurvivalTeams || mode == GameMode.Defender
                || mode == GameMode.DefenderTeams || mode == GameMode.PrimeHunter)
            {
                return time < 0 ? Strings.GetHudMessage(256) : FormatTime(TimeSpan.FromSeconds(time)); // MAX
            }
            return points.ToString();
        }

        private static string Scoreboard8Value2(GameMode mode, int deaths, int kills)
        {
            if (mode == GameMode.Survival || mode == GameMode.SurvivalTeams
                || mode == GameMode.Battle || mode == GameMode.BattleTeams)
            {
                return deaths.ToString();
            }
            return kills.ToString();
        }

        private void DrawScoreboard8()
        {
            GameMode mode = GameState.Mode;
            float posY = 96 - GetScoreboardHeight8() / 2; // centred on the screen
            if (GameState.MatchState == MatchState.Ending)
            {
                string text = Strings.GetHudMessage(219); // GAME OVER
                DrawText2D(128, posY, Align.Center, 0, text, new ColorRgba(0x53F4), fontSpacing: 8);
                posY += _scoreStartSpace;
            }
            string header1;
            if (mode == GameMode.Battle || mode == GameMode.BattleTeams || mode == GameMode.Nodes || mode == GameMode.NodesTeams)
            {
                header1 = Strings.GetHudMessage(225); // points
            }
            else if (mode == GameMode.Capture || mode == GameMode.Bounty || mode == GameMode.BountyTeams)
            {
                header1 = Strings.GetHudMessage(227); // octoliths
            }
            else
            {
                header1 = Strings.GetHudMessage(224); // time
            }
            string header2 = mode == GameMode.Battle || mode == GameMode.BattleTeams
                || mode == GameMode.Survival || mode == GameMode.SurvivalTeams
                ? Strings.GetHudMessage(223) // deaths
                : Strings.GetHudMessage(220); // kills
            // the kills/deaths column sits at 215 unless the ping column (right edge 252: bars, number, "ms" header) needs
            // the room: those are measured from the font, so no device's glyph widths can make them touch (recomp)
            (float value2X, float pingBarsX) = LayoutScoreboardPing(header2,
                s => Scoreboard8Value2(mode, GameState.Deaths[s], GameState.Kills[s]),
                t => Scoreboard8Value2(mode, GameState.TeamDeaths[t], GameState.TeamKills[t]));
            DrawText2D(160, posY, Align.Center, 0, header1, new ColorRgba(0x3FEF), fontSpacing: 8);
            DrawText2D(value2X, posY, Align.Center, 0, header2, new ColorRgba(0x3FEF), fontSpacing: 8);
            if (AnySeatPing())
            {
                DrawText2D(ScoreboardPingRight, posY, Align.Right, 0, "ms", new ColorRgba(0x3FEF), fontSpacing: 8); // recomp: online ping column
            }
            posY += _scoreStartSpace;
            string teamText = Strings.GetHudMessage(222); // team
            int curTeam = -1;
            for (int i = 0; i < GameState.ActivePlayers; i++)
            {
                int slot = GameState.ResultSlots[i];
                PlayerEntity player = Players[slot];
                if (!player.LoadFlags.TestFlag(LoadFlags.Active))
                {
                    continue;
                }
                if (GameState.Teams && player.TeamIndex != curTeam)
                {
                    curTeam = player.TeamIndex;
                    var teamColor = new ColorRgba(player.Team == Team.Orange ? 0x23Fu : 0x2BEAu);
                    float teamY = posY + 1;
                    DrawText2D(46, teamY, Align.Center, 0, $"{teamText} {curTeam + 1}", teamColor, fontSpacing: 8);
                    DrawText2D(160, teamY, Align.Center, 0, Scoreboard8Value1(mode, GameState.TeamTime[curTeam],
                        GameState.TeamPoints[curTeam]), teamColor, fontSpacing: 8);
                    DrawText2D(value2X, teamY, Align.Center, 0, Scoreboard8Value2(mode, GameState.TeamDeaths[curTeam],
                        GameState.TeamKills[curTeam]), teamColor, fontSpacing: 8);
                    posY += _score8TeamLineSpace;
                }
                string value1 = Scoreboard8Value1(mode, GameState.Time[slot], GameState.Points[slot]);
                string value2 = Scoreboard8Value2(mode, GameState.Deaths[slot], GameState.Kills[slot]);
                var color = new ColorRgba(0x7DEF);
                if (player.IsMainPlayer)
                {
                    float pct = _scene.ElapsedTime / (32 / 30f) % 1;
                    float rg = pct <= 0.5f ? Lerp(0, 1, pct * 2) : Lerp(1, 0, (pct - 0.5f) * 2);
                    color = new ColorRgba((byte)(rg * 255), (byte)(rg * 255), 255, 255);
                }
                // name | rank box (the game's two halves, 32 px each) | values, on one line
                DrawText2D(46, posY + 4, Align.Center, 0, GameState.ShownNickname(slot), color, fontSpacing: 8);
                int stars = GameState.Stars[slot];
                _starsInst.PositionX = 80 / 256f;
                _starsInst.PositionY = posY / 192f;
                _starsInst.SetIndex(stars * 2, _scene);
                _scene.DrawHudObject(_starsInst, mode: 2);
                _starsInst.PositionX = 112 / 256f;
                _starsInst.SetIndex(stars * 2 + 1, _scene);
                _scene.DrawHudObject(_starsInst, mode: 2);
                DrawText2D(160, posY + 4, Align.Center, 0, value1, color, fontSpacing: 8);
                DrawText2D(value2X, posY + 4, Align.Center, 0, value2, color, fontSpacing: 8);
                DrawScoreboardPing(slot, ScoreboardPingRight, posY + 4, pingBarsX); // recomp: online (PlayerScoreboardPing.cs)
                posY += _score8RowSpace;
            }
        }
    }
}
