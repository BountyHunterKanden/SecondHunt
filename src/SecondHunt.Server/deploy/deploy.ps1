# Puts SecondHuntServer on the rented server (DEPLOY.md). Windows PowerShell on the PC; uses the built-in ssh/scp.
#   .\deploy.ps1 -Server root@203.0.113.5 -Setup [-Domain play.example.com]   first time: setup.sh, then the server
#   .\deploy.ps1 -Server root@203.0.113.5                                    later: a new build, restarted
#   .\deploy.ps1 -Server root@203.0.113.5 -GameData <folder> [-GameKey AMHE1]  also the game files served matches read
#       (docs/DEDICATED_SERVERS.md section 3: the owner's own extracted files, gamedata.ps1 picks them; they go to
#       /var/lib/secondhunt/gamedata, owned by the service user, mode 0700, never served)
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [switch]$Setup,
    [string]$Domain = "",
    [string]$GameData = "",
    [string]$GameKey = "AMHE1",
    [string]$Key = "$HOME\.ssh\secondhunt_deploy"
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $here '..\SecondHunt.Server.csproj'
$out = Join-Path $env:TEMP 'secondhunt-publish'
$dotnet = "$HOME\.dotnet\dotnet.exe"
$ssh = @('-i', $Key, '-o', 'StrictHostKeyChecking=accept-new')

if ($Setup) {
    ssh @ssh $Server 'mkdir -p /root/secondhunt-setup'
    if ($LASTEXITCODE -ne 0) { throw 'ssh failed' }
    scp @ssh "$here\setup.sh" "$here\secondhunt.service" "$here\Caddyfile" "$here\backup.sh" "${Server}:/root/secondhunt-setup/"
    if ($LASTEXITCODE -ne 0) { throw 'copying the setup files failed' }
    ssh @ssh $Server "DOMAIN='$Domain' bash /root/secondhunt-setup/setup.sh"
    if ($LASTEXITCODE -ne 0) { throw 'setup.sh failed' }
}

& $dotnet publish $project -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:IntermediateOutputPath=obj/linux/ -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
scp @ssh "$out\SecondHuntServer" "${Server}:/opt/secondhunt/SecondHuntServer.new"
if ($LASTEXITCODE -ne 0) { throw 'copying the server failed' }
# served games (server 0.8.0): the match host beside the server (one process per match) and the unit that starts it with it
$mhProject = Join-Path $here '..\..\SecondHunt.MatchHost\SecondHunt.MatchHost.csproj'
$mhOut = Join-Path $env:TEMP 'secondhunt-matchhost-publish'
& $dotnet publish $mhProject -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:IntermediateOutputPath=obj/linux/ -o $mhOut
if ($LASTEXITCODE -ne 0) { throw 'publishing the match host failed' }
scp @ssh "$mhOut\SecondHuntMatchHost" "${Server}:/opt/secondhunt/SecondHuntMatchHost.new"
if ($LASTEXITCODE -ne 0) { throw 'copying the match host failed' }
scp @ssh "$here\secondhunt.service" "${Server}:/etc/systemd/system/secondhunt.service"
if ($LASTEXITCODE -ne 0) { throw 'copying the service unit failed' }
if ($GameData) {
    # the game files as one archive (tar.exe is in Windows 10+), unpacked into a fresh folder that then replaces the old one
    $tar = Join-Path $env:TEMP 'secondhunt-gamedata.tar.gz'
    tar -czf $tar -C $GameData .
    if ($LASTEXITCODE -ne 0) { throw 'packing the game files failed' }
    scp @ssh $tar "${Server}:/root/secondhunt-gamedata.tar.gz"
    if ($LASTEXITCODE -ne 0) { throw 'copying the game files failed' }
    Remove-Item $tar
    $g = '/var/lib/secondhunt/gamedata'
    ssh @ssh $Server "set -e; rm -rf $g.new; install -d -o secondhunt -g secondhunt -m 700 $g.new $g.new/$GameKey; tar -xzf /root/secondhunt-gamedata.tar.gz -C $g.new/$GameKey; rm -f /root/secondhunt-gamedata.tar.gz; printf '0.35.0.0\n${GameKey}=$g/$GameKey\nExport=$g/export\n' > $g.new/paths.txt; chown -R secondhunt:secondhunt $g.new; chmod -R go-rwx $g.new; rm -rf $g; mv $g.new $g; du -sh $g"
    if ($LASTEXITCODE -ne 0) { throw 'unpacking the game files failed' }
}
# the word filter (0.5.0): the LDNOOBW list as words.txt every deploy (the owner's own words live in words_owner.txt, which stays), and the admin tool
scp @ssh "$here\..\wordlists\ldnoobw_en.txt" "${Server}:/opt/secondhunt/words.txt"
if ($LASTEXITCODE -ne 0) { throw 'copying the word list failed' }
scp @ssh "$here\admin.sh" "${Server}:/usr/local/bin/secondhunt-admin"
if ($LASTEXITCODE -ne 0) { throw 'copying the admin tool failed' }
ssh @ssh $Server 'chmod 755 /usr/local/bin/secondhunt-admin; chmod 644 /opt/secondhunt/words.txt; test -f /opt/secondhunt/words_owner.txt || echo "# Second Hunt: words to block on top of the LDNOOBW list (one per line; *word = also inside longer words; -word = allow a list entry)" > /opt/secondhunt/words_owner.txt; chmod 644 /opt/secondhunt/words_owner.txt'
if ($LASTEXITCODE -ne 0) { throw 'setting up the word files failed' }
ssh @ssh $Server 'chmod 755 /opt/secondhunt/SecondHuntServer.new /opt/secondhunt/SecondHuntMatchHost.new && mv -f /opt/secondhunt/SecondHuntServer.new /opt/secondhunt/SecondHuntServer && mv -f /opt/secondhunt/SecondHuntMatchHost.new /opt/secondhunt/SecondHuntMatchHost && systemctl daemon-reload && systemctl restart secondhunt && sleep 3 && systemctl --no-pager --lines=5 status secondhunt && curl -fsS http://127.0.0.1:8080/v1/health'
if ($LASTEXITCODE -ne 0) { throw 'the server did not come up: ssh in and run journalctl -u secondhunt' }
Write-Host "deployed; from outside: $(if ($Domain) { "https://$Domain/v1/health" } else { 'http://<server ip>/v1/health' })"
