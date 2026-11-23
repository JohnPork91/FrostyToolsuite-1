using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Frosty.Core;
using Frosty.Core.Managers;
using Frosty.ImGuiKit;
using FrostyCore;
using FrostyEditor.Gui.Ui;
using FrostySdk;

namespace FrostyEditor.Gui
{
    /// <summary>
    /// Entry point. Port of the WPF App class (constructor + Application_Startup) without System.Windows.Application.
    /// </summary>
    internal static class Program
    {
        /// <summary>A .fbproject passed on the command line, opened once the game profile has loaded.</summary>
        public static string LaunchProject { get; private set; }

        [STAThread]
        private static int Main(string[] args)
        {
            Assembly entryAssembly = Assembly.GetEntryAssembly();
            Frosty.Core.App.Version = entryAssembly.GetName().Version + " — Virjoinga's Fork" + Frosty.Core.App.AlphaVersion;
            Frosty.Core.App.IsEditor = true;

            Environment.CurrentDirectory = AppContext.BaseDirectory;

            FrostyLogger logger = new FrostyLogger();
            Frosty.Core.App.Logger = logger;
            logger.Log("Frosty Editor v{0}", Frosty.Core.App.Version);

            FileUnblocker.UnblockDirectory(".\\");
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

            TypeLibrary.Initialize();
            Frosty.Core.App.PluginManager = new PluginManager(logger, PluginManagerType.Editor);
            ProfilesLibrary.Initialize(Frosty.Core.App.PluginManager.Profiles);
            Frosty.Core.App.NotificationManager = new NotificationManager();

#if FROSTY_DEVELOPER
            Frosty.Core.App.Version += " (Developer)";
#elif FROSTY_ALPHA
            Frosty.Core.App.Version += $" (ALPHA {Frosty.Core.App.Version})";
#elif FROSTY_BETA
            Frosty.Core.App.Version += $" (BETA {Frosty.Core.App.Version})";
#endif

            if (!File.Exists($"{Frosty.Core.App.GlobalSettingsPath}/editor_config.json"))
                Config.UpgradeConfigs();

            Config.Load();

            // "FrostyEditorImGui.exe project.fbproject" opens that project once the game has loaded
            if (args.Length > 0 && args[0].Contains(".fbproject"))
                LaunchProject = args[0];

            using (ImGuiHost host = new ImGuiHost("Frosty Editor", 1500, 900))
            {
                EditorApp app = new EditorApp(host, logger);

                if (Config.Get<bool>("UpdateCheck", true) || Config.Get<bool>("UpdateCheckPrerelease", false))
                    CheckVersion();

                host.Run(app.Draw);

                app.OnShutdown();
                Config.Save();
            }

            return 0;
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            ReadOnlySpan<char> dllName = args.Name;
            int commaIndex = dllName.IndexOf(',');
            if (commaIndex != -1)
                dllName = dllName.Slice(0, commaIndex);

            string baseDirectory = AppContext.BaseDirectory;

            if (dllName is "EbxClasses")
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(
                    Path.Combine(baseDirectory, $"Profiles/{ProfilesLibrary.SDKFilename}.dll"));
            }

            // otherwise, check for an existing assembly within the ThirdParty directory
            string thirdPartyPath = Path.Combine(baseDirectory, $"ThirdParty/{dllName}.dll");
            return File.Exists(thirdPartyPath) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(thirdPartyPath) : null;
        }

        private static void CheckVersion()
        {
            bool checkPrerelease = Config.Get<bool>("UpdateCheckPrerelease", false);
            Version localVersion = Assembly.GetEntryAssembly().GetName().Version;

            // network call off the UI thread; results are shown through the dialog queue
            Task.Run(() =>
            {
                try
                {
                    if (UpdateCheckerUtils.CheckVersion(checkPrerelease, localVersion))
                    {
                        Dialogs.Message("You are using an outdated version of Frosty." + Environment.NewLine +
                            "Would you like to download the latest version?", "Frosty Editor", DialogButtons.YesNo, result =>
                        {
                            if (result == DialogResult.Yes)
                                OpenUrl("https://github.com/CadeEvs/FrostyToolsuite/releases/latest");
                        });
                    }
                }
                catch (Exception e)
                {
                    Dialogs.Message("Frosty Update Checker returned with an error:" + Environment.NewLine + e.Message, "Frosty Editor");
                }
            });
        }

        /// <summary>Opens an http(s) URL in the default browser. Anything else is ignored.</summary>
        public static void OpenUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return;

            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }
}
