using System;
using ImGuiNET;

namespace Frosty.ImGuiKit
{
    /// <summary>
    /// Draws only the rows that are on screen. Tables of tens of thousands of rows (asset lists, resource
    /// conflicts) would otherwise be drawn in full every frame. Every row must be exactly one line tall.
    /// </summary>
    public static class ListClipper
    {
        public static unsafe void Run(int count, Action<int> drawRow)
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
    }
}
