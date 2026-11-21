using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Frosty.Core.Mod;
using Frosty.ImGuiKit;
using FrostyModManager.Gui.Model;
using ImGuiNET;

namespace FrostyModManager.Gui.Ui
{
    internal sealed partial class ModManagerApp
    {
        // ---- shared ----------------------------------------------------------------------------------

        /// <summary>
        /// Draws only the rows that are on screen. These tables can hold tens of thousands of rows
        /// (every resource a pack touches), which a plain loop would draw in full every frame.
        /// </summary>
        private static unsafe void Clip(int count, Action<int> drawRow)
        {
            ImGuiListClipperPtr clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
            clipper.Begin(count);

            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    drawRow(i);
            }

            clipper.End();
            clipper.Destroy();
        }

        // ---- description tab -------------------------------------------------------------------------

        // reading a mod's resource list can take a while for big mods, so it runs once in the background and is cached
        private readonly ConcurrentDictionary<IFrostyMod, Task<List<AffectedFileInfo>>> affectedCache =
            new ConcurrentDictionary<IFrostyMod, Task<List<AffectedFileInfo>>>();

        private string affectedFilter = "";
        private IFrostyMod affectedFilteredFor;
        private string affectedFilteredBy;
        private List<AffectedFileInfo> affectedFiltered = new List<AffectedFileInfo>();

        private void DrawDescription()
        {
            IFrostyMod mod = detailMod;
            if (mod == null)
            {
                ImGui.TextDisabled("Select a mod to see its details.");
                return;
            }

            FrostyModDetails details = mod.ModDetails;

            if (!ImGui.BeginChild("##descscroll", Vector2.Zero))
            {
                ImGui.EndChild();
                return;
            }

            ModIcon(mod, 64f);
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextUnformatted(details.Title);
            ImGui.TextDisabled("by " + (details.Author ?? ""));
            ImGui.TextUnformatted("Version " + (details.Version ?? ""));
            ImGui.TextUnformatted("Category: " + details.Category);

            if (!string.IsNullOrWhiteSpace(details.Link) && ImGui.SmallButton("Open mod page"))
                Program.OpenUrl(details.Link);

            ImGui.EndGroup();

            if (mod.HasWarnings)
            {
                ImGui.Spacing();
                foreach (string warning in mod.Warnings)
                    ImGui.TextColored(Theme.Warning, "Warning: " + warning);
            }

            ImGui.Separator();

            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(details.Description ?? "");
            ImGui.PopTextWrapPos();

            DrawScreenshots(details);

            ImGui.Spacing();
            if (ImGui.CollapsingHeader("Affected files"))
                DrawAffectedFiles(mod);

            ImGui.EndChild();
        }

        private void DrawScreenshots(FrostyModDetails details)
        {
            if (details.ScreenshotData.Count == 0)
                return;

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            for (int i = 0; i < details.ScreenshotData.Count; i++)
            {
                byte[] data = details.ScreenshotData[i];
                ImageHandle image = host.Textures.FromBytes(data, data);
                if (!image.IsValid)
                    continue;

                if (i > 0)
                    ImGui.SameLine();

                ImGui.PushID(i);
                TextureCache.DrawFitted(image, new Vector2(160, 100));
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    ImGui.SetTooltip("Click to enlarge");
                }
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                    Dialogs.Show(new ScreenshotDialog(image));
                ImGui.PopID();
            }
        }

        private void DrawAffectedFiles(IFrostyMod mod)
        {
            Task<List<AffectedFileInfo>> task = affectedCache.GetOrAdd(mod, m => Task.Run(() => ModManagerModel.GetAffectedFiles(m)));

            if (!task.IsCompleted)
            {
                ImGui.TextDisabled("Reading mod...");
                return;
            }

            if (task.IsFaulted)
            {
                ImGui.TextColored(Theme.Error, "Couldn't read this mod's files.");
                return;
            }

            List<AffectedFileInfo> files = task.Result;

            ImGui.SetNextItemWidth(260f);
            ImGui.InputTextWithHint("##affectedfilter", "Filter", ref affectedFilter, 128);
            ImGui.SameLine();
            ImGui.TextDisabled(files.Count + " files");

            // re-filter only when the mod or the filter text changed
            if (affectedFilteredFor != mod || affectedFilteredBy != affectedFilter)
            {
                string needle = affectedFilter.Trim();
                affectedFiltered = needle.Length == 0
                    ? files
                    : files.Where(f => Contains(f.Name, needle) || Contains(f.Type, needle)).ToList();

                affectedFilteredFor = mod;
                affectedFilteredBy = affectedFilter;
            }

            List<AffectedFileInfo> rows = affectedFiltered;

            if (ImGui.BeginTable("##affected", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
                new Vector2(0, Math.Min(360f, Math.Max(60f, rows.Count * ImGui.GetTextLineHeightWithSpacing() + 30f)))))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 60f);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableHeadersRow();

                Clip(rows.Count, i =>
                {
                    AffectedFileInfo file = rows[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(file.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(file.Type);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(file.IsAdded && file.IsModified ? "Added, Modified" : file.IsAdded ? "Added" : file.IsModified ? "Modified" : "");
                });

                ImGui.EndTable();
            }
        }

        // ---- actions tab -----------------------------------------------------------------------------

        private ConflictsData conflicts;
        private bool conflictsStale = true;
        private bool conflictsRunning;
        private bool onlyShowReplacements;
        private string actionsFilter = "";
        private ConflictsData actionsFilteredFor;
        private string actionsFilteredBy;
        private List<ModResourceInfo> actionsFiltered = new List<ModResourceInfo>();

        private void DrawActions()
        {
            Pack pack = model.SelectedPack;
            if (pack == null)
                return;

            if (ImGui.Checkbox("Only show replacements", ref onlyShowReplacements))
                conflictsStale = true;

            ImGui.SameLine();
            if (ImGui.Button("Refresh"))
                conflictsStale = true;

            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##actionsfilter", "Filter by resource name", ref actionsFilter, 128);

            if (conflictsStale && !conflictsRunning)
                RecalculateConflicts();

            ConflictsData data = conflicts;
            if (data == null)
            {
                ImGui.TextDisabled("Calculating...");
                return;
            }

            if (data.Resources.Count == 0)
            {
                ImGui.TextDisabled(onlyShowReplacements ? "No resources are replaced by more than one mod." : "The applied mods don't change any resources.");
                return;
            }

            if (actionsFilteredFor != data || actionsFilteredBy != actionsFilter)
            {
                string needle = actionsFilter.Trim();
                actionsFiltered = needle.Length == 0
                    ? data.Resources
                    : data.Resources.Where(r => Contains(r.Name, needle)).ToList();

                actionsFilteredFor = data;
                actionsFilteredBy = actionsFilter;
            }

            List<ModResourceInfo> rows = actionsFiltered;
            List<AppliedMod> columns = data.Columns;

            const ImGuiTableFlags flags = ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg |
                                          ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV;

            if (!ImGui.BeginTable("##actions", 2 + columns.Count, flags))
                return;

            ImGui.TableSetupScrollFreeze(2, 1);
            ImGui.TableSetupColumn("Resource", ImGuiTableColumnFlags.WidthFixed, 300f);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 46f);
            foreach (AppliedMod column in columns)
                ImGui.TableSetupColumn(column.ModName + "##" + (column.Mod?.Filename ?? column.BackupFileName), ImGuiTableColumnFlags.WidthFixed, 96f);
            ImGui.TableHeadersRow();

            Clip(rows.Count, i =>
            {
                ModResourceInfo info = rows[i];

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(info.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(info.Type.ToUpperInvariant());

                foreach (AppliedMod column in columns)
                {
                    ImGui.TableNextColumn();
                    DrawActionCell(info, column);
                }
            });

            ImGui.EndTable();
        }

        private void DrawActionCell(ModResourceInfo info, AppliedMod column)
        {
            string filename = ActionFilename(info, column);
            if (filename == null)
                return;

            string primary = info.PrimaryImage(filename);
            if (primary != null)
            {
                SmallImage("Images/" + primary, 14f);
                string tip = info.PrimaryTooltip(filename);
                if (tip != null && ImGui.IsItemHovered())
                    ImGui.SetTooltip(tip);
            }

            string secondary = info.SecondaryImage(filename);
            if (secondary != null)
            {
                if (primary != null)
                    ImGui.SameLine(0, 4f);

                SmallImage("Images/" + secondary, 14f);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(info.SecondaryTooltip(filename));
            }
        }

        /// <summary>The file name under which this applied mod's actions on the resource were recorded (a collection's actions are recorded per contained mod).</summary>
        private static string ActionFilename(ModResourceInfo info, AppliedMod column)
        {
            if (column.Mod is FrostyModCollection collection)
            {
                FrostyMod child = collection.Mods.Find(m => info.ActionFor(m.Filename) != null);
                return child?.Filename;
            }

            string filename = column.Mod != null ? column.Mod.Filename : column.BackupFileName;
            return info.ActionFor(filename) != null ? filename : null;
        }

        private void RecalculateConflicts()
        {
            conflictsRunning = true;
            conflictsStale = false;

            bool onlyReplacements = onlyShowReplacements;
            ConflictsData result = null;

            Dialogs.RunTask("Calculating Actions", "", task => result = model.BuildConflicts(onlyReplacements, task), cancellable: true, onDone: ctx =>
            {
                conflictsRunning = false;

                if (ctx.Exception != null)
                    Dialogs.Message("Couldn't calculate the actions for this pack:\n" + ctx.Exception.Message, "Frosty Mod Manager");
                else if (result != null && !result.Cancelled)
                    conflicts = result;
            });
        }
    }
}
