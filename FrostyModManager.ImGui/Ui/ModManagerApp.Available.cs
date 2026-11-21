using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Frosty.Core.Mod;
using Frosty.ImGuiKit;
using FrostyModManager.Gui.Model;
using ImGuiNET;

namespace FrostyModManager.Gui.Ui
{
    internal sealed partial class ModManagerApp
    {
        private static readonly string[] FilterModes = { "All mods", "Applied", "Not applied" };

        private readonly MultiSelect<IFrostyMod> availableSelection = new MultiSelect<IFrostyMod>();
        private string availableFilter = "";
        private int availableFilterMode;
        private bool scrollToSelected;

        /// <summary>The mod shown on the Description tab: whichever list was clicked last.</summary>
        private IFrostyMod detailMod;

        private void DrawAvailableMods()
        {
            // the install worker thread adds to this list, so draw from a snapshot
            IFrostyMod[] all;
            lock (model.Sync)
                all = model.AvailableMods.ToArray();

            Pack pack = model.SelectedPack;
            HashSet<IFrostyMod> applied = new HashSet<IFrostyMod>();
            if (pack != null)
            {
                foreach (AppliedMod am in pack.AppliedMods)
                {
                    if (am.Mod != null)
                        applied.Add(am.Mod);
                }
            }

            availableSelection.Prune(all.Concat(all.OfType<FrostyModCollection>().SelectMany(c => c.Mods)).ToList());

            // ---- toolbar ----
            if (ImGui.Button("Install Mod..."))
            {
                Dialogs.OpenFile("Select Mod(s)",
                    "All Supported Files|*.fbmod;*.rar;*.zip;*.7z;*.fbpack;*.daimod|Frosty Mod (*.fbmod)|*.fbmod|Archives (*.rar;*.zip;*.7z)|*.rar;*.zip;*.7z|Frosty Pack (*.fbpack)|*.fbpack|DAI Mod (*.daimod)|*.daimod",
                    true, InstallFiles);
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(availableSelection.Count == 0 || pack == null);
            if (ImGui.Button("Add to Pack"))
                ApplySelected(all);
            ImGui.SameLine();
            if (ImGui.Button("Uninstall"))
                ConfirmUninstall();
            ImGui.EndDisabled();

            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 130f);
            ImGui.InputTextWithHint("##filter", "Filter by title, author or category", ref availableFilter, 128);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##filtermode", FilterModes[availableFilterMode]))
            {
                for (int i = 0; i < FilterModes.Length; i++)
                {
                    if (ImGui.Selectable(FilterModes[i], i == availableFilterMode))
                        availableFilterMode = i;
                }
                ImGui.EndCombo();
            }

            // ---- filter + group ----
            List<IFrostyMod> shown = all.Where(m => PassesFilter(m, applied)).ToList();
            List<IGrouping<string, IFrostyMod>> groups = shown
                .GroupBy(m => m.ModDetails.Category, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // flat order used for shift-click ranges
            List<IFrostyMod> ordered = new List<IFrostyMod>(shown.Count);
            foreach (IGrouping<string, IFrostyMod> group in groups)
                ordered.AddRange(group.OrderBy(m => m.ModDetails.Title, StringComparer.OrdinalIgnoreCase));

            statusText = shown.Count == all.Length
                ? all.Length + (all.Length == 1 ? " mod available" : " mods available")
                : shown.Count + " of " + all.Length + " mods shown";

            // ---- list ----
            if (!ImGui.BeginTable("##available", 4, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
                return;

            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("Applied", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableHeadersRow();

            foreach (IGrouping<string, IFrostyMod> group in groups)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                bool open = ImGui.TreeNodeEx(group.Key + " (" + group.Count() + ")##cat" + group.Key,
                    ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAllColumns | ImGuiTreeNodeFlags.Framed);

                if (!open)
                    continue;

                foreach (IFrostyMod mod in group.OrderBy(m => m.ModDetails.Title, StringComparer.OrdinalIgnoreCase))
                    DrawModRow(mod, ordered, applied, isChild: false);

                ImGui.TreePop();
            }

            ImGui.EndTable();
        }

        private bool PassesFilter(IFrostyMod mod, HashSet<IFrostyMod> applied)
        {
            bool isApplied = applied.Contains(mod) ||
                             (mod is FrostyModCollection collection && collection.Mods.Any(applied.Contains));

            if (availableFilterMode == 1 && !isApplied)
                return false;
            if (availableFilterMode == 2 && isApplied)
                return false;

            if (string.IsNullOrWhiteSpace(availableFilter))
                return true;

            string needle = availableFilter.Trim();
            return Matches(mod, needle) || (mod is FrostyModCollection c && c.Mods.Any(m => Matches(m, needle)));
        }

        private static bool Matches(IFrostyMod mod, string needle)
        {
            FrostyModDetails d = mod.ModDetails;
            return Contains(d.Title, needle) || Contains(d.Author, needle) || Contains(d.Category, needle);
        }

        private static bool Contains(string haystack, string needle) =>
            haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        private void DrawModRow(IFrostyMod mod, List<IFrostyMod> ordered, HashSet<IFrostyMod> applied, bool isChild)
        {
            FrostyModCollection collection = mod as FrostyModCollection;
            bool isSelected = availableSelection.Contains(mod);
            const float iconSize = 16f;

            ImGui.PushID(mod.Filename ?? mod.ModDetails.Title);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            float x0 = ImGui.GetCursorPosX();
            bool open = false;
            bool clicked;

            if (collection != null)
            {
                // collections expand to show the mods they contain
                ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAllColumns |
                                           ImGuiTreeNodeFlags.AllowOverlap | ImGuiTreeNodeFlags.DefaultOpen;
                if (isSelected)
                    flags |= ImGuiTreeNodeFlags.Selected;

                open = ImGui.TreeNodeEx("##node", flags);
                clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left) && !ImGui.IsItemToggledOpen();
                x0 += ImGui.GetTreeNodeToLabelSpacing();
            }
            else
            {
                clicked = ImGui.Selectable("##row", isSelected,
                    ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap | ImGuiSelectableFlags.AllowDoubleClick,
                    new Vector2(0, iconSize));
            }

            bool doubleClicked = clicked && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

            if (clicked)
            {
                if (isChild)
                {
                    // children aren't part of the flat range list, so shift-click degrades to a single select
                    availableSelection.Click(mod, new List<IFrostyMod> { mod });
                }
                else
                {
                    availableSelection.Click(mod, ordered);
                }

                detailMod = mod;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                availableSelection.RightClick(mod);
                detailMod = mod;
            }

            DrawModContextMenu(mod);

            if (scrollToSelected && availableSelection.Primary == mod)
            {
                ImGui.SetScrollHereY();
                scrollToSelected = false;
            }

            // title cell: icon, warning marker, name
            ImGui.SameLine(x0, 0f);
            ModIcon(mod, iconSize);
            ImGui.SameLine(0, 6f);
            WarningBadge(mod);
            ImGui.TextUnformatted(mod.ModDetails.Title);

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(mod.ModDetails.Author ?? "");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(mod.ModDetails.Version ?? "");

            ImGui.TableNextColumn();
            if (applied.Contains(mod))
                SmallImage("Images/Tick.png", 12f);

            if (collection != null && open)
            {
                foreach (FrostyMod child in collection.Mods)
                    DrawModRow(child, ordered, applied, isChild: true);

                ImGui.TreePop();
            }

            ImGui.PopID();

            if (doubleClicked && model.SelectedPack != null)
                model.SelectedPack.AddMod(mod);
        }

        private void DrawModContextMenu(IFrostyMod mod)
        {
            if (!ImGui.BeginPopupContextItem("##modctx"))
                return;

            if (ImGui.MenuItem("Add to pack", null, false, model.SelectedPack != null))
                ApplySelected(null);

            if (ImGui.MenuItem("Uninstall"))
                ConfirmUninstall();

            string link = mod.ModDetails.Link;
            if (!string.IsNullOrWhiteSpace(link) && ImGui.MenuItem("Open mod page"))
                Program.OpenUrl(link);

            ImGui.EndPopup();
        }

        /// <summary>Adds the selected mods to the current pack, in list order. Pass null to use the model's own list.</summary>
        private void ApplySelected(IFrostyMod[] all)
        {
            Pack pack = model.SelectedPack;
            if (pack == null)
                return;

            if (all == null)
            {
                lock (model.Sync)
                    all = model.AvailableMods.ToArray();
            }

            IEnumerable<IFrostyMod> everything = all.Concat(all.OfType<FrostyModCollection>().SelectMany(c => c.Mods));
            foreach (IFrostyMod mod in everything.Where(availableSelection.Contains).ToList())
                pack.AddMod(mod);
        }

        private void ConfirmUninstall()
        {
            List<IFrostyMod> targets = availableSelection.Items.ToList();
            if (targets.Count == 0)
                return;

            string message = targets.Count == 1
                ? "Are you sure you want to uninstall \"" + targets[0].ModDetails.Title + "\"?"
                : "Are you sure you want to uninstall these " + targets.Count + " mods?";

            Dialogs.Message(message, "Frosty Mod Manager", DialogButtons.YesNo, result =>
            {
                if (result != DialogResult.Yes)
                    return;

                model.UninstallMods(targets);

                availableSelection.Clear();
                appliedSelection.Clear();

                if (targets.Contains(detailMod))
                    detailMod = null;
            });
        }
    }
}
