using System;
using System.Runtime.CompilerServices;

// The runtime library exposes its internals to the desktop tools project, which
// was split out of this assembly (Program/Menu-driven CLI, test harnesses,
// repackers, exporters). Keeping the same `MphRead` namespaces across both
// assemblies means no `using` churn; InternalsVisibleTo covers internal members.
[assembly: InternalsVisibleTo("MphRead.Tools")]
[assembly: InternalsVisibleTo("MphRead.Android")]

namespace MphRead
{
    // Build/version info owned by the runtime, split out of Program (which now
    // lives in the tools project) so no runtime code depends on the desktop entry.
    public static class AppInfo
    {
        public static Version Version { get; } = new Version(0, 35, 0, 0);
    }

    // Core runtime exception. Was defined in Program.cs (which moved to the tools
    // project); it is thrown/used throughout the runtime, so it lives here now.
    public class ProgramException : Exception
    {
        public ProgramException(string message) : base(message) { }
    }
}
