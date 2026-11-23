using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using Frosty.Core;
using Frosty.ImGuiKit;
using FrostySdk;
using ImGuiNET;
using Microsoft.Win32;

namespace FrostyModManager.Gui.Ui
{
    /// <summary>
    /// Game picker. Port of Frosty.Core.Windows.FrostyProfileSelectWindow, drawn as a full-window screen
    /// instead of a modal window. Uses its own entry type instead of FrostyConfiguration, which builds WPF
    /// ImageSources and so needs a running WPF Application.
    /// </summary>
    internal sealed class GameSelectScreen
    {
        private sealed class Entry
        {
            public string Profile;
            public string GameName;
            public string GamePath;
            public byte[] IconPng;
        }

        private readonly ImGuiHost host;
        private readonly Action<string> onChosen;
        private readonly List<Entry> entries = new List<Entry>();
        private Entry selected;
        private bool scanning;
        private bool initialScanDone;

        public GameSelectScreen(ImGuiHost host, Action<string> onChosen)
        {
            this.host = host;
            this.onChosen = onChosen;
            Refresh();
        }

        /// <summary>Rebuilds the list from the config; drops games whose executable has disappeared.</summary>
        public void Refresh()
        {
            entries.Clear();
            selected = null;

            foreach (string profile in Config.GameProfiles)
            {
                try
                {
                    entries.Add(CreateEntry(profile));
                }
                catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
                {
                    // couldn't find the exe, so remove it from the profile list
                    Config.RemoveGame(profile);
                    Config.Save();
                }
            }
        }

        private static Entry CreateEntry(string profile)
        {
            string gamePath = Config.Get<string>("GamePath", "", ConfigScope.Game, profile);
            string exe = Path.Combine(gamePath, profile) + ".exe";

            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exe); // throws FileNotFoundException if missing

            Entry entry = new Entry
            {
                Profile = profile,
                GamePath = gamePath,
                GameName = string.IsNullOrWhiteSpace(vi.ProductName) ? profile : vi.ProductName
            };

            try
            {
                using (System.Drawing.Icon icon = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                using (System.Drawing.Bitmap bitmap = icon.ToBitmap())
                using (MemoryStream ms = new MemoryStream())
                {
                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    entry.IconPng = ms.ToArray();
                }
            }
            catch
            {
                // no icon is fine; the row just won't have one
            }

            return entry;
        }

        public void Draw()
        {
            ImGuiViewportPtr vp = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(vp.WorkPos);
            ImGui.SetNextWindowSize(vp.WorkSize);

            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                                           ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBringToFrontOnFocus;

            if (!ImGui.Begin("##gameselect", flags))
            {
                ImGui.End();
                return;
            }

            // first run with nothing configured: look for installed games automatically
            if (!initialScanDone)
            {
                initialScanDone = true;
                if (entries.Count == 0)
                    Scan();
            }

            ImGui.TextUnformatted("Select a game");
            ImGui.TextDisabled(ProfileLoader.AppName + " " + Frosty.Core.App.Version);
            ImGui.Separator();

            float footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
            if (ImGui.BeginChild("##games", new Vector2(0, -footer)))
            {
                if (entries.Count == 0)
                {
                    ImGui.Spacing();
                    ImGui.TextWrapped(scanning
                        ? "Scanning for installed games..."
                        : "No games found. Use \"Add Game...\" to choose a game executable.");
                }

                foreach (Entry entry in entries)
                    DrawEntry(entry);
            }
            ImGui.EndChild();

            ImGui.BeginDisabled(selected == null);
            if (ImGui.Button("Select", new Vector2(100, 0)))
                Choose(selected);
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Add Game...", new Vector2(100, 0)))
                AddGame();

            ImGui.SameLine();
            ImGui.BeginDisabled(selected == null);
            if (ImGui.Button("Remove", new Vector2(100, 0)))
                RemoveSelected();
            ImGui.EndDisabled();

            ImGui.SameLine();
            ImGui.BeginDisabled(scanning);
            if (ImGui.Button("Rescan", new Vector2(100, 0)))
                Scan();
            ImGui.EndDisabled();

            ImGui.End();
        }

        private void DrawEntry(Entry entry)
        {
            const float iconSize = 40f;

            ImGui.PushID(entry.Profile);

            float x0 = ImGui.GetCursorPosX();
            bool isSelected = selected == entry;

            if (ImGui.Selectable("##entry", isSelected, ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowOverlap, new Vector2(0, iconSize + 4)))
            {
                selected = entry;
                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    Choose(entry);
            }

            ImGui.SameLine(x0 + 4, 0f);

            ImageHandle icon = entry.IconPng != null ? host.Textures.FromBytes(entry, entry.IconPng) : host.Textures.FromFile("Images/Warning.png");
            if (icon.IsValid)
                ImGui.Image(icon.Id, new Vector2(iconSize, iconSize));
            else
                ImGui.Dummy(new Vector2(iconSize, iconSize));

            ImGui.SameLine(0, 10f);
            ImGui.BeginGroup();
            ImGui.TextUnformatted(entry.GameName);
            ImGui.TextDisabled(entry.GamePath);
            ImGui.EndGroup();

            ImGui.PopID();
        }

        private void Choose(Entry entry)
        {
            if (entry == null)
                return;

            if (entry.Profile == "Dragon Age The Veilguard")
            {
                Dialogs.Message(entry.GameName + " is not supported.\n\n" +
                                "This release is never meant to support Dragon Age™: The Veilguard. Use J-Lyt's release for that game.",
                    "Unsupported Profile");
                return;
            }

            onChosen(entry.Profile);
        }

        private void RemoveSelected()
        {
            Entry entry = selected;
            if (entry == null)
                return;

            Dialogs.Message("Are you sure you want to remove this profile?", "Remove Profile", DialogButtons.YesNo, result =>
            {
                if (result != DialogResult.Yes)
                    return;

                Config.RemoveGame(entry.Profile);
                Config.Save();
                Refresh();
            });
        }

        private void AddGame()
        {
            Dialogs.OpenFile("Choose Game Executable", "Game Executable (*.exe)|*.exe", false, files =>
            {
                FileInfo fi = new FileInfo(files[0]);
                string profile = Path.GetFileNameWithoutExtension(fi.Name);

                // try to load game profile
                if (!ProfilesLibrary.HasProfile(profile))
                {
                    Dialogs.Message("There was an error when trying to load game using specified profile.", "Frosty Core");
                    return;
                }

                // make sure config doesnt already exist
                Entry existing = entries.Find(e => e.Profile == profile);
                if (existing != null)
                {
                    Dialogs.Message(existing.GameName + " already has a profile.", "Frosty Core");
                    return;
                }

                Config.AddGame(profile, fi.DirectoryName);
                Config.Save();
                Refresh();
            });
        }

        private void Scan()
        {
            scanning = true;
            List<KeyValuePair<string, string>> found = new List<KeyValuePair<string, string>>();

            Dialogs.RunTask("Scanning", "Looking for installed games...", task =>
            {
                using (RegistryKey lmKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\WOW6432Node"))
                {
                    if (lmKey != null)
                        IterateSubKeys(lmKey, found);
                }
            },
            onDone: ctx =>
            {
                scanning = false;

                if (ctx.Exception != null)
                {
                    Dialogs.Message("An error occurred while scanning for games.\n\nPlease manually set the game executable(s).", "Frosty Core");
                    return;
                }

                foreach (KeyValuePair<string, string> game in found)
                {
                    if (!Config.GameProfiles.Contains(game.Key))
                        Config.AddGame(game.Key, game.Value);
                }

                Config.Save();
                Refresh();
            });
        }

        private static void IterateSubKeys(RegistryKey key, List<KeyValuePair<string, string>> found)
        {
            foreach (string subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using (RegistryKey sub = key.OpenSubKey(subKeyName))
                    {
                        if (sub != null)
                            IterateSubKeys(sub, found);
                    }
                }
                catch (Exception e) when (e is System.Security.SecurityException || e is UnauthorizedAccessException || e is IOException)
                {
                    // protected key: skip it
                }
            }

            foreach (string valueName in key.GetValueNames())
            {
                if (valueName.IndexOf("Install Dir", StringComparison.OrdinalIgnoreCase) == -1)
                    continue;

                string installDir = key.GetValue("Install Dir") as string;
                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir))
                    continue;

                foreach (string filename in Directory.EnumerateFiles(installDir, "*.exe"))
                {
                    string profile = Path.GetFileNameWithoutExtension(filename);

                    // original aborted the whole key on the first duplicate; skipping just that exe is what was meant
                    if (ProfilesLibrary.HasProfile(profile) && !found.Exists(f => f.Key == profile))
                        found.Add(new KeyValuePair<string, string>(profile, new FileInfo(filename).DirectoryName));
                }
            }
        }
    }
}
