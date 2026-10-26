using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace EmbySeerrNative
{
    public sealed class PluginConfiguration : BasePluginConfiguration
    {
        public string SeerrUrl { get; set; } = "http://seerr:5055";
        public string ApiKey { get; set; } = "";
        public int PageSize { get; set; } = 20;
    }

    public sealed class Plugin : BasePlugin<PluginConfiguration>
    {
        public static Plugin Instance { get; private set; }
        public override string Name => "Seerr Discover (Native)";
        public override string Description => "Browse Seerr discovery catalogues as an Emby channel. Read-only first release.";
        public override Guid Id => new Guid("ed685bed-a759-4d9a-9482-3987767c3e4c");
        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer) { Instance = this; }
    }
}
