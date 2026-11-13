using System.Reflection;

[assembly: AssemblyTitle("AI Usage Widget")]
[assembly: AssemblyProduct("AI Usage Widget")]
[assembly: AssemblyCompany("AI Usage Widget")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyConfiguration("Release")]

namespace AiUsageWidget
{
    /// <summary>
    /// Single source of the application release version. The Windows application
    /// manifest identity is checked against this value by the offline test suite.
    /// Configuration schema version is separate (<see cref="AppConfig.CurrentVersion"/>).
    /// </summary>
    public static class AppInfo
    {
        public const string Product = "AI Usage Widget";
        public const string Version = "1.0.0";
        public const string VersionWithPrefix = "v" + Version;
    }
}
