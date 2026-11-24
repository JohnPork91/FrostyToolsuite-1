using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace Frosty.ImGuiKit
{
    /// <summary>
    /// Built-in open/save file browser. Replaces Microsoft.Win32.OpenFileDialog / SaveFileDialog
    /// and FrostySaveFileDialog, so the app has no WPF or WinForms dependency for file picking.
    /// Filters use the same "Description|*.ext;*.ext|Description|*.ext" syntax as the old dialogs.
    /// </summary>
    public sealed class FileDialog : Dialog
    {
        public enum DialogMode { Open, Save }

        private sealed class FilterEntry
        {
            public string Description;
            public string[] Patterns;
        }

        private sealed class Row
        {
            public string Name;
            public string FullPath;
            public bool IsDirectory;
            public long Length;
            public DateTime Modified;
        }

        private static string lastDirectory;

        private readonly DialogMode mode;
        private readonly bool multiselect;
        private readonly string defaultExtension;
        private readonly Action<string[]> onPicked;
        private readonly List<FilterEntry> filters = new List<FilterEntry>();
        private readonly List<Row> rows = new List<Row>();
        private readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private string currentDirectory;
        private string pathInput;
        private string fileName = "";
        private string error;
        private int filterIndex;
        private bool needsRefresh = true;
        private string lastClicked;

        protected override Vector2? FixedSize => new Vector2(640, 440);

        protected override ImGuiWindowFlags Flags => ImGuiWindowFlags.NoSavedSettings;

        public FileDialog(string title, string filter, DialogMode mode, bool multiselect, string defaultExtension, Action<string[]> onPicked)
        {
            Title = title;
            this.mode = mode;
            this.multiselect = multiselect && mode == DialogMode.Open;
            this.defaultExtension = defaultExtension;
            this.onPicked = onPicked;

            ParseFilters(filter);

            currentDirectory = Directory.Exists(lastDirectory) ? lastDirectory : Environment.CurrentDirectory;
            pathInput = currentDirectory;
        }

        private void ParseFilters(string filter)
        {
            if (!string.IsNullOrWhiteSpace(filter))
            {
                string[] parts = filter.Split('|');
                for (int i = 0; i + 1 < parts.Length; i += 2)
                {
                    filters.Add(new FilterEntry
                    {
                        Description = parts[i].Trim(),
                        Patterns = parts[i + 1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToArray()
                    });
                }
            }

            if (filters.Count == 0)
                filters.Add(new FilterEntry { Description = "All files", Patterns = new[] { "*.*" } });
        }

        private bool Matches(string name)
        {
            foreach (string pattern in filters[filterIndex].Patterns)
            {
                if (pattern == "*.*" || pattern == "*")
                    return true;

                // patterns are always of the form "*.ext"
                if (pattern.StartsWith("*.") && name.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private void Refresh()
        {
            rows.Clear();
            error = null;
            needsRefresh = false;

            try
            {
                DirectoryInfo di = new DirectoryInfo(currentDirectory);

                foreach (DirectoryInfo sub in di.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if ((sub.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                        continue;

                    rows.Add(new Row { Name = sub.Name, FullPath = sub.FullName, IsDirectory = true, Modified = sub.LastWriteTime });
                }

                foreach (FileInfo file in di.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || !Matches(file.Name))
                        continue;

                    rows.Add(new Row { Name = file.Name, FullPath = file.FullName, Length = file.Length, Modified = file.LastWriteTime });
                }
            }
            catch (Exception e)
            {
                error = e.Message;
            }
        }

        private void Navigate(string directory)
        {
            if (!Directory.Exists(directory))
            {
                error = "Folder not found: " + directory;
                return;
            }

            currentDirectory = Path.GetFullPath(directory);
            pathInput = currentDirectory;
            selected.Clear();
            lastClicked = null;
            needsRefresh = true;
        }

        protected override void DrawContents()
        {
            if (needsRefresh)
                Refresh();

            // --- location bar ---
            if (ImGui.Button("Up"))
            {
                DirectoryInfo parent = Directory.GetParent(currentDirectory);
                if (parent != null)
                    Navigate(parent.FullName);
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(70);
            string driveLabel = Path.GetPathRoot(currentDirectory) ?? "";
            if (ImGui.BeginCombo("##drive", driveLabel))
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady)
                        continue;

                    if (ImGui.Selectable(drive.Name, drive.Name == driveLabel))
                        Navigate(drive.RootDirectory.FullName);
                }
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##path", ref pathInput, 512, ImGuiInputTextFlags.EnterReturnsTrue))
                Navigate(pathInput);

            // --- listing ---
            float footer = ImGui.GetFrameHeightWithSpacing() * 3 + ImGui.GetStyle().ItemSpacing.Y;
            if (ImGui.BeginTable("##files", 3,
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV,
                new Vector2(0, -footer)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Modified", ImGuiTableColumnFlags.WidthFixed, 130);
                ImGui.TableHeadersRow();

                foreach (Row row in rows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    bool isSelected = selected.Contains(row.FullPath);
                    string label = (row.IsDirectory ? "[DIR]  " : "") + row.Name;

                    if (ImGui.Selectable(label + "##" + row.FullPath, isSelected,
                        ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick))
                    {
                        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                        {
                            if (row.IsDirectory)
                            {
                                Navigate(row.FullPath);
                                break; // rows list is rebuilt next frame
                            }

                            selected.Clear();
                            selected.Add(row.FullPath);
                            fileName = row.Name;
                            Accept();
                        }
                        else if (!row.IsDirectory)
                        {
                            bool ctrl = ImGui.GetIO().KeyCtrl;
                            if (!(multiselect && ctrl))
                                selected.Clear();

                            if (isSelected && multiselect && ctrl)
                                selected.Remove(row.FullPath);
                            else
                                selected.Add(row.FullPath);

                            lastClicked = row.Name;
                            fileName = multiselect ? string.Join(" ", selected.Select(p => Path.GetFileName(p))) : row.Name;
                        }
                    }

                    ImGui.TableNextColumn();
                    if (!row.IsDirectory)
                        ImGui.TextUnformatted(FormatSize(row.Length));

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Modified.ToString("yyyy-MM-dd HH:mm"));
                }

                ImGui.EndTable();
            }

            // --- footer ---
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("File name:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-250);
            ImGui.InputText("##name", ref fileName, 260, mode == DialogMode.Save ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.ReadOnly);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##filter", filters[filterIndex].Description))
            {
                for (int i = 0; i < filters.Count; i++)
                {
                    if (ImGui.Selectable(filters[i].Description, i == filterIndex))
                    {
                        filterIndex = i;
                        needsRefresh = true;
                    }
                }
                ImGui.EndCombo();
            }

            if (error != null)
                ImGui.TextColored(Theme.Error, error);
            else
                ImGui.NewLine();

            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 180 - ImGui.GetStyle().WindowPadding.X);
            if (ImGui.Button(mode == DialogMode.Open ? "Open" : "Save", new Vector2(80, 0)))
                Accept();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                Close();
        }

        private void Accept()
        {
            if (mode == DialogMode.Open)
            {
                if (selected.Count == 0)
                {
                    error = "Select a file first.";
                    return;
                }

                lastDirectory = currentDirectory;
                string[] picked = selected.ToArray();
                Close();
                onPicked(picked);
                return;
            }

            string name = fileName.Trim();
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                error = "Enter a valid file name.";
                return;
            }

            if (!string.IsNullOrEmpty(defaultExtension) && Path.GetExtension(name).Length == 0)
                name += "." + defaultExtension.TrimStart('.').ToLowerInvariant();

            lastDirectory = currentDirectory;
            string full = Path.Combine(currentDirectory, name);

            if (File.Exists(full))
            {
                // asked on top of this dialog; "No" leaves the browser open so another name can be chosen
                Dialogs.Message(name + " already exists.\nDo you want to replace it?", "Confirm Save", DialogButtons.YesNo, result =>
                {
                    if (result != DialogResult.Yes)
                        return;

                    Close();
                    onPicked(new[] { full });
                });
                return;
            }

            Close();
            onPicked(new[] { full });
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.##") + " GB";
        }
    }
}
