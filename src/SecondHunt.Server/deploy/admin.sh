#!/usr/bin/env bash
# The owner's moderation tool on the server (Social O7; docs/ONLINE_PLAY.md section 8). Installed as /usr/local/bin/secondhunt-admin
# by deploy.ps1; admin.ps1 on the PC runs it over ssh. It must run as the service's user so the database files keep their owner.
#   secondhunt-admin reports | show 12 | dismiss 12 | resolve 12 note | ban NAME#0421 7 reason | unban ... | mute ... | words text
exec sudo -u secondhunt env DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/secondhunt/.net \
    /opt/secondhunt/SecondHuntServer admin --db /var/lib/secondhunt/secondhunt.db --words /opt/secondhunt/words.txt "$@"
