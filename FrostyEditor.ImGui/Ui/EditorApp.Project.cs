using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Frosty.Core;
using Frosty.Core.Managers;
using Frosty.Core.Mod;
using Frosty.ImGuiKit;
using Frosty.ModSupport;
using FrostySdk;
using ImGuiNET;

namespace FrostyEditor.Gui.Ui
{
    internal sealed partial class EditorApp
    {
        private FrostyProject project = new FrostyProject();
        private List<string> recentProjects = new List<string>();

        private bool launching;
        private bool autosaving;
        private double lastAutosave;
        private int lastAutosaveIndex;

        // ---- unsaved changes -----------------------------------------------------------------------

        /// <summary>Runs <paramref name="proceed"/> once the user has dealt with unsaved changes (saved or discarded them).</summary>
        private void ConfirmUnsavedThen(Action proceed)
        {
            FlushPendingEdits();

            if (!project.IsDirty)
            {
                proceed();
                return;
            }

            Dialogs.Message("Do you wish to save changes to " + project.DisplayName + "?", AppTitle, DialogButtons.YesNoCancel, result =>
            {
                if (result == DialogResult.Cancel)
                    return;

                if (result == DialogResult.Yes)
                    SaveProject(false, proceed);
                else
                    proceed();
            });
        }

        // ---- new / open / save ---------------------------------------------------------------------

        private void NewProject()
        {
            tabs.Clear();

            Dialogs.RunTask("New Project", "Clearing modifications", task => Frosty.Core.App.AssetManager.Reset(), onDone: ctx =>
            {
                project = new FrostyProject();
                explorer.Reset();

                Frosty.Core.App.Logger.Log("New project started");
                Frosty.Core.App.NotificationManager.Show("New project started");

                lastAutosave = ImGui.GetTime();
            });
        }

        private void OpenProjectDialog()
        {
            Dialogs.OpenFile("Open Project", "*.fbproject (Frosty Project)|*.fbproject", false, files => LoadProject(files[0]));
        }

        private void LoadProject(string filename)
        {
            if (!File.Exists(filename))
            {
                Dialogs.Message("The selected project does not exist.", AppTitle);
                return;
            }

            tabs.Clear();

            FrostyProject loaded = new FrostyProject();
            bool ok = false;

            Dialogs.RunTask("Loading Project", filename, task => ok = loaded.Load(filename), onDone: ctx =>
            {
                if (ctx.Exception != null)
                {
                    Frosty.Core.App.Logger.LogWarning("Failed to load {0}: {1}", filename, ctx.Exception.Message);
                    return;
                }

                if (!ok)
                {
                    if (ProfilesLibrary.DataVersion != loaded.gameVersion)
                        Frosty.Core.App.Logger.LogWarning("Project {0} is not for {1}.", filename, ProfilesLibrary.DisplayName);
                    else
                        Frosty.Core.App.Logger.LogWarning("Failed to load {0}", filename);

                    return;
                }

                project = loaded;
                explorer.Reset();

                Frosty.Core.App.Logger.Log("Loaded {0}", project.Filename);
                Frosty.Core.App.NotificationManager.Show("Loaded " + project.Filename);

                AddRecentProject(project.Filename);
                lastAutosave = ImGui.GetTime();
            });
        }

        private void SaveProject(bool forceSaveAs, Action afterSaved = null)
        {
            FlushPendingEdits();

            if (string.IsNullOrEmpty(project.Filename) || forceSaveAs)
            {
                Dialogs.SaveFile("Save Project As", "*.fbproject (Frosty Project)|*.fbproject", "fbproject", path =>
                {
                    project.Filename = path;
                    WriteProject(afterSaved);
                });
            }
            else
            {
                WriteProject(afterSaved);
            }
        }

        private void WriteProject(Action afterSaved)
        {
            string filename = project.Filename;

            Dialogs.RunTask("Saving Project", filename, task => project.Save(), onDone: ctx =>
            {
                if (ctx.Exception != null)
                {
                    Dialogs.Message("Couldn't save the project:\n" + ctx.Exception.Message, AppTitle);
                    return;
                }

                Frosty.Core.App.Logger.Log("Project saved to {0}", filename);
                AddRecentProject(filename);

                explorer.Refresh();
                lastAutosave = ImGui.GetTime();

                afterSaved?.Invoke();
            });
        }

        private void TickAutosave(double now)
        {
            if (autosaving || !Config.Get<bool>("AutosaveEnabled", true))
                return;

            int minutes = Config.Get<int>("AutosavePeriod", 5);
            if (minutes <= 0 || now - lastAutosave < minutes * 60.0)
                return;

            lastAutosave = now;
            if (!project.IsDirty || Dialogs.AnyOpen)
                return;

            FlushPendingEdits();

            ++lastAutosaveIndex;
            if (lastAutosaveIndex > Config.Get<int>("AutosaveMaxCount", 10))
                lastAutosaveIndex = 1;

            string name = Path.GetFileNameWithoutExtension(project.DisplayName);
            FileInfo fi = new FileInfo("Autosave/" + name + "_" + lastAutosaveIndex.ToString("D3") + ".fbproject");

            Frosty.Core.App.Logger.Log("Initiated autosave of project to " + fi.FullName);

            autosaving = true;
            FrostyProject snapshot = project;

            // saves without touching the dirty state, like the old timer thread did
            Task.Run(() =>
            {
                try
                {
                    snapshot.Save(overrideFilename: fi.FullName, updateDirtyState: false);
                }
                catch (Exception e)
                {
                    Frosty.Core.App.Logger.LogError("Autosave failed: {0}", e.Message);
                }
                finally
                {
                    autosaving = false;
                }
            });
        }

        // ---- recent projects -----------------------------------------------------------------------

        private void AddRecentProject(string path)
        {
            recentProjects.Remove(path);
            recentProjects.Insert(0, path);

            while (recentProjects.Count > 10)
                recentProjects.RemoveAt(recentProjects.Count - 1);

            Config.Add("RecentProjects", recentProjects, ConfigScope.Game);
            Config.Save();
        }

        private void DrawRecentProjectsMenu()
        {
            // drop projects whose files are gone, as the old menu did
            if (recentProjects.RemoveAll(p => !File.Exists(p)) > 0)
            {
                Config.Add("RecentProjects", recentProjects, ConfigScope.Game);
                Config.Save();
            }

            if (!ImGui.BeginMenu("Recent Projects", recentProjects.Count > 0))
                return;

            int index = 1;
            foreach (string recent in recentProjects.ToList())
            {
                string label = index++ + ": " + Path.GetPathRoot(recent) + "...\\" + Path.GetFileName(recent);
                if (ImGui.MenuItem(label + "##" + recent))
                    ConfirmUnsavedThen(() => LoadProject(recent));
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Clear Recent Projects"))
            {
                recentProjects.Clear();
                Config.Add("RecentProjects", recentProjects, ConfigScope.Game);
                Config.Save();
            }

            ImGui.EndMenu();
        }

        // ---- export mod ----------------------------------------------------------------------------

        private void ExportMod()
        {
            FlushPendingEdits();

            Dialogs.Show(new ModSettingsDialog(host, project.ModSettings, () =>
            {
                Dialogs.SaveFile("Save Mod", "*.fbmod (Frosty Mod)|*.fbmod", "fbmod", filename =>
                {
                    Dialogs.RunTask("Saving Mod", filename, task => project.WriteToMod(filename, project.GetModSettings()), onDone: ctx =>
                    {
                        if (ctx.Exception != null)
                            Dialogs.Message("Couldn't save the mod:\n" + ctx.Exception.Message, AppTitle);
                        else
                            Frosty.Core.App.Logger.Log("Mod saved to {0}", filename);
                    });
                });
            }));
        }

        // ---- launch --------------------------------------------------------------------------------

        /// <summary>Exports the project as a temporary mod, applies all installed mods with it and starts the game.</summary>
        private void Launch()
        {
            if (!ProfilesLibrary.EnableExecution || launching)
                return;

            FlushPendingEdits();
            launching = true;

            string modsDirectory = "Mods/" + ProfilesLibrary.ProfileName + "/";
            List<string> modPaths = new List<string>();

            try
            {
                foreach (string modPath in Directory.EnumerateFiles(modsDirectory, "*.fbmod", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(modPath).Contains("EditorMod"))
                        File.Delete(modPath);
                    else
                        modPaths.Add(Path.GetFileName(modPath));
                }
            }
            catch (Exception e)
            {
                launching = false;
                Dialogs.Message("Couldn't read the mods folder:\n" + e.Message, AppTitle);
                return;
            }

            string editorModName = "EditorMod_" + new Random().Next(1000, 9999).ToString("D4") + ".fbmod";
            string editorModPath = modsDirectory + editorModName;

            ModSettings editorSettings = new ModSettings
            {
                Title = editorModName,
                Author = "Frosty Editor",
                Version = Frosty.Core.App.Version,
                Category = "Editor"
            };

            Frosty.Core.App.SelectedPack = "Editor";
            FrostyModExecutor executor = new FrostyModExecutor();
            bool cancelled = false;

            Dialogs.RunTask("Launching", "", task =>
            {
                var token = task.Cancellation.Token;

                try
                {
                    foreach (ExecutionAction action in Frosty.Core.App.PluginManager.ExecutionActions)
                        action.PreLaunchAction(task.TaskLogger, PluginManagerType.Editor, token);

                    task.Update("Exporting Mod");
                    project.WriteToMod(editorModPath, editorSettings);
                    modPaths.Add(editorModName);
                    Frosty.Core.App.Logger.Log("Temporary editor mod saved to {0}", editorModPath);

                    // cancelling only takes effect after the mod has been written
                    token.ThrowIfCancellationRequested();

                    task.Update("Removing mods.json");
                    string patchPath = "Patch";
                    int version = ProfilesLibrary.DataVersion;

                    if (version == (int)ProfileVersion.Fifa17 || version == (int)ProfileVersion.DragonAgeInquisition ||
                        version == (int)ProfileVersion.Battlefield4 || version == (int)ProfileVersion.NeedForSpeed ||
                        version == (int)ProfileVersion.PlantsVsZombiesGardenWarfare2 || version == (int)ProfileVersion.NeedForSpeedRivals)
                    {
                        patchPath = "Update\\Patch\\Data";
                    }
                    else if (version == (int)ProfileVersion.PlantsVsZombiesBattleforNeighborville || version == (int)ProfileVersion.Battlefield5 ||
                             version == (int)ProfileVersion.NeedForSpeedUnbound || version == (int)ProfileVersion.DeadSpace)
                    {
                        // these games have no patch directory
                        patchPath = "Data";
                    }

                    string modsJson = Frosty.Core.App.FileSystemManager.BasePath + "\\ModData\\" + Frosty.Core.App.SelectedPack + "\\" + patchPath + "\\mods.json";
                    if (File.Exists(modsJson))
                    {
                        File.Delete(modsJson);
                        Frosty.Core.App.Logger.Log("Removed mods.json");
                    }

                    task.Update("");
                    executor.Run(Frosty.Core.App.FileSystemManager, token, task.TaskLogger, modsDirectory,
                        Frosty.Core.App.SelectedPack, "", modPaths.ToArray());

                    foreach (ExecutionAction action in Frosty.Core.App.PluginManager.ExecutionActions)
                        action.PostLaunchAction(task.TaskLogger, PluginManagerType.Editor, token);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;

                    foreach (ExecutionAction action in Frosty.Core.App.PluginManager.ExecutionActions)
                        action.PostLaunchAction(task.TaskLogger, PluginManagerType.Editor, token);
                }
            },
            cancellable: true,
            onDone: ctx =>
            {
                try
                {
                    FileInfo editorMod = new FileInfo(editorModPath);
                    if (editorMod.Exists)
                        editorMod.Delete();
                }
                catch
                {
                    // the next start removes leftover EditorMod files
                }

                launching = false;
                GC.Collect();

                if (cancelled)
                {
                    Frosty.Core.App.Logger.Log("Launch Cancelled");
                    Frosty.Core.App.NotificationManager.Show("Launch Cancelled");
                }
                else if (ctx.Exception != null)
                {
                    Dialogs.Message("Launch failed:\n" + ctx.Exception.Message, AppTitle);
                }
            });
        }
    }
}
