using System.Runtime.CompilerServices;

// The desktop tools project hosts our headless test harnesses (-savetest, -modtest,
// -inputtest, -simtest, ...), which are white-box tests that exercise internal
// members of these systems (e.g. ModInstance.Data, InputMap.RadialDeadZone). Grant
// it -- and the Android app, for future white-box sim/render wiring -- friend
// access. Mirrors the same InternalsVisibleTo pattern MphRead uses in AppInfo.cs.
// Note: this is the Core -> Tools/Android direction; the Core -> MphRead direction
// needs no such grant (RoomLoader uses only MphRead's public API).
[assembly: InternalsVisibleTo("MphRead.Tools")]
[assembly: InternalsVisibleTo("MphRead.Android")]
