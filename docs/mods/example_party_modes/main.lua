-- Example: Party Modes
-- Adds three match modes (mod.json "Modes"). They show up at the end of the GAME MODE list while this mod is
-- turned on in MODS. Each plays as Battle; the filters below change it. A mod that declares modes only runs in
-- matches playing one of them, so these rules never touch a normal game.

local function playing(id)
  return mode.current() == id
end

mode.setup(function()
  mod.log(mode.current() .. ': first to ' .. match.point_goal())
end)

-- INSTAGIB: any hit from another player kills, and the dead come back quickly
mod.filter('damage', function(t)
  if playing('instagib') and t.attacker >= 0 and t.attacker ~= t.target then
    t.damage = 999
  end
end)

mod.filter('respawn_time', function(t)
  if playing('instagib') then
    t.frames = 60 * mod.setting('respawn') -- 60 frames a second (vanilla waits 3 seconds)
  end
end)

-- HEADHUNTER: a headshot kill scores the setting's points instead of 1 ("points" are added on top of vanilla's)
mod.filter('kill', function(t)
  if playing('headhunter') and t.headshot and not t.suicide then
    t.points = mod.setting('headshot_points') - t.vanilla_points
  end
end)

-- MOON BOUNCE: 40% gravity and 40% higher jumps for every hunter (values are fixed point, 4096 = 1.0)
mod.filter('player_values', function(t)
  if playing('moon') then
    t.BipedGravity = math.floor(t.BipedGravity * 0.4)
    t.AltAirGravity = math.floor(t.AltAirGravity * 0.4)
    t.JumpSpeed = math.floor(t.JumpSpeed * 1.4)
  end
end)
