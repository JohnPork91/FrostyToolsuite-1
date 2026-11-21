using System.Numerics;
using ImGuiNET;

namespace Frosty.ImGuiKit
{
    /// <summary>
    /// Dark Frosty palette, taken from FrostyControls/Themes/Generic.xaml.
    /// </summary>
    public static class Theme
    {
        public static readonly Vector4 WindowBackground = Hex(0x141414);
        public static readonly Vector4 TitlebarBackground = Hex(0x040404);
        public static readonly Vector4 FontColor = Hex(0xF8F8F8);
        public static readonly Vector4 ControlBackground = Hex(0x454545);
        public static readonly Vector4 ControlHighlight = Hex(0x707070);
        public static readonly Vector4 ControlPressed = Hex(0x585858);
        public static readonly Vector4 ButtonImage = Hex(0x292929);
        public static readonly Vector4 ScrollbarBackground = Hex(0x232323);
        public static readonly Vector4 MenuItemHighlight = Hex(0x6C6C6C);
        public static readonly Vector4 MenuItemBackground = Hex(0x181818);
        public static readonly Vector4 SubItemHighlight = Hex(0x303030);
        public static readonly Vector4 SubItemPressed = Hex(0x404040);
        public static readonly Vector4 ListBackground = Hex(0x292929);

        public static readonly Vector4 Warning = new Vector4(1.00f, 0.78f, 0.25f, 1f);
        public static readonly Vector4 Error = new Vector4(1.00f, 0.38f, 0.38f, 1f);
        public static readonly Vector4 Disabled = new Vector4(0.55f, 0.55f, 0.55f, 1f);

        public static Vector4 Hex(int rgb, float alpha = 1f)
        {
            return new Vector4(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
        }

        public static void Apply()
        {
            ImGuiStylePtr style = ImGui.GetStyle();
            style.WindowRounding = 0f;
            style.FrameRounding = 2f;
            style.PopupRounding = 2f;
            style.ScrollbarRounding = 0f;
            style.GrabRounding = 2f;
            style.TabRounding = 0f;
            style.WindowBorderSize = 0f;
            style.FrameBorderSize = 0f;
            style.WindowPadding = new Vector2(6, 6);
            style.FramePadding = new Vector2(6, 3);
            style.ItemSpacing = new Vector2(6, 4);
            style.CellPadding = new Vector2(6, 2);

            var c = style.Colors;
            c[(int)ImGuiCol.Text] = FontColor;
            c[(int)ImGuiCol.TextDisabled] = Disabled;
            c[(int)ImGuiCol.WindowBg] = ListBackground;
            c[(int)ImGuiCol.ChildBg] = ListBackground;
            c[(int)ImGuiCol.PopupBg] = MenuItemBackground;
            c[(int)ImGuiCol.Border] = ButtonImage;
            c[(int)ImGuiCol.FrameBg] = ButtonImage;
            c[(int)ImGuiCol.FrameBgHovered] = SubItemPressed;
            c[(int)ImGuiCol.FrameBgActive] = ControlPressed;
            c[(int)ImGuiCol.TitleBg] = TitlebarBackground;
            c[(int)ImGuiCol.TitleBgActive] = TitlebarBackground;
            c[(int)ImGuiCol.MenuBarBg] = MenuItemBackground;
            c[(int)ImGuiCol.ScrollbarBg] = ScrollbarBackground;
            c[(int)ImGuiCol.ScrollbarGrab] = ControlBackground;
            c[(int)ImGuiCol.ScrollbarGrabHovered] = ControlHighlight;
            c[(int)ImGuiCol.ScrollbarGrabActive] = ControlPressed;
            c[(int)ImGuiCol.CheckMark] = FontColor;
            c[(int)ImGuiCol.SliderGrab] = ControlHighlight;
            c[(int)ImGuiCol.SliderGrabActive] = ControlPressed;
            c[(int)ImGuiCol.Button] = ControlBackground;
            c[(int)ImGuiCol.ButtonHovered] = ControlHighlight;
            c[(int)ImGuiCol.ButtonActive] = ControlPressed;
            c[(int)ImGuiCol.Header] = SubItemPressed;
            c[(int)ImGuiCol.HeaderHovered] = MenuItemHighlight;
            c[(int)ImGuiCol.HeaderActive] = ControlPressed;
            c[(int)ImGuiCol.Separator] = ButtonImage;
            c[(int)ImGuiCol.Tab] = MenuItemBackground;
            c[(int)ImGuiCol.TabHovered] = ControlHighlight;
            c[(int)ImGuiCol.TabActive] = ControlBackground;
            c[(int)ImGuiCol.TabUnfocused] = MenuItemBackground;
            c[(int)ImGuiCol.TabUnfocusedActive] = ControlBackground;
            c[(int)ImGuiCol.TableHeaderBg] = ControlBackground;
            c[(int)ImGuiCol.TableBorderStrong] = ButtonImage;
            c[(int)ImGuiCol.TableBorderLight] = SubItemHighlight;
            c[(int)ImGuiCol.TableRowBgAlt] = new Vector4(1f, 1f, 1f, 0.03f);
            c[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0f, 0f, 0f, 0.55f);
        }
    }
}
