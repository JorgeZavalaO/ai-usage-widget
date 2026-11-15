using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace AiUsageWidget.Tests
{
    public static class VersionTests
    {
        [Test]
        public static void ApplicationVersion_MatchesAssemblyMetadata()
        {
            var assembly = typeof(AppInfo).Assembly;
            Assert.Equal(new Version(1, 1, 0, 0), assembly.GetName().Version);
            Assert.Equal("1.1.0", AppInfo.Version);
            Assert.Equal("v1.1.0", AppInfo.VersionWithPrefix);
        }

        [Test]
        public static void ApplicationManifestIdentity_MatchesApplicationVersion()
        {
            var manifest = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "src", "app.manifest"));
            var match = Regex.Match(manifest, "assemblyIdentity\\s+version=\"([^\"]+)\"");
            Assert.True(match.Success, "Manifest assemblyIdentity version was not found.");
            Assert.Equal(AppInfo.Version + ".0", match.Groups[1].Value);
        }

        [Test]
        public static void ConfigurationSchemaVersion_IsIndependentFromReleaseVersion()
        {
            Assert.Equal(1, AppConfig.CurrentVersion);
            Assert.NotEqual(AppInfo.Version, AppConfig.CurrentVersion.ToString());
            Assert.Equal(AppConfig.CurrentVersion, AppConfig.CreateDefault().Version);
        }
    }
}

