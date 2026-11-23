using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Frosty.Core.Mod;
using Frosty.ImGuiKit;
using ImGuiNET;

namespace FrostyEditor.Gui.Ui
{
    /// <summary>Mod title/author/category/icon/screenshots shown before exporting a mod. Replaces ModSettingsWindow.</summary>
    internal sealed class ModSettingsDialog : Dialog
    {
        private static readonly string[] Categories =
        {
            "Custom", "Audio", "Cosmetic", "Gameplay", "Graphic", "Map", "Miscellaneous", "User Interface", "Visuals"
        };

        private readonly ImGuiHost host;
        private readonly ModSettings settings;
        private readonly Action onAccepted;

        private string title, author, category, version, description;
        private int selectedCategory;
        private byte[] icon;
        private readonly byte[][] screenshots = new byte[4][];
        private string error;

        protected override Vector2? FixedSize => new Vector2(560, 620);
        protected override ImGuiWindowFlags Flags => ImGuiWindowFlags.NoSavedSettings;

        public ModSettingsDialog(ImGuiHost host, ModSettings settings, Action onAccepted)
        {
            Title = "Mod Settings";
            this.host = host;
            this.settings = settings;
            this.onAccepted = onAccepted;

            title = settings.Title ?? "";
            author = settings.Author ?? "";
            version = settings.Version ?? "";
            description = settings.Description ?? "";
            selectedCategory = Math.Clamp(settings.SelectedCategory, 0, Categories.Length - 1);
            category = selectedCategory == 0 ? settings.Category ?? "" : Categories[selectedCategory];
            icon = settings.Icon;

            for (int i = 0; i < screenshots.Length; i++)
                screenshots[i] = settings.GetScreenshot(i);
        }

        protected override void DrawContents()
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##title", "Title", ref title, 128);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##author", "Author", ref author, 128);

            ImGui.SetNextItemWidth(160);
            if (ImGui.BeginCombo("##category", Categories[selectedCategory]))
            {
                for (int i = 0; i < Categories.Length; i++)
                {
                    if (ImGui.Selectable(Categories[i], i == selectedCategory))
                    {
                        selectedCategory = i;
                        category = i == 0 ? "" : Categories[i];
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(selectedCategory != 0);
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##customcategory", "Custom category", ref category, 64);
            ImGui.EndDisabled();

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##version", "Version", ref version, 32);

            ImGui.InputTextMultiline("##description", ref description, 8192, new Vector2(-1, 120));

            ImGui.Spacing();
            ImGui.TextUnformatted("Icon (up to 128x128)");
            DrawImageSlot("icon", icon, 64f, bytes => icon = bytes, isIcon: true);

            ImGui.Spacing();
            ImGui.TextUnformatted("Screenshots (up to 5 MB each)");
            for (int i = 0; i < screenshots.Length; i++)
            {
                int index = i;
                ImGui.PushID(i);
                DrawImageSlot("ss" + i, screenshots[i], 96f, bytes => screenshots[index] = bytes, isIcon: false);
                ImGui.PopID();

                if (i < screenshots.Length - 1)
                    ImGui.SameLine();
            }

            if (error != null)
                ImGui.TextColored(Theme.Error, error);

            ImGui.SetCursorPosY(ImGui.GetWindowHeight() - ImGui.GetFrameHeight() - ImGui.GetStyle().WindowPadding.Y);
            if (ImGui.Button("Save", new Vector2(80, 0)))
                Accept();

            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                Close();
        }

        private void DrawImageSlot(string id, byte[] current, float size, Action<byte[]> assign, bool isIcon)
        {
            ImGui.BeginGroup();

            ImageHandle image = current != null && current.Length > 0 ? host.Textures.FromBytes(current, current) : null;
            if (image != null && image.IsValid)
                TextureCache.DrawFitted(image, new Vector2(size, size));
            else
                ImGui.Dummy(new Vector2(size, size));

            if (ImGui.SmallButton("Choose..."))
            {
                Dialogs.OpenFile(isIcon ? "Select Icon" : "Select Screenshot", "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", false, files =>
                {
                    byte[] bytes = Load(files[0], isIcon, out string problem);
                    if (bytes != null)
                    {
                        assign(bytes);
                        error = null;
                    }
                    else
                    {
                        error = problem;
                    }
                });
            }

            if (current != null && current.Length > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear"))
                    assign(null);
            }

            ImGui.EndGroup();
        }

        private static byte[] Load(string path, bool isIcon, out string problem)
        {
            problem = null;

            try
            {
                FileInfo fi = new FileInfo(path);

                if (!isIcon && fi.Length > 5 * 1024 * 1024)
                {
                    problem = "Screenshots cannot be larger than 5 MB.";
                    return null;
                }

                byte[] bytes = File.ReadAllBytes(path);

                if (isIcon)
                {
                    using (MemoryStream ms = new MemoryStream(bytes))
                    using (System.Drawing.Image image = System.Drawing.Image.FromStream(ms))
                    {
                        if (image.Width > 128 || image.Height > 128)
                        {
                            problem = "Icon cannot be larger than 128x128.";
                            return null;
                        }
                    }
                }

                return bytes;
            }
            catch (Exception e)
            {
                problem = "Couldn't read that image: " + e.Message;
                return null;
            }
        }

        private void Accept()
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author) ||
                string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(version))
            {
                error = "Title, Author, Category and Version are mandatory fields.";
                return;
            }

            string[] invalid = { "{", "}" };
            if (invalid.Any(title.Contains) || invalid.Any(author.Contains) || invalid.Any(category.Contains) || invalid.Any(version.Contains))
            {
                error = "Invalid characters: { }";
                return;
            }

            settings.Title = title;
            settings.Author = author;
            settings.Category = category;
            settings.SelectedCategory = selectedCategory;
            settings.Version = version;
            settings.Description = description;
            settings.Icon = icon;

            for (int i = 0; i < screenshots.Length; i++)
                settings.SetScreenshot(i, screenshots[i]);

            Close();
            onAccepted();
        }
    }
}
