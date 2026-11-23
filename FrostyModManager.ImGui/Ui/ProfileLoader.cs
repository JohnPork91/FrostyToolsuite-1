using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Frosty.Core;
using Frosty.Core.Legacy;
using Frosty.ImGuiKit;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using ImGuiNET;

namespace FrostyModManager.Gui.Ui
{
    /// <summary>
    /// Mod Manager half of Frosty.Core.Windows.FrostyProfileTaskWindow, without WPF: encryption-key handling,
    /// cache folder migration and loading the file system / resource / asset managers.
    /// Editor-only steps (localized strings, startup actions, patch summary) are not needed here.
    /// </summary>
    internal static class ProfileLoader
    {
        /// <summary>Shown in dialog titles; the Editor project links this file and sets its own name.</summary>
        public static string AppName { get; set; } = "Frosty Mod Manager";

        /// <summary>
        /// Extra loading steps that run on the worker thread after the managers are initialized
        /// (the Editor loads localized strings, the custom string list and plugin startup actions here).
        /// </summary>
        public static Action<TaskContext> AfterDataLoaded { get; set; }

        /// <summary>Loads <paramref name="profile"/>; <paramref name="done"/> receives true on success.</summary>
        public static void Load(string profile, Action<bool> done)
        {
            Frosty.Core.App.ClearProfileData();

            if (!ProfilesLibrary.Initialize(profile))
            {
                Dialogs.Message("There was an error when trying to load game using specified profile.", "Frosty Core", onClose: _ => done(false));
                return;
            }

            if (!Frosty.Core.App.IsEditor && !ProfilesLibrary.EnableExecution)
            {
                Dialogs.Message("The selected profile is a read-only profile, and therefore cannot be loaded in the mod manager",
                    AppName, onClose: _ => done(false));
                return;
            }

            LoadKeys(keysOk =>
            {
                if (!keysOk)
                {
                    done(false);
                    return;
                }

                StartDataLoad(done);
            });
        }

        private static void LoadKeys(Action<bool> next)
        {
            if (!ProfilesLibrary.RequiresKey)
            {
                next(true);
                return;
            }

            string keyFile = ProfilesLibrary.CacheName + ".key";
            if (File.Exists(keyFile))
            {
                ApplyKeys(NativeReader.ReadInStream(new FileStream(keyFile, FileMode.Open, FileAccess.Read)), next);
                return;
            }

            // prompt for encryption key
            Dialogs.Show(new KeyPromptDialog(
                keyData =>
                {
                    using (NativeWriter writer = new NativeWriter(new FileStream(keyFile, FileMode.Create)))
                        writer.Write(keyData);

                    ApplyKeys(keyData, next);
                },
                () => Dialogs.Message("Encryption key not entered. Unable to load profile.", AppName, onClose: _ => next(false))));
        }

        private static void ApplyKeys(byte[] keyData, Action<bool> next)
        {
            string keyFile = ProfilesLibrary.CacheName + ".key";

            try
            {
                // primary encryption key
                byte[] key = new byte[0x10];
                Array.Copy(keyData, key, 0x10);
                KeyManager.Instance.AddKey("Key1", key);

                if (keyData.Length > 0x10)
                {
                    // additional encryption keys
                    key = new byte[0x10];
                    Array.Copy(keyData, 0x10, key, 0, 0x10);
                    KeyManager.Instance.AddKey("Key2", key);

                    key = new byte[0x4000];
                    Array.Copy(keyData, 0x20, key, 0, 0x4000);
                    KeyManager.Instance.AddKey("Key3", key);
                }
            }
            catch
            {
                File.Delete(keyFile);
                Dialogs.Message("Encryption key is invalid. Unable to load profile.", AppName, onClose: _ => next(false));
                return;
            }

            next(true);
        }

        private static void StartDataLoad(Action<bool> done)
        {
            Config.Save();
            Frosty.Core.App.Logger.Log("Loading profile for " + ProfilesLibrary.DisplayName);

            bool sdkOutOfDate = false;

            Dialogs.RunTask("Loading " + ProfilesLibrary.DisplayName, "Initializing...", task =>
            {
                MoveLegacyCaches();

                AssetManagerImportResult result = new AssetManagerImportResult();
                LoadData(KeyManager.Instance.GetKey("Key1"), task, result);

                if (TypeLibrary.GetSdkVersion() != Frosty.Core.App.FileSystemManager.Head)
                    sdkOutOfDate = true;
                else
                    AfterDataLoaded?.Invoke(task);
            },
            cancellable: false,
            onDone: ctx =>
            {
                if (ctx.Exception != null)
                {
                    Dialogs.Message("Failed to load profile:\n" + ctx.Exception.Message, AppName, onClose: _ => done(false));
                    return;
                }

                if (sdkOutOfDate)
                {
                    // SdkUpdateWindow (SDK generation) has not been ported to ImGui yet.
                    Dialogs.Message(
                        "The SDK for this game is missing or out of date.\n\n" +
                        "Generating an SDK isn't available in the ImGui build yet. Run the WPF Frosty Editor once for this game to generate it, then try again.",
                        AppName, onClose: _ => done(false));
                    return;
                }

                Frosty.Core.App.Logger.Log("Initialization complete");
                done(true);
            },
            forwardTo: Frosty.Core.App.Logger);
        }

        private static void MoveLegacyCaches()
        {
            DirectoryInfo di = new DirectoryInfo("Caches");
            if (!di.Exists)
                Directory.CreateDirectory(di.FullName);

            // move any existing cache/sbdata.cas file over to the new caches directory
            foreach (string cacheName in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.cache"))
            {
                FileInfo fi = new FileInfo(cacheName);
                File.Move(fi.FullName, ".\\Caches\\" + fi.Name);

                string sbDataName = fi.FullName.Replace(".cache", "_sbdata.cas");
                if (File.Exists(sbDataName))
                    File.Move(sbDataName, ".\\Caches\\" + fi.Name.Replace(".cache", "_sbdata.cas"));
            }
        }

        private static void LoadData(byte[] key, TaskContext task, AssetManagerImportResult result)
        {
            string basePath = Config.Get<string>("GamePath", null, ConfigScope.Game);

            Frosty.Core.App.FileSystemManager = new FileSystemManager(basePath);
            foreach (FileSystemSource source in ProfilesLibrary.Sources)
                Frosty.Core.App.FileSystemManager.AddSource(source.Path, source.SubDirs);
            Frosty.Core.App.FileSystemManager.Initialize(key);

            Frosty.Core.App.ResourceManager = new ResourceManager(Frosty.Core.App.FileSystemManager);
            Frosty.Core.App.ResourceManager.SetLogger(task.TaskLogger);
            Frosty.Core.App.ResourceManager.Initialize();

            Frosty.Core.App.AssetManager = new AssetManager(Frosty.Core.App.FileSystemManager, Frosty.Core.App.ResourceManager);

            TypeLibrary.Initialize();

            // initialize plugin extensions
            Frosty.Core.App.PluginManager.Initialize();

            // load legacy asset manager if profile uses legacy system
            if (ProfilesLibrary.IsLoaded(ProfileVersion.Fifa17, ProfileVersion.Fifa18, ProfileVersion.Madden19,
                    ProfileVersion.Fifa19, ProfileVersion.Madden20, ProfileVersion.Fifa20,
                    ProfileVersion.PlantsVsZombiesBattleforNeighborville))
            {
                Frosty.Core.App.AssetManager.RegisterCustomAssetManager("legacy", typeof(LegacyFileManager));
            }
            else if (ProfilesLibrary.IsLoaded(ProfileVersion.Fifa21, ProfileVersion.Madden22, ProfileVersion.Fifa22,
                         ProfileVersion.Madden23, ProfileVersion.Fifa23))
            {
                Frosty.Core.App.AssetManager.RegisterCustomAssetManager("legacy", typeof(LegacyFileManagerV2));
            }

            // ensure mods folder is created
            DirectoryInfo di = new DirectoryInfo("Mods/" + ProfilesLibrary.ProfileName);
            if (!di.Exists)
                Directory.CreateDirectory(di.FullName);

            // newer ebx formats need the SDK for the types, so the SDK has to be current before the cache is generated
            if (ProfilesLibrary.EbxVersion > 4 && TypeLibrary.GetSdkVersion() != Frosty.Core.App.FileSystemManager.Head)
                return;

            Frosty.Core.App.AssetManager.SetLogger(task.TaskLogger);
            Frosty.Core.App.AssetManager.Initialize(Frosty.Core.App.IsEditor, result);
        }
    }

    /// <summary>Hex encryption key entry (port of KeyPromptWindow).</summary>
    internal sealed class KeyPromptDialog : Dialog
    {
        private readonly Action<byte[]> onKey;
        private readonly Action onCancel;
        private string text = "";
        private string error;

        protected override ImGuiWindowFlags Flags => ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings;

        public KeyPromptDialog(Action<byte[]> onKey, Action onCancel)
        {
            Title = "Encryption Key";
            this.onKey = onKey;
            this.onCancel = onCancel;
        }

        protected override void DrawContents()
        {
            ImGui.TextColored(Theme.Warning, "The developers cannot provide the encryption key.");
            ImGui.Spacing();
            ImGui.TextUnformatted("This game requires an encryption key before you can\naccess the game files.");
            ImGui.Spacing();
            ImGui.TextUnformatted("Please enter the encryption key:");

            // key material can be 0x4020 bytes (32 KB of hex), so the buffer has to be generous
            ImGui.SetNextItemWidth(420);
            ImGui.InputText("##key", ref text, 0x8100);

            if (error != null)
                ImGui.TextColored(Theme.Error, error);

            ImGui.Spacing();
            if (ImGui.Button("Done", new Vector2(80, 0)))
            {
                if (TryParse(text.Trim(), out byte[] key))
                {
                    Close();
                    onKey(key);
                }
                else
                {
                    error = "Encryption key is invalid. Please try again.";
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(80, 0)))
            {
                Close();
                onCancel();
            }
        }

        protected override void OnDismissed()
        {
            Close();
            onCancel();
        }

        private static bool TryParse(string hex, out byte[] key)
        {
            key = new byte[hex.Length / 2];
            try
            {
                for (int i = 0; i < key.Length; i++)
                    key[i] = byte.Parse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);

                return key.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
