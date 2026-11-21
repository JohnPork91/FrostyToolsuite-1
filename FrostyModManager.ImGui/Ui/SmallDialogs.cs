using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Frosty.ImGuiKit;
using FrostyModManager.Gui.Model;
using ImGuiNET;

namespace FrostyModManager.Gui.Ui
{
    /// <summary>Lists problems found while installing mods. Replaces InstallErrorsWindow.</summary>
    internal sealed class InstallErrorsDialog : Dialog
    {
        private readonly List<ImportErrorInfo> errors;
        private readonly ImGuiHost host;

        protected override Vector2? FixedSize => new Vector2(620, 320);
        protected override ImGuiWindowFlags Flags => ImGuiWindowFlags.NoSavedSettings;

        public InstallErrorsDialog(ImGuiHost host, List<ImportErrorInfo> errors)
        {
            Title = "Install Errors";
            this.host = host;
            this.errors = errors;
        }

        protected override void DrawContents()
        {
            ImGui.TextUnformatted("Some files could not be installed, or were installed with warnings:");
            ImGui.Spacing();

            float footer = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
            if (ImGui.BeginTable("##errors", 3, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable,
                new Vector2(0, -footer)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 22);
                ImGui.TableSetupColumn("File", ImGuiTableColumnFlags.WidthFixed, 180);
                ImGui.TableSetupColumn("Problem", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                foreach (ImportErrorInfo error in errors)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImageHandle icon = host.Textures.FromFile(error.IsWarning ? "Images/ModImportWarning.png" : "Images/Cross.png");
                    if (icon.IsValid)
                        ImGui.Image(icon.Id, new Vector2(14, 14));

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(error.Filename ?? "");

                    ImGui.TableNextColumn();
                    ImGui.PushStyleColor(ImGuiCol.Text, error.IsWarning ? Theme.Warning : Theme.Error);
                    ImGui.TextWrapped(error.Error ?? "");
                    ImGui.PopStyleColor();
                }

                ImGui.EndTable();
            }

            if (ImGui.Button("OK", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                Close();
        }
    }

    /// <summary>Replaces AboutWindow.</summary>
    internal sealed class AboutDialog : Dialog
    {
        private readonly string credits;

        protected override Vector2? FixedSize => new Vector2(480, 440);
        protected override ImGuiWindowFlags Flags => ImGuiWindowFlags.NoSavedSettings;

        public AboutDialog()
        {
            Title = "About Frosty Mod Manager";

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
            ImGui.TextUnformatted("Frosty Mod Manager");
            ImGui.TextDisabled(Frosty.Core.App.Version);
            ImGui.Spacing();
            ImGui.TextUnformatted("Copyright 2017-2020 derwangler, Cade and benji");
            ImGui.TextUnformatted("Originally created by GalaxyMan2015");
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

            ImGui.SameLine();
            if (ImGui.SmallButton("Support: discord.gg/sB8ZUAT"))
                Program.OpenUrl("https://discord.gg/sB8ZUAT");

            if (ImGui.Button("OK", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                Close();
        }
    }

    /// <summary>Full-size screenshot viewer (the WPF version opened a window per screenshot).</summary>
    internal sealed class ScreenshotDialog : Dialog
    {
        private readonly ImageHandle image;

        protected override ImGuiWindowFlags Flags => ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings;

        public ScreenshotDialog(ImageHandle image)
        {
            Title = "Screenshot";
            this.image = image;
        }

        protected override void DrawContents()
        {
            Vector2 max = ImGui.GetMainViewport().Size * 0.8f;
            TextureCache.DrawFitted(image, max);

            if (ImGui.Button("Close", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                Close();
        }
    }
}
