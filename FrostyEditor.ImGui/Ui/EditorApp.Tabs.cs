using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Frosty.Core;
using Frosty.ImGuiKit;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using ImGuiNET;

namespace FrostyEditor.Gui.Ui
{
    internal sealed partial class EditorApp
    {
        /// <summary>One open asset (replaces FrostyTabItem + FrostyAssetEditor for the generic editor).</summary>
        private sealed class AssetTab
        {
            public EbxAssetEntry Entry;
            public EbxAsset Asset;
            public PropertyEditor Editor;
            public int RootIndex;

            /// <summary>When the first not-yet-saved edit happened; edits are pushed into the asset manager once the user stops typing.</summary>
            public double PendingSince = -1;
            public bool Open = true;
        }

        private readonly List<AssetTab> tabs = new List<AssetTab>();
        private AssetTab tabToSelect;

        // ---- opening / closing ---------------------------------------------------------------------

        private void OpenAsset(EbxAssetEntry entry)
        {
            if (entry == null || entry.Type == "EncryptedAsset")
                return;

            AssetTab existing = tabs.Find(t => t.Entry.Name == entry.Name);
            if (existing != null)
            {
                tabToSelect = existing;
                return;
            }

            if (entry.IsAdded && !entry.HasModifiedData)
            {
                Dialogs.Message("Unable to open asset", AppTitle);
                return;
            }

            EbxAsset asset = null;

            Dialogs.RunTask("Opening Asset", entry.Name, task => asset = Frosty.Core.App.AssetManager.GetEbx(entry), onDone: ctx =>
            {
                if (ctx.Exception != null || asset == null || !asset.IsValid)
                {
                    Dialogs.Message("Unable to open asset", AppTitle);
                    return;
                }

                AssetTab tab = new AssetTab
                {
                    Entry = entry,
                    Asset = asset,
                    Editor = new PropertyEditor(OpenAsset)
                };

                tabs.Add(tab);
                tabToSelect = tab;
            });
        }

        private void CloseTab(AssetTab tab)
        {
            FlushPendingEdits(tab);
            tabs.Remove(tab);
            explorer.Refresh();
        }

        private void CloseAllTabs()
        {
            FlushPendingEdits();
            tabs.Clear();
            explorer.Refresh();
        }

        private AssetTab currentTab;

        private void CloseCurrentTab()
        {
            if (currentTab != null)
                CloseTab(currentTab);
        }

        // ---- writing edits back --------------------------------------------------------------------

        /// <summary>
        /// Pushes edited objects into the asset manager (asset.Update + ModifyEbx, exactly what the WPF editor did when its
        /// AssetModified flag flipped). Done once the user stops editing so typing doesn't re-serialize the asset per keystroke.
        /// </summary>
        private void ApplyPendingEdits(double now)
        {
            bool typing = ImGui.IsAnyItemActive();

            foreach (AssetTab tab in tabs.ToList())
            {
                if (tab.Editor.Changed && tab.PendingSince < 0)
                    tab.PendingSince = now;

                if (tab.PendingSince >= 0 && (!typing || now - tab.PendingSince > 1.5))
                    FlushPendingEdits(tab);
            }
        }

        private void FlushPendingEdits()
        {
            foreach (AssetTab tab in tabs.ToList())
                FlushPendingEdits(tab);
        }

        private void FlushPendingEdits(AssetTab tab)
        {
            if (!tab.Editor.Changed && tab.PendingSince < 0)
                return;

            tab.Editor.Changed = false;
            tab.PendingSince = -1;

            try
            {
                tab.Asset.Update();
                Frosty.Core.App.AssetManager.ModifyEbx(tab.Entry.Name, tab.Asset);
            }
            catch (Exception e)
            {
                Frosty.Core.App.Logger.LogError("Couldn't apply changes to {0}: {1}", tab.Entry.Name, e.Message);
                Dialogs.Message("Couldn't apply your changes to " + tab.Entry.Name + ":\n" + e.Message, AppTitle);
                return;
            }

            explorer.Refresh();
        }

        // ---- drawing -------------------------------------------------------------------------------

        private void DrawTabs()
        {
            if (tabs.Count == 0)
            {
                currentTab = null;
                ImGui.Spacing();
                ImGui.TextDisabled("Double-click an asset in the data explorer to open it.");
                return;
            }

            if (!ImGui.BeginTabBar("##assettabs", ImGuiTabBarFlags.AutoSelectNewTabs | ImGuiTabBarFlags.FittingPolicyScroll | ImGuiTabBarFlags.Reorderable))
                return;

            AssetTab toClose = null;
            AssetTab drawn = null;

            foreach (AssetTab tab in tabs)
            {
                ImGuiTabItemFlags flags = ImGuiTabItemFlags.None;
                if (tab == tabToSelect)
                {
                    flags |= ImGuiTabItemFlags.SetSelected;
                    tabToSelect = null;
                }

                bool open = true;
                string label = tab.Entry.DisplayName + "###" + tab.Entry.Name;

                if (ImGui.BeginTabItem(label, ref open, flags))
                {
                    drawn = tab;
                    DrawAssetTab(tab);
                    ImGui.EndTabItem();
                }

                if (!open)
                    toClose = tab;
            }

            ImGui.EndTabBar();

            currentTab = drawn;
            if (toClose != null)
                CloseTab(toClose);
        }

        private void DrawAssetTab(AssetTab tab)
        {
            List<object> roots = tab.Asset.RootObjects.ToList();

            ImGui.TextDisabled(tab.Entry.Name + "   |   " + tab.Entry.Type);

            AssetDefinition definition = Frosty.Core.App.PluginManager.GetAssetDefinition(tab.Entry.Type);
            if (definition != null && definition.GetType() != typeof(AssetDefinition))
            {
                ImGui.TextColored(Theme.Warning,
                    "This asset type has a dedicated editor in the WPF Frosty Editor, which hasn't been ported. Showing the raw properties.");
            }

            if (roots.Count > 1)
            {
                tab.RootIndex = Math.Clamp(tab.RootIndex, 0, roots.Count - 1);

                ImGui.SetNextItemWidth(320);
                if (ImGui.BeginCombo("Root object", RootName(roots[tab.RootIndex], tab.RootIndex)))
                {
                    for (int i = 0; i < roots.Count; i++)
                    {
                        if (ImGui.Selectable(RootName(roots[i], i), i == tab.RootIndex))
                            tab.RootIndex = i;
                    }

                    ImGui.EndCombo();
                }
            }

            ImGui.Separator();

            if (ImGui.BeginChild("##props", Vector2.Zero))
            {
                object root = roots.Count > 0 ? roots[Math.Clamp(tab.RootIndex, 0, roots.Count - 1)] : null;
                tab.Editor.DrawObject(root, !ProfilesLibrary.EnableExecution);
            }
            ImGui.EndChild();
        }

        private static string RootName(object root, int index) => index + ": " + root.GetType().Name;

        // ---- explorer actions ----------------------------------------------------------------------

        private void RevertAsset(EbxAssetEntry entry)
        {
            if (entry == null || !entry.IsModified)
                return;

            AssetTab open = tabs.Find(t => t.Entry.Name == entry.Name);
            if (open != null)
                tabs.Remove(open);

            Dialogs.RunTask("Reverting Asset", entry.Name,
                task => Frosty.Core.App.AssetManager.RevertAsset(entry, suppressOnModify: false),
                onDone: ctx => explorer.Refresh());
        }

        private void ImportAsset(EbxAssetEntry entry)
        {
            if (entry == null)
                return;

            AssetDefinition definition = Frosty.Core.App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();

            List<AssetImportType> filters = new List<AssetImportType>();
            definition.GetSupportedImportTypes(filters);

            if (filters.Count == 0)
            {
                Dialogs.Message("This asset type can't be imported.", AppTitle);
                return;
            }

            // several import types can share an extension (e.g. full and data-only .bin), so the type is chosen explicitly
            ChooseOne("Import Asset", "Import as:", filters.Select(f => f.Description + " (*." + f.Extension + ")").ToList(), index =>
            {
                AssetImportType filter = filters[index];

                Dialogs.OpenFile("Import Asset", filter.FilterString, false, files =>
                {
                    string path = files[0];
                    bool imported = false;

                    Dialogs.RunTask("Importing Asset", entry.Name, task => imported = definition.Import(entry, path, filter), onDone: ctx =>
                    {
                        if (ctx.Exception != null)
                        {
                            Dialogs.Message("Import failed:\n" + ctx.Exception.Message, AppTitle);
                            return;
                        }

                        if (imported)
                        {
                            Frosty.Core.App.Logger.Log("Imported {0} into {1}", path, entry.Name);

                            // an open tab would otherwise keep showing (and re-saving) the pre-import data
                            AssetTab open = tabs.Find(t => t.Entry.Name == entry.Name);
                            if (open != null)
                            {
                                tabs.Remove(open);
                                OpenAsset(entry);
                            }

                            explorer.Refresh();
                        }
                    });
                });
            });
        }

        private void ExportAsset(EbxAssetEntry entry)
        {
            if (entry == null)
                return;

            FlushPendingEdits();

            AssetDefinition definition = Frosty.Core.App.PluginManager.GetAssetDefinition(entry.Type) ?? new AssetDefinition();

            List<AssetExportType> filters = new List<AssetExportType>();
            definition.GetSupportedExportTypes(filters);

            if (filters.Count == 0)
            {
                Dialogs.Message("This asset type can't be exported.", AppTitle);
                return;
            }

            ChooseOne("Export Asset", "Export as:", filters.Select(f => f.Description + " (*." + f.Extension + ")").ToList(), index =>
            {
                AssetExportType filter = filters[index];

                Dialogs.SaveFile("Export Asset", filter.FilterString, filter.Extension, path =>
                {
                    bool exported = false;

                    Dialogs.RunTask("Exporting Asset", entry.Name, task => exported = definition.Export(entry, path, filter.Extension), onDone: ctx =>
                    {
                        if (ctx.Exception != null)
                            Dialogs.Message("Export failed:\n" + ctx.Exception.Message, AppTitle);
                        else if (exported)
                            Frosty.Core.App.Logger.Log("Exported {0} to {1}", entry.Name, path);
                    });
                });
            });
        }

        private static void ChooseOne(string title, string prompt, List<string> options, Action<int> chosen)
        {
            if (options.Count == 1)
            {
                chosen(0);
                return;
            }

            Dialogs.Show(new ChoiceDialog(title, prompt, options, chosen));
        }
    }

    /// <summary>Pick one entry from a short list.</summary>
    internal sealed class ChoiceDialog : Dialog
    {
        private readonly string prompt;
        private readonly List<string> options;
        private readonly Action<int> chosen;
        private int selected;

        protected override ImGuiWindowFlags Flags => ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings;

        public ChoiceDialog(string title, string prompt, List<string> options, Action<int> chosen)
        {
            Title = title;
            this.prompt = prompt;
            this.options = options;
            this.chosen = chosen;
        }

        protected override void DrawContents()
        {
            ImGui.TextUnformatted(prompt);
            ImGui.Spacing();

            for (int i = 0; i < options.Count; i++)
            {
                if (ImGui.RadioButton(options[i], selected == i))
                    selected = i;
            }

            ImGui.Spacing();
            if (ImGui.Button("OK", new Vector2(80, 0)))
            {
                Close();
                chosen(selected);
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                Close();
        }
    }
}
