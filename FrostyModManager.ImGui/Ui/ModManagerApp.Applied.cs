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
        private readonly MultiSelect<AppliedMod> appliedSelection = new MultiSelect<AppliedMod>();

        private void DrawAppliedMods()
        {
            Pack pack = model.SelectedPack;
            if (pack == null)
                return;

            DrawPackBar(pack);

            // ---- toolbar ----
            bool hasSelection = appliedSelection.Count > 0;
            ImGuiIOPtr io = ImGui.GetIO();

            ImGui.BeginDisabled(!hasSelection);
            if (ImGui.Button("Move Up"))
                MoveSelected(pack, up: true, io.KeyShift, io.KeyCtrl);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold Shift to move 4 places, Shift+Ctrl to move to the top");

            ImGui.SameLine();
            if (ImGui.Button("Move Down"))
                MoveSelected(pack, up: false, io.KeyShift, io.KeyCtrl);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Hold Shift to move 4 places, Shift+Ctrl to move to the bottom");

            ImGui.SameLine();
            if (ImGui.Button("Remove"))
                RemoveSelected(pack);
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.Button("Uncheck All"))
            {
                foreach (AppliedMod mod in pack.AppliedMods)
                    mod.IsEnabled = false;

                pack.Refresh();
            }

            // Delete key removes the selection, as it did in the list view
            if (hasSelection && ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows) && !ImGui.GetIO().WantTextInput &&
                ImGui.IsKeyPressed(ImGuiKey.Delete))
                RemoveSelected(pack);

            // ---- list ----
            float footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
            if (ImGui.BeginTable("##applied", 4, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV,
                new Vector2(0, -footer)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("On", ImGuiTableColumnFlags.WidthFixed, 34f);
                ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Author", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableSetupColumn("Version", ImGuiTableColumnFlags.WidthFixed, 70f);
                ImGui.TableHeadersRow();

                List<AppliedMod> ordered = pack.AppliedMods;

                for (int i = 0; i < ordered.Count; i++)
                    DrawAppliedRow(pack, ordered[i], i, ordered);

                ImGui.EndTable();
            }

            ImGui.BeginDisabled(pack.AppliedMods.Count == 0);
            if (ImGui.Button("Launch", new Vector2(120, 0)))
                Launch();
            ImGui.EndDisabled();
        }

        private void DrawAppliedRow(Pack pack, AppliedMod am, int index, List<AppliedMod> ordered)
        {
            const float iconSize = 16f;

            ImGui.PushID(index);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            float x0 = ImGui.GetCursorPosX();
            bool isSelected = appliedSelection.Contains(am);

            // the row selectable goes first and is allowed to be overlapped by the checkbox drawn on top of it
            if (ImGui.Selectable("##row", isSelected, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap, new Vector2(0, iconSize + 2)))
            {
                appliedSelection.Click(am, ordered);
                if (am.Mod != null)
                    detailMod = am.Mod;
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                appliedSelection.RightClick(am);

            if (ImGui.BeginPopupContextItem("##appliedctx"))
            {
                if (ImGui.MenuItem("Remove from pack"))
                    RemoveSelected(pack);

                ImGui.EndPopup();
            }

            ImGui.SameLine(x0 + 4f, 0f);

            ImGui.BeginDisabled(!am.IsFound);
            bool enabled = am.IsEnabled;
            if (ImGui.Checkbox("##enabled", ref enabled))
            {
                am.IsEnabled = enabled;
                pack.Refresh();
            }
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            if (am.IsFound)
            {
                ModIcon(am.Mod, iconSize);
                ImGui.SameLine(0, 6f);
                WarningBadge(am.Mod);
                ImGui.TextUnformatted(am.ModName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(am.Mod.ModDetails.Author ?? "");

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(am.Mod.ModDetails.Version ?? "");
            }
            else
            {
                // the mod is in the pack but its file is gone; keep it visible (and in the pack) so the user can remove it
                SmallImage("Images/Warning.png", iconSize);
                ImGui.SameLine(0, 6f);
                ImGui.TextColored(Theme.Error, am.ModName + " (not found)");
            }

            ImGui.PopID();
        }

        private void DrawPackBar(Pack pack)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Pack");
            ImGui.SameLine();

            ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - 70f));
            if (ImGui.BeginCombo("##pack", pack.Name))
            {
                foreach (Pack candidate in model.Packs.ToList())
                {
                    if (ImGui.Selectable(candidate.Name, candidate == pack) && candidate != pack)
                        SelectPack(candidate);
                }
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.Button("+##addpack"))
                AddPackPrompt();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Add pack");

            ImGui.SameLine();
            if (ImGui.Button("-##removepack"))
                RemoveSelectedPack();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Delete pack");
        }

        // ---- applied list actions ------------------------------------------------------------------

        private void MoveSelected(Pack pack, bool up, bool shift, bool ctrl)
        {
            // selection in list order so the moves keep the mods' relative order
            List<AppliedMod> selection = pack.AppliedMods.Where(appliedSelection.Contains).ToList();

            for (int i = 0; i < (shift ? 4 : 1); i++)
            {
                if (up)
                    pack.MoveModsUp(selection);
                else
                    pack.MoveModsDown(selection);
            }

            if (shift && ctrl)
            {
                if (up)
                    pack.MoveModsTop(selection);
                else
                    pack.MoveModsBottom(selection);
            }
        }

        private void RemoveSelected(Pack pack)
        {
            foreach (AppliedMod mod in pack.AppliedMods.Where(appliedSelection.Contains).ToList())
                pack.RemoveMod(mod);

            appliedSelection.Clear();
        }

        // ---- pack management -----------------------------------------------------------------------

        private string PackNameError(string name) =>
            model.Packs.Exists(p => p.Name.CompareTo(name) == 0) ? "A pack with the same name already exists" : null;

        private void AddPackPrompt()
        {
            Dialogs.Prompt("Add Pack", "Pack name:", "", name =>
            {
                Pack pack = model.AddPack(name, out string error);
                if (pack == null)
                    Dialogs.Message(error, "Frosty Mod Manager");
                else
                    SelectPack(pack);
            }, PackNameError);
        }

        private void RemoveSelectedPack()
        {
            Pack pack = model.SelectedPack;
            if (pack == null)
                return;

            if (model.Packs.Count == 1)
            {
                Dialogs.Message("There must be at least one active pack", "Frosty Mod Manager");
                return;
            }

            Dialogs.Message("Are you sure you want to delete this pack?", "Frosty Mod Manager", DialogButtons.YesNo, result =>
            {
                if (result != DialogResult.Yes)
                    return;

                model.RemovePack(pack);
                SelectPack(model.Packs[0]);
            });
        }

        private void RenameSelectedPack()
        {
            Pack oldPack = model.SelectedPack;
            if (oldPack == null)
                return;

            Dialogs.Prompt("Rename Pack", "New pack name:", oldPack.Name, name =>
            {
                Pack renamed = model.RenamePack(oldPack, name, out string error);
                if (renamed == null)
                    Dialogs.Message(error, "Frosty Mod Manager");
                else
                    SelectPack(renamed);
            }, PackNameError);
        }

        private void DuplicateSelectedPack()
        {
            Pack oldPack = model.SelectedPack;
            if (oldPack == null)
                return;

            Dialogs.Prompt("Duplicate Pack", "Name of the new pack:", oldPack.Name + " (copy)", name =>
            {
                Pack copy = model.DuplicatePack(oldPack, name, out string error);
                if (copy == null)
                    Dialogs.Message(error, "Frosty Mod Manager");
                else
                    SelectPack(copy);
            }, PackNameError);
        }

        private void ExportSelectedPack()
        {
            if (model.SelectedPack == null)
                return;

            Dialogs.SaveFile("Export Pack", "Frosty Pack (*.fbpack)|*.fbpack", "fbpack", path =>
                Dialogs.RunTask("Exporting Pack", "Writing " + System.IO.Path.GetFileName(path), task => model.ExportPack(path)));
        }

        private void ImportPack()
        {
            Dialogs.OpenFile("Import Pack", "Frosty Pack (*.fbpack)|*.fbpack", false, InstallFiles);
        }
    }
}
