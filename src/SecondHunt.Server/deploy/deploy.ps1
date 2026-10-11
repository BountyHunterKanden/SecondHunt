# Puts SecondHuntServer on the rented server (DEPLOY.md). Windows PowerShell on the PC; uses the built-in ssh/scp.
#   .\deploy.ps1 -Server root@203.0.113.5 -Setup [-Domain play.example.com]   first time: setup.sh, then the server
#   .\deploy.ps1 -Server root@203.0.113.5                                    later: a new build, restarted
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [switch]$Setup,
    [string]$Domain = "",
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
# the word filter (0.5.0): the LDNOOBW list as words.txt every deploy (the owner's own words live in words_owner.txt, which stays), and the admin tool
scp @ssh "$here\..\wordlists\ldnoobw_en.txt" "${Server}:/opt/secondhunt/words.txt"
if ($LASTEXITCODE -ne 0) { throw 'copying the word list failed' }
scp @ssh "$here\admin.sh" "${Server}:/usr/local/bin/secondhunt-admin"
if ($LASTEXITCODE -ne 0) { throw 'copying the admin tool failed' }
ssh @ssh $Server 'chmod 755 /usr/local/bin/secondhunt-admin; chmod 644 /opt/secondhunt/words.txt; test -f /opt/secondhunt/words_owner.txt || echo "# Second Hunt: words to block on top of the LDNOOBW list (one per line; *word = also inside longer words; -word = allow a list entry)" > /opt/secondhunt/words_owner.txt; chmod 644 /opt/secondhunt/words_owner.txt'
if ($LASTEXITCODE -ne 0) { throw 'setting up the word files failed' }
ssh @ssh $Server 'chmod 755 /opt/secondhunt/SecondHuntServer.new && mv -f /opt/secondhunt/SecondHuntServer.new /opt/secondhunt/SecondHuntServer && systemctl restart secondhunt && sleep 3 && systemctl --no-pager --lines=5 status secondhunt && curl -fsS http://127.0.0.1:8080/v1/health'
if ($LASTEXITCODE -ne 0) { throw 'the server did not come up: ssh in and run journalctl -u secondhunt' }
Write-Host "deployed; from outside: $(if ($Domain) { "https://$Domain/v1/health" } else { 'http://<server ip>/v1/health' })"
