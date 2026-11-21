using System.Collections.Generic;
using ImGuiNET;

namespace FrostyModManager.Gui.Ui
{
    /// <summary>
    /// Explorer-style selection for a list drawn with ImGui selectables: click selects one,
    /// ctrl+click toggles, shift+click selects a range from the last anchor.
    /// (ImGui has no selection model of its own; WPF's ListView gave us this for free.)
    /// </summary>
    internal sealed class MultiSelect<T> where T : class
    {
        private T anchor;

        public HashSet<T> Items { get; } = new HashSet<T>();

        /// <summary>The most recently clicked item (drives the Description tab).</summary>
        public T Primary { get; private set; }

        public int Count => Items.Count;

        public bool Contains(T item) => Items.Contains(item);

        public void Clear()
        {
            Items.Clear();
            anchor = null;
            Primary = null;
        }

        public void Click(T item, IList<T> ordered)
        {
            ImGuiIOPtr io = ImGui.GetIO();

            if (io.KeyShift && anchor != null)
            {
                int a = ordered.IndexOf(anchor);
                int b = ordered.IndexOf(item);

                if (a >= 0 && b >= 0)
                {
                    if (!io.KeyCtrl)
                        Items.Clear();

                    for (int i = System.Math.Min(a, b); i <= System.Math.Max(a, b); i++)
                        Items.Add(ordered[i]);

                    Primary = item;
                    return;
                }
            }

            if (io.KeyCtrl)
            {
                if (!Items.Remove(item))
                    Items.Add(item);
            }
            else
            {
                Items.Clear();
                Items.Add(item);
            }

            anchor = item;
            Primary = item;
        }

        /// <summary>Replaces the selection with just this item.</summary>
        public void SelectOnly(T item)
        {
            Items.Clear();
            Items.Add(item);
            anchor = item;
            Primary = item;
        }

        /// <summary>Right-click behaviour: keep a multi-selection if the item is in it, otherwise select just that item.</summary>
        public void RightClick(T item)
        {
            if (Items.Contains(item))
                return;

            Items.Clear();
            Items.Add(item);
            anchor = item;
            Primary = item;
        }

        /// <summary>Drops selected items that no longer exist (after uninstall / remove).</summary>
        public void Prune(ICollection<T> valid)
        {
            Items.RemoveWhere(i => !valid.Contains(i));

            if (anchor != null && !valid.Contains(anchor))
                anchor = null;

            if (Primary != null && !valid.Contains(Primary))
                Primary = null;
        }
    }
}
