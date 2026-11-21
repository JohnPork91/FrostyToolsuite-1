using System;
using System.Collections.Generic;
using System.Text;
using Frosty.Core;
using Frosty.Core.Mod;

namespace FrostyModManager.Gui.Model
{
    /// <summary>A mod entry inside a pack. Port of FrostyAppliedMod without the WPF ImageSource.</summary>
    public sealed class AppliedMod
    {
        public IFrostyMod Mod { get; }
        public string BackupFileName { get; }
        public bool IsEnabled { get; set; }
        public bool IsFound { get; }

        public string ModName => Mod != null ? Mod.ModDetails.Title : BackupFileName;

        public AppliedMod(IFrostyMod mod, bool isEnabled = true)
        {
            Mod = mod;
            IsEnabled = isEnabled;
            IsFound = true;
        }

        public AppliedMod(string backupFileName, bool isEnabled = true)
        {
            BackupFileName = backupFileName;
            IsEnabled = isEnabled;
            IsFound = false;
        }
    }

    /// <summary>
    /// Ordered list of applied mods, persisted to the config. Port of FrostyPack: same persistence format
    /// ("file.fbmod:True|other.fbmod:False") and the same move semantics, but selections are passed as
    /// typed lists instead of <c>dynamic</c> and the event no longer depends on WPF's RoutedEventHandler.
    /// </summary>
    public sealed class Pack
    {
        public string Name { get; }
        public List<AppliedMod> AppliedMods { get; } = new List<AppliedMod>();
        public event Action AppliedModsUpdated;

        public Pack(string name)
        {
            Name = name;
        }

        /// <summary>Writes the pack to the config and notifies listeners.</summary>
        public void Refresh()
        {
            Config.Add(Name, ToConfigString(), ConfigScope.Pack);
            AppliedModsUpdated?.Invoke();
        }

        public void AddMod(IFrostyMod mod, bool isEnabled = true, string backupFileName = "")
        {
            int index = AppliedMods.FindIndex(a => a.Mod != null ? a.Mod == mod : a.ModName == backupFileName);
            if (index != -1)
                return;

            // a mod that couldn't be found is kept (IsFound = false) so the pack doesn't silently lose it
            AppliedMods.Add(mod != null ? new AppliedMod(mod, isEnabled) : new AppliedMod(backupFileName, isEnabled));
            Refresh();
        }

        public void RemoveMod(AppliedMod mod)
        {
            AppliedMods.Remove(mod);
            Refresh();
        }

        public void MoveModsUp(IList<AppliedMod> mods)
        {
            List<int> indices = Indices(mods, i => i <= 0);
            if (indices == null)
                return;

            for (int i = 0; i < indices.Count; i++)
            {
                AppliedMod mod = AppliedMods[indices[i]];
                AppliedMods.RemoveAt(indices[i]);
                AppliedMods.Insert(indices[i] - 1, mod);
            }

            Refresh();
        }

        public void MoveModsTop(IList<AppliedMod> mods)
        {
            List<int> indices = Indices(mods, i => i <= 0);
            if (indices == null)
                return;

            for (int i = 0; i < indices.Count; i++)
            {
                AppliedMod mod = AppliedMods[indices[i]];
                AppliedMods.RemoveAt(indices[i]);
                AppliedMods.Insert(i, mod);
            }

            Refresh();
        }

        public void MoveModsDown(IList<AppliedMod> mods)
        {
            List<int> indices = Indices(mods, i => i >= AppliedMods.Count - 1);
            if (indices == null)
                return;

            for (int i = indices.Count - 1; i >= 0; i--)
            {
                AppliedMod mod = AppliedMods[indices[i]];
                AppliedMods.RemoveAt(indices[i]);
                AppliedMods.Insert(indices[i] + 1, mod);
            }

            Refresh();
        }

        public void MoveModsBottom(IList<AppliedMod> mods)
        {
            List<int> indices = Indices(mods, i => i >= AppliedMods.Count - 1);
            if (indices == null)
                return;

            for (int i = indices.Count - 1; i >= 0; i--)
            {
                AppliedMod mod = AppliedMods[indices[i]];
                AppliedMods.RemoveAt(indices[i]);
                AppliedMods.Insert(AppliedMods.Count, mod);
            }

            Refresh();
        }

        /// <summary>
        /// Sorted indices of the given mods, or null if any of them is already at the edge it would move past
        /// (the original moves nothing in that case).
        /// </summary>
        private List<int> Indices(IList<AppliedMod> mods, Func<int, bool> atEdge)
        {
            List<int> indices = new List<int>(mods.Count);
            foreach (AppliedMod mod in mods)
            {
                int index = AppliedMods.FindIndex(a => a == mod);
                if (atEdge(index))
                    return null;

                indices.Add(index);
            }

            indices.Sort();
            return indices;
        }

        private string ToConfigString()
        {
            StringBuilder sb = new StringBuilder();
            foreach (AppliedMod mod in AppliedMods)
            {
                // fall back to the stored file name if the mod itself wasn't found
                string name = mod.Mod != null ? mod.Mod.Filename : mod.BackupFileName;
                sb.Append(name).Append(':').Append(mod.IsEnabled).Append('|');
            }

            if (sb.Length > 0)
                sb.Length--; // drop trailing '|'

            return sb.ToString();
        }
    }
}
