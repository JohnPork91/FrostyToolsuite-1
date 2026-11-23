using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Frosty.Core;
using Frosty.Core.Managers;
using Frosty.ImGuiKit;
using FrostyModManager.Gui.Ui;
using FrostySdk;
using ImGuiNET;

namespace FrostyEditor.Gui.Ui
{
    /// <summary>
    /// The Editor main window: replaces MainWindow.xaml / MainWindow.xaml.cs and the Prelaunch/Splash windows.
    /// Split across partial files by area: this file (flow, menu, layout), .Project (project, launch, export), .Tabs (assets).
    ///
    /// Not ported yet: bookmarks, the legacy file explorer, the plugin-provided asset editors (textures, meshes, blueprints...),
    /// plugin toolbar/menu/tab extensions, the Options window, the SDK update window and the patch summary.
    /// </summary>
    internal sealed partial class EditorApp
    {
        private const string AppTitle = "Frosty Editor";

        private enum Phase { SelectGame, Loading, Main }

        private readonly ImGuiHost host;
        private readonly FrostyLogger logger;

        private readonly GameSelectScreen gameSelect;
        private readonly AssetExplorer explorer = new AssetExplorer();
        private Phase phase = Phase.SelectGame;

        private float explorerWidth = 360f;
        private float logHeight = 170f;
        private bool showLog = true;
        private string lastTitle;

        private readonly List<Toast> toasts = new List<Toast>();

        private sealed class Toast
        {
            public string Text;
            public double Expires;
        }

        public EditorApp(ImGuiHost host, FrostyLogger logger)
        {
            this.host = host;
            this.logger = logger;

            ProfileLoader.AppName = AppTitle;
            ProfileLoader.AfterDataLoaded = LoadEditorData;

            NotificationManager.ExternalHandler = (text, duration) => ImGuiHost.Post(() =>
                toasts.Add(new Toast { Text = text, Expires = ImGui.GetTime() + Math.Max(1, duration) }));

            host.FileDropped += file =>
            {
                if (phase == Phase.Main && file.EndsWith(".fbproject", StringComparison.OrdinalIgnoreCase))
                    ConfirmUnsavedThen(() => LoadProject(file));
            };

            explorer.OpenRequested = entry => OpenAsset(entry);
            explorer.RevertRequested = RevertAsset;
            explorer.ImportRequested = ImportAsset;
            explorer.ExportRequested = ExportAsset;

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
                    gameSelect.Refresh();
                    phase = Phase.SelectGame;
                    return;
                }

                OnProfileLoaded();
            });
        }

        /// <summary>Runs on the worker thread after the managers are up (the old SplashWindow's tail).</summary>
        private static void LoadEditorData(TaskContext task)
        {
            task.Update("Loading localized strings");
            Frosty.Core.App.PluginManager.GetLocalizedStringDatabase().Initialize();

            task.Update("Loading custom strings");
            FrostySdk.Utils.LoadStringList("strings.txt", task.TaskLogger);

            task.Update("Running startup actions");
            foreach (StartupAction startupAction in Frosty.Core.App.PluginManager.StartupActions)
                startupAction.Action(task.TaskLogger);
        }

        private void OnProfileLoaded()
        {
            Config.Save();

            // cleanup any outstanding editor mods (in case of a crash)
            try
            {
                string modsDir = "Mods/" + ProfilesLibrary.ProfileName;
                if (Directory.Exists(modsDir))
                {
                    foreach (string file in Directory.EnumerateFiles(modsDir, "EditorMod*"))
                        File.Delete(file);
                }
            }
            catch
            {
                // best effort
            }

            ResetEditorState();
            phase = Phase.Main;

            Frosty.Core.App.Logger.Log("Initialization complete");
            Frosty.Core.App.NotificationManager.Show("Initialization complete");

            if (!string.IsNullOrEmpty(Program.LaunchProject) && File.Exists(Program.LaunchProject))
                LoadProject(Program.LaunchProject);
        }

        private void ReturnToGameSelect()
        {
            ConfirmUnsavedThen(() =>
            {
                Config.Save();
                Frosty.Core.App.ClearProfileData();

                ResetEditorState();
                host.SetTitle(AppTitle);
                gameSelect.Refresh();
                phase = Phase.SelectGame;
            });
        }

        private void ResetEditorState()
        {
            tabs.Clear();
            explorer.Reset();
            project = new FrostyProject();
            recentProjects = Config.Get("RecentProjects", new List<string>(), ConfigScope.Game);
            lastAutosave = ImGui.GetTime();
        }

        /// <summary>Called after the window has closed. The OS close button can't be cancelled, so unsaved work is autosaved.</summary>
        public void OnShutdown()
        {
            if (phase != Phase.Main || project == null || !project.IsDirty)
                return;

            try
            {
                FlushPendingEdits();

                string name = Path.GetFileNameWithoutExtension(project.DisplayName);
                DateTime now = DateTime.Now;
                string path = "Autosave/" + name + "_" + now.ToString("ddMMyyyy_HHmmss") + ".fbproject";

                project.Save(overrideFilename: path, updateDirtyState: false);
                Console.WriteLine("Unsaved changes were autosaved to " + path);
            }
            catch
            {
                // nothing more can be done while shutting down
            }
        }

        // ---- frame ---------------------------------------------------------------------------------

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
                    DrawMain(now);
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

        private void DrawMain(double now)
        {
            UpdateTitle();
            TickAutosave(now);
            ApplyPendingEdits(now);

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
                DrawToolbar();

                Vector2 avail = ImGui.GetContentRegionAvail();
                const float splitter = 6f;

                explorerWidth = Math.Clamp(explorerWidth, 200f, Math.Max(200f, avail.X - 300f));

                if (ImGui.BeginChild("##explorer", new Vector2(explorerWidth, avail.Y)))
                    explorer.Draw();
                ImGui.EndChild();

                ImGui.SameLine(0, 0);
                ImGui.InvisibleButton("##vsplit", new Vector2(splitter, avail.Y));
                if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
                if (ImGui.IsItemActive())
                    explorerWidth += ImGui.GetIO().MouseDelta.X;

                ImGui.SameLine(0, 0);
                ImGui.BeginGroup();

                float rightWidth = ImGui.GetContentRegionAvail().X;
                float bottom = showLog ? logHeight + splitter : 0f;

                if (ImGui.BeginChild("##tabs", new Vector2(rightWidth, avail.Y - bottom)))
                    DrawTabs();
                ImGui.EndChild();

                if (showLog)
                {
                    ImGui.InvisibleButton("##hsplit", new Vector2(rightWidth, splitter));
                    if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                        ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
                    if (ImGui.IsItemActive())
                        logHeight = Math.Clamp(logHeight - ImGui.GetIO().MouseDelta.Y, 60f, Math.Max(60f, avail.Y - 160f));

                    if (ImGui.BeginChild("##log", new Vector2(rightWidth, logHeight)))
                        DrawLog();
                    ImGui.EndChild();
                }

                ImGui.EndGroup();
            }

            ImGui.End();
        }

        private void UpdateTitle()
        {
            string title = AppTitle + " - " + Frosty.Core.App.Version + " (" + ProfilesLibrary.DisplayName + ") " +
                           (ProfilesLibrary.EnableExecution ? "[" + project.DisplayName + (project.IsDirty ? "*" : "") + "]" : "[Read Only]");

            if (title != lastTitle)
            {
                lastTitle = title;
                host.SetTitle(title);
            }
        }

        // ---- menu / toolbar ------------------------------------------------------------------------

        private void DrawMenuBar()
        {
            if (!ImGui.BeginMenuBar())
                return;

            if (ImGui.BeginMenu("File"))
            {
                bool canEdit = ProfilesLibrary.EnableExecution;

                if (ImGui.MenuItem("New Project", "Ctrl+N"))
                    ConfirmUnsavedThen(NewProject);

                if (ImGui.MenuItem("Open Project...", "Ctrl+O"))
                    ConfirmUnsavedThen(OpenProjectDialog);

                DrawRecentProjectsMenu();

                ImGui.Separator();

                if (ImGui.MenuItem("Save Project", "Ctrl+S"))
                    SaveProject(false);

                if (ImGui.MenuItem("Save Project As..."))
                    SaveProject(true);

                ImGui.Separator();

                if (ImGui.MenuItem("Export Mod...", null, false, canEdit && Frosty.Core.App.AssetManager != null && Frosty.Core.App.AssetManager.GetModifiedCount() != 0))
                    ExportMod();

                ImGui.Separator();

                if (ImGui.MenuItem("Select Game"))
                    ReturnToGameSelect();

                if (ImGui.MenuItem("Exit"))
                    ConfirmUnsavedThen(host.RequestClose);

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("View"))
            {
                ImGui.MenuItem("Log", null, ref showLog);

                if (ImGui.MenuItem("Close All Tabs"))
                    CloseAllTabs();

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Tools"))
            {
                if (ImGui.MenuItem("Options..."))
                    NotPorted("The Options window");

                ImGui.EndMenu();
            }

            if (ImGui.BeginMenu("Help"))
            {
                if (ImGui.MenuItem("About Frosty Editor"))
                    Dialogs.Show(new EditorAboutDialog());

                ImGui.EndMenu();
            }

            ImGui.EndMenuBar();

            HandleShortcuts();
        }

        private void HandleShortcuts()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            if (io.WantTextInput || Dialogs.AnyOpen || !io.KeyCtrl)
                return;

            if (ImGui.IsKeyPressed(ImGuiKey.S, false))
                SaveProject(io.KeyShift);
            else if (ImGui.IsKeyPressed(ImGuiKey.N, false))
                ConfirmUnsavedThen(NewProject);
            else if (ImGui.IsKeyPressed(ImGuiKey.O, false))
                ConfirmUnsavedThen(OpenProjectDialog);
            else if (ImGui.IsKeyPressed(ImGuiKey.W, false))
                CloseCurrentTab();
        }

        private void DrawToolbar()
        {
            bool canLaunch = ProfilesLibrary.EnableExecution && !launching;

            ImGui.BeginDisabled(!canLaunch);
            if (ImGui.Button("Launch"))
                Launch();
            ImGui.EndDisabled();

            if (!ProfilesLibrary.EnableExecution && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("This profile is read-only");

            ImGui.SameLine();
            if (ImGui.Button("Save"))
                SaveProject(false);

            ImGui.SameLine();
            ImGui.TextDisabled(project.DisplayName + (project.IsDirty ? "*" : "") +
                               "   |   " + Frosty.Core.App.AssetManager.GetModifiedCount() + " modified assets");

            ImGui.Separator();
        }

        private static void NotPorted(string feature)
        {
            Dialogs.Message(feature + " hasn't been ported to the ImGui Editor yet.\n\nIt is still available in the WPF Frosty Editor.", AppTitle);
        }

        private int lastLogLength;

        private void DrawLog()
        {
            string text = logger.LogText ?? "";

            bool atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f;

            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();

            if (atBottom && text.Length != lastLogLength)
                ImGui.SetScrollHereY(1f);

            lastLogLength = text.Length;
        }
    }

    /// <summary>Replaces AboutWindow.</summary>
    internal sealed class EditorAboutDialog : Dialog
    {
        protected override Vector2? FixedSize => new Vector2(480, 420);
        protected override ImGuiWindowFlags Flags => ImGuiWindowFlags.NoSavedSettings;

        private readonly string credits;

        public EditorAboutDialog()
        {
            Title = "About Frosty Editor";

            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Credits.txt");
                credits = File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch
            {
                credits = "";
            }
        }

        protected override void DrawContents()
        {
            ImGui.TextUnformatted("Frosty Editor");
            ImGui.TextDisabled(Frosty.Core.App.Version);
            ImGui.Spacing();

            float footer = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetStyle().ItemSpacing.Y * 2;
            if (ImGui.BeginChild("##credits", new Vector2(0, -footer)))
            {
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted(credits);
                ImGui.PopTextWrapPos();
            }
            ImGui.EndChild();

            if (ImGui.SmallButton("Website: frostytoolsuite.com"))
                Program.OpenUrl("https://frostytoolsuite.com");

            if (ImGui.Button("OK", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                Close();
        }
    }
}
