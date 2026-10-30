# SDL3 / Switch Pro controller on PC: scope (D6, GitHub #6)

Thread D, 2026-10-24. Scope only. Nothing is built yet; this waits for the owner's call.

## What PC does today

- **How pads are read.** `src/SecondHunt.Desktop/DesktopInput.cs` reads pads through GLFW 3.4, the version bundled
  with OpenTK.Redist.Glfw 3.4.0.44.
  - It uses `JoystickIsGamepad` and `GetGamepadState` and reads the first pad that matches GLFW's mapping database.
  - That database is a copy of SDL's gamecontrollerdb.
- **The Pro controller has a mapping.** The bundled glfw3.dll includes `030000007e0500000920000000000000,Nintendo
  Switch Pro Controller` for Windows (DirectInput). So a Pro controller paired over **Bluetooth** shows up and plays
  today, through Windows' generic HID mode.
- **What that mode gets wrong:**
  - The sticks come through uncalibrated, so the centre and range are off. Our 0.15 deadzone hides most of that.
  - Buttons sit in Xbox positions. The Pro controller's A is on the right, where Xbox has B, so "A" on screen means a
    different physical button than the Switch habit.
  - There's no gyro, rumble or battery level.
- **Over USB it most likely doesn't work at all.** The Pro controller sends nothing until the host does Nintendo's USB
  handshake. Windows and GLFW don't do that handshake; SDL's HIDAPI driver does. Steam Input also does it, which is
  why it "works through Steam". I haven't tested this on our build: it needs the player's pad or one of ours.
- **Other pads:**
  - DualSense and DualShock 4 also go through DirectInput mappings, with no gyro.
  - Xbox pads go through XInput and are fine.

## What SDL3 would add

SDL3 (zlib licence, so fine to ship) has a HIDAPI driver for the Pro controller, Joy-Cons, DualSense, DualShock 4 and
others:

1. **USB and Bluetooth Pro controller,** with calibrated full-range sticks and rumble.
2. **Gyro on PC.** SDL exposes the Pro controller's, Joy-Cons' and DualSense/DS4's gyro and accelerometer through
   `SDL_SetGamepadSensorEnabled` and `SDL_GetGamepadSensorData`, in rad/s.
   - The axis frame is standard: X right, Y up, Z toward the player.
   - That data feeds straight into the existing Core `GyroAim`, the same math as the Odin, with every D2 tuning row
     included.
   - The gyro rows that `HiddenOnDesktop` hides today would show whenever a gyro pad is connected.
3. **Labels.** SDL reports the pad type (`SDL_GetGamepadType`), so we can offer a "Nintendo layout" switch: swap A/B and
   X/Y by position, and show Nintendo names in hints.
4. **Better pad support overall:**
   - Proper names and hotplug.
   - Several pads told apart (only the first is read today).
   - Player LEDs.

## How it would fit (clean, no shim)

- **D6a: pad reading.** Load SDL3 for pads only (`SDL_Init(SDL_INIT_GAMEPAD)`); GLFW keeps the window, keyboard and
  mouse.
  - A small P/Invoke file of our own, about 20 functions: init, gamepad open/close/events, buttons, axes, sensors,
    type, rumble.
  - `PollGamepad` reads from SDL when SDL is present and falls back to today's GLFW path when the DLL is missing.
    Never both, so no pad is read twice.
  - Set `SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS`. The window is GLFW's, so SDL never sees it get focus.
  - About one session. Check: the existing pad checks plus a `-padtest` in Tools that lists what SDL sees.
- **D6b: gyro on PC.** A desktop `IGyroAimSource` in the Desktop project, like the Android `CampaignGyro.cs` but fed
  from SDL sensor data.
  - Show the gyro rows when a gyro pad is in.
  - Calibrate the bias at rest, as today.
  - About half a session. Check: `-gyrotest`, then a PC run with a DualSense or Pro controller.
- **D6c: Nintendo layout option.** Default auto from the pad type, with an OFF choice. Small.
- **D6d: packaging.** Ship `SDL3.dll` (about 2.5 MB, win-x64) in the release zip, add a THIRD_PARTY_NOTICES entry
  (zlib), and check the release-build gate. A Linux or macOS build later needs its own SDL3 native.

## Risks

- **Steam Input.** When Steam is running with Steam Input on for the pad, it may hold the HID device, and the game then
  sees Steam's virtual Xbox pad: no gyro, but still playable. That's the usual behaviour for SDL games; to note in help.
- **Exclusive HID access** can fight other tools: DS4Windows, BetterJoy, reWASD. SDL's hints can turn HIDAPI off per
  pad family if players report trouble. We'd expose that as a setting only if needed.
- **Mapping changes.** Pads that work through GLFW's mapping today could map slightly differently through SDL, which
  has the same database plus HIDAPI. Saved binds use our PadButton names, so binds don't break.
- **Testing needs real pads.** I can build and unit-check it, but the Pro controller and DualSense runs need the owner
  or the player who filed #6.
- **Android:** nothing is needed. Android already reads the Pro controller natively. Pad gyro on Android, through
  `InputDevice.getSensorManager` on Android 12+, is a separate and later question.

## Recommendation

Build D6a + D6c first, about a session. They fix USB Pro controllers and the button positions, with GLFW as a
fallback. Then D6b, about half a session, which turns the D2 gyro work into a PC feature for Pro and DualSense owners.
All four parts are separate, and each one ships on its own.
