# Word lists

`ldnoobw_en.txt` is the English list from **LDNOOBW** (List of Dirty, Naughty, Obscene, and Otherwise Bad Words),
https://github.com/LDNOOBW/List-of-Dirty-Naughty-Obscene-and-Otherwise-Bad-Words, licensed **CC BY 4.0**
(https://creativecommons.org/licenses/by/4.0/). Unmodified copy, fetched 2026-10-09 (403 entries). Credited in
`THIRD_PARTY_NOTICES.md`.

The server (`WordFilter.cs`) reads it as `words.txt` and, beside it, the owner's own `words_owner.txt` (not in the repo;
syntax in the comment at the top of `WordFilter.cs`). Both are re-read within 20 seconds of being edited. The filter is used
for online names and typed chat only; the game itself never loads these files.
