Second Hunt for Windows
=======================

Metroid Prime Hunters rebuilt as a native PC game, the Windows build of Second Hunt (a fork of MphRead).
Bring your own ROM: nothing of the game comes with this download.

Needs
-----
- Windows 10 or 11, 64-bit
- A graphics card or chip with OpenGL 3.3 or newer (any from the last ten years; keep its driver up to date)
- Your own Metroid Prime Hunters ROM: USA, version 1.0 or 1.1 (game code AMHE)

Start
-----
1. Unzip this folder anywhere (Desktop, Documents, a games folder) and open SecondHunt.exe.
2. Windows may say "Windows protected your PC" because the app isn't signed by a paid certificate.
   Click "More info", then "Run anyway".
3. The first time, pick your ROM (or drop the .nds file on the window). It is read once and unpacked
   into the app's data folder. After that the game starts straight away.

Keyboard and mouse (MphRead's own PC controls)
----------------------------------------------
Move            W A S D
Aim             mouse
Fire            left mouse button
Zoom / scan     right mouse button (scans with the scan visor on)
Jump / boost    Space
Morph ball      C
Bomb / scan     Q (left mouse button also lays bombs in morph ball)
Scan visor      E
Weapons         mouse wheel (next / previous weapon you have), hold middle mouse for the weapon menu, 1 to 9 to pick one
Pause menu      Tab or Esc
Map             M
Push to talk    V (online)
Chat            Enter or T (online match)
Status line     F3
Full screen     F11 or Alt+Enter

In the menus: arrow keys or W A S D move, Enter or Space choose, Esc or Backspace go back, Q / E page,
and the mouse clicks anything that the DS touch screen would.

In the game the arrow keys are the controller's D-pad (below).

A controller (Xbox, PlayStation and most others) works too, with the same buttons as the Android app:
left stick move, right stick aim, R2 or B fire, A jump, X morph, Y scan or zoom, tap L2 zoom, hold L2 for the
weapon ring, R3 push to talk (online), R1 missiles / Power Beam, D-pad left / right previous / next weapon,
D-pad up scan visor (Power Beam in a match), D-pad down missiles, Start pause, Back / View map (in an online match:
hold for quick chat messages, tap to type one).

These are the defaults. OPTIONS > CONTROLS (also SECOND HUNT SETTINGS > CONTROLS and the gunship's OPTIONS) changes
them: pick a function, then press its new key, mouse button or controller button (X adds a second one). The same
page has sensitivity and look invert for the controller and its own for the mouse (SECOND HUNT SETTINGS >
CONTROLS > AIM inverts up / down and left / right separately). Every change is saved at once.

Where things are kept
---------------------
Saves, settings and the unpacked game files are in
    %LOCALAPPDATA%\Second Hunt
(paste that into the Explorer address bar). The saves are the files in "saves". Copy that folder to back them up.
To remove the app, delete this folder and that one.

Multiplayer
-----------
MULTIPLAYER on the main menu: up to 8 players in every mode, with bots, on this PC, over LAN with other players on
the same network (Windows or Android), or online through ONLINE PLAY (find a game, browse, or create your own; friends,
voice and text chat; ages 13 and up). The first time you host or join, Windows asks whether to allow network access:
allow it. The prompt can hide behind a full-screen window, and on a network set to Public nothing gets through until
it's allowed. If other devices can't see your match or joining says no answer, open Windows Security > Firewall &
network protection > Allow an app through firewall, add SecondHunt.exe, and tick both Private and Public.

Extras
------
SECOND HUNT SETTINGS > IMPORT GAMES makes extra content from your own Metroid Prime, Echoes, Corruption or Trilogy
disc: HD Samus suits, the TROOPER and DARK SAMUS characters, and six Echoes multiplayer arenas. The disc is read where
it is, never copied or uploaded.

If something goes wrong
-----------------------
A crash writes a report to the "crash" folder in the data folder. SECOND HUNT SETTINGS > CRASH LOG opens it.
Reports and the log ("logs" folder) hold no personal data. Please attach the newest one when you report a problem
on the GitHub page: https://github.com/BountyHunterKanden/SecondHunt/issues

Metroid Prime Hunters is (c) Nintendo. This app contains no game code or data and is not affiliated with or
endorsed by Nintendo. The licences of everything it is built on are in THIRD_PARTY_NOTICES.txt and in-game under
SECOND HUNT SETTINGS > CREDITS.
