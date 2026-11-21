using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;
using Frosty.Core;
using Frosty.Core.Managers;
using Frosty.ImGuiKit;
using FrostyCore;
using FrostyEditor;
using FrostyModManager.Gui.Ui;
using FrostySdk;

namespace FrostyModManager.Gui
{
    /// <summary>
    /// Entry point. Port of the WPF App class (constructor + Application_Startup) without System.Windows.Application.
    /// </summary>
    internal static class Program
    {
        public static bool LaunchGameImmediately { get; set; }
        public static string LaunchProfile { get; private set; }
        public static string LaunchArgs { get; private set; } = "";

        [STAThread]
        private static int Main(string[] args)
        {
            // Frosty.Core.App is a static class holding the managers plugins reach for; keep it initialized exactly as before.
            Assembly entryAssembly = Assembly.GetEntryAssembly();
            Frosty.Core.App.Version = entryAssembly.GetName().Version + " — Virjoinga's Fork" + Frosty.Core.App.AlphaVersion;
            Frosty.Core.App.IsEditor = false;

            FrostyLogger logger = new FrostyLogger();
            Frosty.Core.App.Logger = logger;
            logger.Log("Frosty Mod Manager v{0}", Frosty.Core.App.Version);

            FileUnblocker.UnblockDirectory(".\\");
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

            TypeLibrary.Initialize();
            Frosty.Core.App.PluginManager = new PluginManager(logger, PluginManagerType.ModManager);
            ProfilesLibrary.Initialize(Frosty.Core.App.PluginManager.Profiles);
            Frosty.Core.App.NotificationManager = new NotificationManager();

#if FROSTY_DEVELOPER
            Frosty.Core.App.Version += " (Developer)";
#elif FROSTY_ALPHA
            Frosty.Core.App.Version += $" (ALPHA {Frosty.Core.App.Version})";
#elif FROSTY_BETA
            Frosty.Core.App.Version += $" (BETA {Frosty.Core.App.Version})";
#endif

            if (!File.Exists($"{Frosty.Core.App.GlobalSettingsPath}/manager_config.json"))
                Config.UpgradeConfigs();

            Config.Load();

            if (!ParseArguments(args))
                return 1;

            using (ImGuiHost host = new ImGuiHost("Frosty Mod Manager", 1000, 750))
            {
                ModManagerApp app = new ModManagerApp(host, logger);

                if (Config.Get<bool>("UpdateCheck", true) || Config.Get<bool>("UpdateCheckPrerelease", false))
                    CheckVersion();

                host.Run(app.Draw);
                Config.Save();
            }

            return 0;
        }

        private static bool ParseArguments(string[] args)
        {
            // "-launch <pack> [game args...]" starts the game immediately with the named pack
            if (args.Length == 0 || args[0] != "-launch")
                return true;

            if (args.Length < 2)
            {
                Console.Error.WriteLine("-launch argument found, but missing profile name");
                return false;
            }

            LaunchGameImmediately = true;
            LaunchProfile = args[1];

            StringBuilder sb = new StringBuilder();
            for (int i = 2; i < args.Length; i++)
                sb.Append(args[i]).Append(' ');

            LaunchArgs = sb.ToString().Trim();
            return true;
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
                            "Would you like to download the latest version?", "Frosty Mod Manager", DialogButtons.YesNo, result =>
                        {
                            if (result == DialogResult.Yes)
                                OpenUrl("https://github.com/CadeEvs/FrostyToolsuite/releases/latest");
                        });
                    }
                }
                catch (Exception e)
                {
                    Dialogs.Message("Frosty Update Checker returned with an error:" + Environment.NewLine + e.Message, "Frosty Mod Manager");
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
