#!/usr/bin/env bash
# Nightly copy of the server's database (sqlite3 .backup is safe while the server runs); the last 14 are kept.
set -euo pipefail
db=/var/lib/secondhunt/secondhunt.db
dir=/var/lib/secondhunt/backups
[ -f "$db" ] || exit 0
sqlite3 "$db" ".backup '$dir/secondhunt-$(date +%F).db'"
ls -1t "$dir"/secondhunt-*.db | tail -n +15 | xargs -r rm --
