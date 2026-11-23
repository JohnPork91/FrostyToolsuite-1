using System;
using System.Collections.Generic;
using System.Linq;
using Frosty.ImGuiKit;
using FrostySdk.Managers.Entries;
using ImGuiNET;

namespace FrostyEditor.Gui.Ui
{
    /// <summary>
    /// The data explorer: replaces FrostyDataExplorer (a WPF tree + list). Folders come from each asset's path;
    /// typing in the filter box switches to a flat list of matching asset names.
    /// </summary>
    internal sealed class AssetExplorer
    {
        private sealed class Folder
        {
            public string Name;
            public string FullPath;
            public readonly List<Folder> Folders = new List<Folder>();
            public readonly List<EbxAssetEntry> Assets = new List<EbxAssetEntry>();
        }

        private Folder root = new Folder { Name = "", FullPath = "" };
        private List<EbxAssetEntry> all = new List<EbxAssetEntry>();
        private List<EbxAssetEntry> flat = new List<EbxAssetEntry>();

        private string filter = "";
        private string builtFilter = "";
        private bool onlyModified;
        private bool needsRebuild = true;

        public EbxAssetEntry Selected { get; private set; }

        public Action<EbxAssetEntry> OpenRequested;
        public Action<EbxAssetEntry> RevertRequested;
        public Action<EbxAssetEntry> ImportRequested;
        public Action<EbxAssetEntry> ExportRequested;

        /// <summary>Rebuild the list on the next frame (after projects load, assets revert or modified-only changes).</summary>
        public void Refresh() => needsRebuild = true;

        public void Reset()
        {
            Selected = null;
            filter = "";
            builtFilter = "";
            onlyModified = false;
            needsRebuild = true;
        }

        public void SelectAsset(EbxAssetEntry entry) => Selected = entry;

        private void Rebuild()
        {
            needsRebuild = false;

            all = Frosty.Core.App.AssetManager == null
                ? new List<EbxAssetEntry>()
                : Frosty.Core.App.AssetManager.EnumerateEbx("", onlyModified).ToList();

            root = new Folder { Name = "", FullPath = "" };
            Dictionary<string, Folder> folders = new Dictionary<string, Folder> { { "", root } };

            foreach (EbxAssetEntry entry in all)
            {
                string path = (entry.Path ?? "").Trim('/');
                Folder folder = GetFolder(folders, path);
                folder.Assets.Add(entry);
            }

            SortFolder(root);
            RebuildFlat();
        }

        private static Folder GetFolder(Dictionary<string, Folder> folders, string path)
        {
            if (folders.TryGetValue(path, out Folder existing))
                return existing;

            int slash = path.LastIndexOf('/');
            string parentPath = slash < 0 ? "" : path.Substring(0, slash);
            string name = slash < 0 ? path : path.Substring(slash + 1);

            Folder parent = GetFolder(folders, parentPath);
            Folder folder = new Folder { Name = name, FullPath = path };
            parent.Folders.Add(folder);
            folders[path] = folder;
            return folder;
        }

        private static void SortFolder(Folder folder)
        {
            folder.Folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            folder.Assets.Sort((a, b) => string.Compare(a.Filename, b.Filename, StringComparison.OrdinalIgnoreCase));

            foreach (Folder child in folder.Folders)
                SortFolder(child);
        }

        private void RebuildFlat()
        {
            builtFilter = filter;
            string needle = filter.Trim();

            flat = needle.Length == 0
                ? new List<EbxAssetEntry>()
                : all.Where(e => e.Name != null && e.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                     .ToList();
        }

        // ---- drawing -------------------------------------------------------------------------------

        public void Draw()
        {
            if (needsRebuild)
                Rebuild();

            ImGui.SetNextItemWidth(Math.Max(80f, ImGui.GetContentRegionAvail().X - 120f));
            ImGui.InputTextWithHint("##explorerfilter", "Filter by asset name", ref filter, 256);

            ImGui.SameLine();
            if (ImGui.Checkbox("Modified", ref onlyModified))
                Rebuild();

            if (filter != builtFilter)
                RebuildFlat();

            bool filtering = filter.Trim().Length != 0;
            ImGui.TextDisabled(filtering ? flat.Count + " of " + all.Count + " assets" : all.Count + " assets");

            if (!ImGui.BeginChild("##explorerlist", System.Numerics.Vector2.Zero))
            {
                ImGui.EndChild();
                return;
            }

            if (filtering)
            {
                ListClipper.Run(flat.Count, i => DrawAsset(flat[i], flat[i].Name));
            }
            else
            {
                DrawFolder(root);
            }

            ImGui.EndChild();
        }

        private void DrawFolder(Folder folder)
        {
            foreach (Folder child in folder.Folders)
            {
                bool open = ImGui.TreeNodeEx(child.Name + "##" + child.FullPath, ImGuiTreeNodeFlags.SpanAvailWidth);
                if (!open)
                    continue;

                DrawFolder(child);
                ImGui.TreePop();
            }

            List<EbxAssetEntry> assets = folder.Assets;
            ListClipper.Run(assets.Count, i => DrawAsset(assets[i], assets[i].DisplayName));
        }

        private void DrawAsset(EbxAssetEntry entry, string label)
        {
            bool modified = entry.IsModified;

            ImGui.PushID(entry.Name);
            ImGui.PushStyleColor(ImGuiCol.Text, modified ? Theme.Warning : Theme.FontColor);

            bool clicked = ImGui.Selectable((entry.IsAdded ? "+ " : "") + label, Selected == entry,
                ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.SpanAllColumns);

            ImGui.PopStyleColor();

            if (clicked)
            {
                Selected = entry;
                Frosty.Core.App.SelectedAsset = entry;

                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    OpenRequested?.Invoke(entry);
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                Selected = entry;
                Frosty.Core.App.SelectedAsset = entry;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(entry.Name + "\n" + entry.Type);

            if (ImGui.BeginPopupContextItem("##assetctx"))
            {
                if (ImGui.MenuItem("Open"))
                    OpenRequested?.Invoke(entry);

                if (ImGui.MenuItem("Revert", null, false, modified))
                    RevertRequested?.Invoke(entry);

                ImGui.Separator();

                if (ImGui.MenuItem("Import..."))
                    ImportRequested?.Invoke(entry);

                if (ImGui.MenuItem("Export..."))
                    ExportRequested?.Invoke(entry);

                ImGui.Separator();

                if (ImGui.MenuItem("Copy name"))
                    ImGui.SetClipboardText(entry.Name);

                ImGui.EndPopup();
            }

            ImGui.PopID();
        }
    }
}
