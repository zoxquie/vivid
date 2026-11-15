using System.Reflection;

[assembly: AssemblyTitle(Vivid.AppInfo.Name)]
[assembly: AssemblyProduct(Vivid.AppInfo.Name)]
[assembly: AssemblyCompany("fjord.wtf")]
[assembly: AssemblyVersion(Vivid.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(Vivid.AppInfo.Version + ".0")]

namespace Vivid
{
    public static class AppInfo
    {
        /// <summary>Release builds set this from the git tag (v1.3.0 → 1.3.0).</summary>
        public const string Version = "1.7.0";
        /// <summary>The name people see. Internally the app is still "Vivid" (assembly name, Vivid.exe
        /// release asset, settings folder, single-instance names) so older copies keep updating.</summary>
        public const string Name = "Fjord Vision";
        public const string Site = "https://fjord.wtf";
        public const string SiteLabel = "fjord.wtf";
        /// <summary>GitHub repository that hosts the releases the app updates from.</summary>
        public const string Repo = "zoxquie/vivid";
    }
}
