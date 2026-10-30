# The owner's moderation tool, run from the PC over ssh (Social O7). Examples:
#   .\admin.ps1 reports                       the open reports
#   .\admin.ps1 show 12                       one report, with what the player said
#   .\admin.ps1 ban KANDEN#0421 7 harassing   ban a key for 7 days ("perm" = for good); the player is kicked within 15 s
#   .\admin.ps1 help                          every command
# Edit the owner's own filter words on the server: ssh root@<server> nano /opt/secondhunt/words_owner.txt (syntax in WordFilter.cs).
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Server = 'root@play.secondhuntonline.com',
    [string]$Key = "$HOME\.ssh\secondhunt_deploy",
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$Rest
)
$ErrorActionPreference = 'Stop'
$quoted = ($Rest | ForEach-Object { "'" + ($_ -replace "'", "'\''") + "'" }) -join ' '
ssh -i $Key -o StrictHostKeyChecking=accept-new $Server "secondhunt-admin $quoted"
