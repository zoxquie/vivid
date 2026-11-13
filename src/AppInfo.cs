using System.Reflection;

[assembly: AssemblyTitle("Vivid")]
[assembly: AssemblyProduct("Vivid")]
[assembly: AssemblyVersion(Vivid.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(Vivid.AppInfo.Version + ".0")]

namespace Vivid
{
    public static class AppInfo
    {
        /// <summary>Release builds set this from the git tag (v1.3.0 → 1.3.0).</summary>
        public const string Version = "1.3.0";
        /// <summary>GitHub repository that hosts the releases the app updates from.</summary>
        public const string Repo = "zoxquie/vivid";
    }
}
