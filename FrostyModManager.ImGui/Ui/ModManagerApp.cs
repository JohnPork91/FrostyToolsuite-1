using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Frosty.Core;
using Frosty.Core.Managers;
using Frosty.Core.Mod;
using Frosty.ImGuiKit;
using FrostyCore;
using FrostyModManager.Gui.Model;
using FrostySdk;
using ImGuiNET;

namespace FrostyModManager.Gui.Ui
{
    /// <summary>
    /// The Mod Manager main window: replaces MainWindow.xaml / MainWindow.xaml.cs.
    /// Split across partial files by area: this file (flow, menu, layout), .Available, .Applied, .Details.
    /// </summary>
    internal sealed partial class ModManagerApp
    {
        private enum Phase { SelectGame, Loading, Main }

        private readonly ImGuiHost host;
        private readonly FrostyLogger logger;

        private ModManagerModel model = new ModManagerModel();
        private GameSelectScreen gameSelect;
        private Phase phase = Phase.SelectGame;

        private float leftPaneFraction = 0.5f;
        private string statusText = "";

        // files dropped onto the window arrive one event per file; gather them and install as a batch
        private readonly List<string> droppedFiles = new List<string>();
        private double lastDropTime;

        private readonly List<Toast> toasts = new List<Toast>();

        private sealed class Toast
        {
            public string Text;
            public double Expires;
        }

        public ModManagerApp(ImGuiHost host, FrostyLogger logger)
        {
            this.host = host;
            this.logger = logger;

            host.FileDropped += file =>
            {
                if (phase != Phase.Main)
                    return;

                droppedFiles.Add(file);
                lastDropTime = ImGui.GetTime();
            };

            // plugins call App.NotificationManager.Show(...), which would otherwise need a WPF Application
            NotificationManager.ExternalHandler = (text, duration) => ImGuiHost.Post(() =>
                toasts.Add(new Toast { Text = text, Expires = ImGui.GetTime() + Math.Max(1, duration) }));

            gameSelect = new GameSelectScreen(host, OnGameChosen);
        }

        // ---- flow ----------------------------------------------------------------------------------

        private void OnGameChosen(string profile)
        {
            phase = Phase.Loading;

            ProfileLoader.Load(profile, ok =>
            {
                if (!ok)
                {
                    // the WPF version closed the app here; when launched from the command line that is still right
                    if (Program.LaunchGameImmediately)
                        host.RequestClose();
                    else
                    {
                        gameSelect.Refresh();
                        phase = Phase.SelectGame;
                    }

                    return;
                }

                OnProfileLoaded();
            });
        }

        private void OnProfileLoaded()
        {
            Config.Save();
            host.SetTitle("Frosty Mod Manager - " + Frosty.Core.App.Version + " (" + ProfilesLibrary.DisplayName + ")");

            model = new ModManagerModel();
            ResetViewState();

            Dialogs.RunTask("Loading Mods", "", task => model.LoadMods(task), onDone: ctx =>
            {
                if (ctx.Exception != null)
                    Dialogs.Message("Some mods could not be loaded:\n" + ctx.Exception.Message, "Frosty Mod Manager");

                model.LoadPacks();

                string wanted = Config.Get<string>("SelectedPack", "", ConfigScope.Game);
                Pack pack = model.Packs.Find(p => p.Name == wanted) ?? model.Packs[0];

                if (Program.LaunchGameImmediately)
                {
                    Pack launchPack = model.Packs.Find(p => p.Name.Equals(Program.LaunchProfile, StringComparison.OrdinalIgnoreCase));
                    if (launchPack == null)
                    {
                        Dialogs.Message(string.Format("Unable to find pack with name {0}. Launch request cancelled", Program.LaunchProfile), "Frosty Mod Manager");
                        Program.LaunchGameImmediately = false;
                    }
                    else
                    {
                        pack = launchPack;
                    }
                }

                SelectPack(pack);
                phase = Phase.Main;

                if (Program.LaunchGameImmediately)
                    Launch();
            });
        }

        private void ReturnToGameSelect()
        {
            Config.Save();
            Frosty.Core.App.ClearProfileData();

            model = new ModManagerModel();
            ResetViewState();
            host.SetTitle("Frosty Mod Manager");

            gameSelect.Refresh();
            phase = Phase.SelectGame;
        }

        private void SelectPack(Pack pack)
        {
            model.SelectedPack = pack;

            pack.AppliedModsUpdated -= OnAppliedModsUpdated;
            pack.AppliedModsUpdated += OnAppliedModsUpdated;
            pack.Refresh();

            Config.Add("SelectedPack", pack.Name, ConfigScope.Game);

            appliedSelection.Clear();
            conflictsStale = true;
        }

        private void OnAppliedModsUpdated()
        {
            conflictsStale = true;
        }

        private void Launch()
        {
            // combine stored args with launch args
            string additionalArgs = Config.Get<string>("CommandLineArgs", "", ConfigScope.Game) + " " + Program.LaunchArgs;
            int retCode = 0;

            Dialogs.RunTask("Launching", "", task => retCode = model.Launch(task, additionalArgs), cancellable: true, onDone: ctx =>
            {
                if (ctx.Exception != null)
                    Dialogs.Message("Launch failed:\n" + ctx.Exception.Message, "Frosty Mod Manager");
                else if (retCode != -1)
                    host.Minimize();

                // kill the application if launched from the command line
                if (Program.LaunchGameImmediately)
                    host.RequestClose();

                GC.Collect();
            });
        }

        private void InstallFiles(string[] files)
        {
            InstallResult result = null;

            Dialogs.RunTask("Installing Mods", "", task => result = model.InstallMods(files, task), onDone: ctx =>
            {
                if (ctx.Exception != null)
                {
                    Dialogs.Message("Installing mods failed:\n" + ctx.Exception.Message, "Frosty Mod Manager");
                    return;
                }

                if (result.Errors.Count > 0)
                    Dialogs.Show(new InstallErrorsDialog(host, result.Errors));

                if (result.PackManifest != null)
                    ImportPackFromManifest(result.PackManifest);

                if (result.LastInstalledMod != null)
                {
                    availableSelection.SelectOnly(result.LastInstalledMod);
                    scrollToSelected = true;
                }

                RefreshStatus();
            });
        }

        /// <summary>An imported .fbpack carries a manifest: create a pack from it and apply its mods in order.</summary>
        private void ImportPackFromManifest(PackManifest manifest)
        {
            if (manifest.mods == null || string.IsNullOrWhiteSpace(manifest.name))
                return;

            string name = manifest.name;
            for (int i = 2; model.Packs.Exists(p => p.Name == name); i++)
                name = manifest.name + " (" + i + ")";

            Pack pack = model.AddPack(name, out _);
            if (pack == null)
                return;

            foreach (string filename in manifest.mods)
            {
                IFrostyMod mod;
                lock (model.Sync)
                    mod = FindModByFilename(filename);

                if (mod != null)
                    pack.AddMod(mod);
            }

            SelectPack(pack);
        }

        private IFrostyMod FindModByFilename(string filename)
        {
            IFrostyMod mod = model.AvailableMods.Find(a => a.Filename == filename);
            if (mod != null)
                return mod;

            foreach (FrostyModCollection collection in model.AvailableMods.OfType<FrostyModCollection>())
            {
                mod = collection.Mods.Find(a => a.Filename == filename);
                if (mod != null)
                    return mod;
            }

            return null;
        }

        private void ResetViewState()
        {
            availableSelection.Clear();
            appliedSelection.Clear();
            conflicts = null;
            conflictsStale = true;
            statusText = "";
        }

        // ---- per frame -----------------------------------------------------------------------------

        public void Draw()
        {
            double now = ImGui.GetTime();

            switch (phase)
            {
                case Phase.SelectGame:
                    gameSelect.Draw();
                    break;

                case Phase.Loading:
                    DrawBlankBackground();
                    break;

                case Phase.Main:
                    FlushDroppedFiles(now);
                    DrawMain();
                    break;
            }

            DrawToasts(now);
        }

        private static void DrawBlankBackground()
        {
            ImGuiViewportPtr vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos);
            ImGui.SetNextWindowSize(vp.WorkSize);

            ImGui.Begin("##loading", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                     ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus);
            ImGui.End();
        }

        private void FlushDroppedFiles(double now)
        {
            // wait a moment after the last drop event so a multi-file drop becomes one install
            if (droppedFiles.Count == 0 || now - lastDropTime < 0.2 || Dialogs.AnyOpen)
                return;

            string[] files = droppedFiles.ToArray();
            droppedFiles.Clear();
            InstallFiles(files);
        }

        private void DrawToasts(double now)
        {
            toasts.RemoveAll(t => t.Expires < now);
            if (toasts.Count == 0)
                return;

            ImDrawListPtr draw = ImGui.GetForegroundDrawList();
            Vector2 size = ImGui.GetMainViewport().Size;
            float y = size.Y - 12f;

            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                Vector2 textSize = ImGui.CalcTextSize(toasts[i].Text);
                Vector2 boxSize = textSize + new Vector2(24, 16);
                Vector2 min = new Vector2(size.X - boxSize.X - 12f, y - boxSize.Y);

                draw.AddRectFilled(min, min + boxSize, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.08f, 0.95f)), 3f);
                draw.AddRect(min, min + boxSize, ImGui.GetColorU32(Theme.ControlHighlight), 3f);
                draw.AddText(min + new Vector2(12, 8), ImGui.GetColorU32(Theme.FontColor), toasts[i].Text);

                y = min.Y - 6f;
            }
        }

        private void DrawMain()
        {
            ImGuiViewportPtr vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos);
            ImGui.SetNextWindowSize(vp.WorkSize);

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(6, 4));
            bool visible = ImGui.Begin("##main", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                                 ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings |
                                                 ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.MenuBar);
            ImGui.PopStyleVar();

            if (visible)
            {
                DrawMenuBar();

                float statusHeight = ImGui.GetTextLineHeightWithSpacing() + 4f;
                Vector2 avail = ImGui.GetContentRegionAvail();
                float splitterWidth = 6f;
                float leftWidth = Math.Max(240f, (avail.X - splitterWidth) * leftPaneFraction);
                float rightWidth = Math.Max(240f, avail.X - splitterWidth - leftWidth);
                float paneHeight = avail.Y - statusHeight;

                if (ImGui.BeginChild("##left", new Vector2(leftWidth, paneHeight)))
                    DrawLeftPane();
                ImGui.EndChild();

                ImGui.SameLine(0, 0);
                ImGui.InvisibleButton("##splitter", new Vector2(splitterWidth, paneHeight));
                if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                if (ImGui.IsItemActive() && avail.X > 0)
                    leftPaneFraction = Math.Clamp(leftPaneFraction + ImGui.GetIO().MouseDelta.X / avail.X, 0.25f, 0.75f);

                ImGui.SameLine(0, 0);
                if (ImGui.BeginChild("##right", new Vector2(rightWidth, paneHeight)))
                    DrawRightPane();
                ImGui.EndChild();

                ImGui.Separator();
                ImGui.TextDisabled(statusText);
            }

            ImGui.End();
        }

        private void DrawMenuBar()
        {
            if (!ImGui.BeginMenuBar())
                return;

            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.BeginMenu("Pack"))
                {
                    if (ImGui.MenuItem("Rename"))
                        RenameSelectedPack();
                    if (ImGui.MenuItem("Duplicate"))
                        DuplicateSelectedPack();
                    if (ImGui.MenuItem("Export"))
                        ExportSelectedPack();
                    if (ImGui.MenuItem("Import"))
                        ImportPack();
                    ImGui.EndMenu();
                }

                if (ImGui.MenuItem("Create Collection"))
                    NotPorted("Create Collection");

                if (ImGui.MenuItem("Select Game"))
                    ReturnToGameSelect();

                if (ImGui.MenuItem("Exit"))
                    host.RequestClose();

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Tools"))
            {
                if (ImGui.MenuItem("Manage ModData"))
                    NotPorted("Manage ModData");

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Help"))
            {
                if (ImGui.MenuItem("About Frosty Mod Manager"))
                    Dialogs.Show(new AboutDialog());

                ImGui.EndMenu();
            }

            ImGui.EndMenuBar();
        }

        private static void NotPorted(string feature)
        {
            Dialogs.Message(feature + " hasn't been ported to the ImGui Mod Manager yet.\n\nIt is still available in the WPF Mod Manager.", "Frosty Mod Manager");
        }

        // ---- panes ---------------------------------------------------------------------------------

        private void DrawLeftPane()
        {
            if (!ImGui.BeginTabBar("##lefttabs"))
                return;

            if (ImGui.BeginTabItem("Available Mods"))
            {
                DrawAvailableMods();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Installed Plugins"))
            {
                DrawPlugins();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Log"))
            {
                DrawLog();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        private void DrawRightPane()
        {
            if (!ImGui.BeginTabBar("##righttabs"))
                return;

            if (ImGui.BeginTabItem("Applied Mods"))
            {
                DrawAppliedMods();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Description"))
            {
                DrawDescription();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Actions"))
            {
                DrawActions();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        private static void DrawPlugins()
        {
            if (ImGui.BeginTable("##plugins", 3, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableHeadersRow();

                foreach (Plugin plugin in Frosty.Core.App.PluginManager.LoadedPlugins)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(plugin.Name ?? "");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(plugin.Author ?? "");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(plugin.Version ?? "");
                }

                ImGui.EndTable();
            }
        }

        private int lastLogLength;

        private void DrawLog()
        {
            string text = logger.LogText ?? "";

            if (ImGui.BeginChild("##logtext", new Vector2(0, 0)))
            {
                // keep following the tail unless the user scrolled up
                bool atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f;

                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();

                if (atBottom && text.Length != lastLogLength)
                    ImGui.SetScrollHereY(1f);

                lastLogLength = text.Length;
            }
            ImGui.EndChild();
        }

        private void RefreshStatus()
        {
            int count;
            lock (model.Sync)
                count = model.AvailableMods.Count;

            statusText = count + (count == 1 ? " mod available" : " mods available");
        }

        // ---- shared widgets ------------------------------------------------------------------------

        private void ModIcon(IFrostyMod mod, float size)
        {
            ImageHandle icon = default;
            byte[] data = mod?.ModDetails?.IconData;

            if (data != null && data.Length > 0)
                icon = host.Textures.FromBytes(mod, data);

            if (icon == null || !icon.IsValid)
                icon = host.Textures.FromFile("Images/DefaultModIcon.png");

            if (icon != null && icon.IsValid)
                ImGui.Image(icon.Id, new Vector2(size, size));
            else
                ImGui.Dummy(new Vector2(size, size));
        }

        private void SmallImage(string file, float size)
        {
            ImageHandle image = host.Textures.FromFile(file);
            if (image.IsValid)
                ImGui.Image(image.Id, new Vector2(size, size));
            else
                ImGui.Dummy(new Vector2(size, size));
        }

        /// <summary>Warning marker with the mod's warnings as tooltip. Draws nothing when there are none.</summary>
        private void WarningBadge(IFrostyMod mod)
        {
            if (mod == null || !mod.HasWarnings)
                return;

            SmallImage("Images/ModImportWarning.png", 12);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", mod.Warnings));

            ImGui.SameLine(0, 4);
        }
    }
}
