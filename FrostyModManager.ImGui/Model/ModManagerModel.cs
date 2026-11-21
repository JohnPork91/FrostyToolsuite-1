using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Frosty.Core;
using Frosty.Core.IO;
using Frosty.Core.Mod;
using Frosty.Hash;
using Frosty.ImGuiKit;
using Frosty.ModSupport;
using FrostyModManager.Compression;
using FrostySdk;
using FrostySdk.Interfaces;
using FrostySdk.IO;
using FrostySdk.Managers;
using Newtonsoft.Json;

namespace FrostyModManager.Gui.Model
{
    public enum ModPrimaryActionType { None, Modify, Add, Merge }

    public enum ModSecondaryActionType { None, AddToBundle }

    public sealed class ModAction
    {
        public ModPrimaryActionType PrimaryAction;
        public ModSecondaryActionType SecondaryAction;
        public string Name;
    }

    /// <summary>A resource touched by one or more applied mods (row of the Actions/conflicts table).</summary>
    public sealed class ModResourceInfo
    {
        public string Name { get; }
        public string Type { get; }

        public IReadOnlyList<ModAction> Mods => mods;
        public int ModCount => mods.Count;
        public int FirstModToModifyIndex { get; private set; } = -1;

        private readonly int nameHash;
        private readonly List<ModAction> mods = new List<ModAction>();
        private readonly List<int> addBundles = new List<int>();

        public ModResourceInfo(string name, string type)
        {
            Name = name;
            Type = type;
            nameHash = Fnv1.HashString(type + "/" + name);
        }

        public void AddMod(string modFilename, ModPrimaryActionType primaryAction, IEnumerable<int> modAddBundles)
        {
            bool isAdded = false;
            if (modAddBundles != null)
            {
                foreach (int addBundle in modAddBundles)
                {
                    if (!addBundles.Contains(addBundle))
                        isAdded = true;
                    addBundles.Add(addBundle);
                }
            }

            mods.Add(new ModAction
            {
                Name = modFilename,
                PrimaryAction = primaryAction,
                SecondaryAction = isAdded ? ModSecondaryActionType.AddToBundle : ModSecondaryActionType.None
            });

            if (FirstModToModifyIndex == -1 && primaryAction != ModPrimaryActionType.None)
                FirstModToModifyIndex = mods.Count - 1;
        }

        public bool Matches(string typeSlashName) => Fnv1.HashString(typeSlashName) == nameHash;

        /// <summary>Looks up the action a given mod (by file name) has on this resource, or null.</summary>
        public ModAction ActionFor(string modFilename)
        {
            int index = mods.FindIndex(a => a.Name == modFilename);
            return index == -1 ? null : mods[index];
        }

        public string PrimaryTooltip(string modFilename)
        {
            int index = mods.FindIndex(a => a.Name == modFilename);
            if (index == -1)
                return null;

            bool first = index == FirstModToModifyIndex;
            switch (mods[index].PrimaryAction)
            {
                case ModPrimaryActionType.Modify: return first ? "Resource is initially modified by this mod" : "Resource is replaced by this mod";
                case ModPrimaryActionType.Add: return first ? "Resource is initially added by this mod" : "Resource is replaced by this mod";
                case ModPrimaryActionType.Merge: return first ? "Resource is initially modified by this mod" : "Resource is merged by this mod";
                default: return null;
            }
        }

        public string SecondaryTooltip(string modFilename)
        {
            ModAction action = ActionFor(modFilename);
            return action != null && action.SecondaryAction == ModSecondaryActionType.AddToBundle
                ? "Resource is added to other bundle(s) by this mod"
                : null;
        }

        /// <summary>Image file name (under Images/) for a mod's primary action, or null for none.</summary>
        public string PrimaryImage(string modFilename)
        {
            int index = mods.FindIndex(a => a.Name == modFilename);
            if (index == -1)
                return null;

            bool first = index == FirstModToModifyIndex;
            switch (mods[index].PrimaryAction)
            {
                case ModPrimaryActionType.Modify: return first ? "PrimaryActionModify.png" : "PrimaryActionReplace.png";
                case ModPrimaryActionType.Add: return first ? "PrimaryActionAdd.png" : "PrimaryActionReplace.png";
                case ModPrimaryActionType.Merge: return first ? "PrimaryActionModify.png" : "PrimaryActionMerge.png";
                default: return null;
            }
        }

        public string SecondaryImage(string modFilename)
        {
            ModAction action = ActionFor(modFilename);
            return action != null && action.SecondaryAction == ModSecondaryActionType.AddToBundle ? "SecondaryActionAdd.png" : null;
        }
    }

    public sealed class ConflictsData
    {
        public List<ModResourceInfo> Resources = new List<ModResourceInfo>();
        public List<AppliedMod> Columns = new List<AppliedMod>();
        public bool Cancelled;
    }

    public sealed class AffectedFileInfo
    {
        public string Name { get; }
        public string Type { get; }
        public bool IsAdded { get; }
        public bool IsModified { get; }

        public AffectedFileInfo(string name, string type, bool isAdded, bool isModified)
        {
            Name = name;
            Type = type;
            IsAdded = isAdded;
            IsModified = isModified;
        }
    }

    public struct ImportErrorInfo
    {
        public string Filename;
        public string Error;
        public bool IsWarning;
    }

    public sealed class PackManifest
    {
        public string name;
        public string managerVersion;
        public int version;
        public List<string> mods;
    }

    public sealed class InstallResult
    {
        public IFrostyMod LastInstalledMod;
        public List<ImportErrorInfo> Errors = new List<ImportErrorInfo>();
        public PackManifest PackManifest;
    }

    /// <summary>
    /// All Mod Manager behaviour that used to live in MainWindow.xaml.cs, minus the WPF.
    /// Methods that take a <see cref="TaskContext"/> are meant to run on a worker thread;
    /// anything that mutates <see cref="AvailableMods"/> does so under <see cref="Sync"/>.
    /// </summary>
    public sealed class ModManagerModel
    {
        private const int ManifestVersion = 1;

        public object Sync { get; } = new object();
        public List<IFrostyMod> AvailableMods { get; } = new List<IFrostyMod>();
        public List<Pack> Packs { get; } = new List<Pack>();
        public Pack SelectedPack { get; set; }

        /// <summary>Raised (possibly from a worker thread) whenever AvailableMods changed.</summary>
        public event Action ModsChanged;

        private static FileSystemManager Fs => Frosty.Core.App.FileSystemManager;

        private static string ModsDirectory => "Mods/" + ProfilesLibrary.ProfileName + "/";

        // ---- loading -------------------------------------------------------------------------------

        public void LoadMods(TaskContext task)
        {
            DirectoryInfo di = new DirectoryInfo("Mods/" + ProfilesLibrary.ProfileName);
            if (!di.Exists)
                Directory.CreateDirectory(di.FullName);

            foreach (FileInfo fi in di.EnumerateFiles())
            {
                if (fi.Extension != ".fbmod")
                    continue;

                int retCode;
                using (FileStream stream = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read))
                    retCode = VerifyMod(stream);

                if (retCode >= 0)
                {
                    try
                    {
                        AddMod(fi.FullName, (retCode & 0x8000) != 0 ? 1 : 0);
                    }
                    catch (FrostyModLoadException)
                    {
                        // failed to load for whatever reason
                        File.Delete(fi.FullName);
                        File.Delete(fi.FullName.Replace(".fbmod", "_01.archive"));
                    }
                }
                else if (retCode == -3)
                {
                    // bad mod. delete it
                    File.Delete(fi.FullName);
                    File.Delete(fi.FullName.Replace(".fbmod", "_01.archive"));
                }
            }

            foreach (FileInfo fi in di.EnumerateFiles())
            {
                if (fi.Extension == ".fbcollection")
                    AddCollection(fi.FullName, out _);
            }
        }

        public void LoadPacks()
        {
            foreach (string packName in Config.EnumerateKeys(ConfigScope.Pack))
            {
                string values = Config.Get(packName, "", ConfigScope.Pack);
                string[] valuesArray = values.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

                Pack pack = new Pack(packName);
                Packs.Add(pack);

                foreach (string entry in valuesArray)
                {
                    string[] pair = entry.Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                    string fileName = pair[0];
                    bool isEnabled = bool.Parse(pair[1]);

                    IFrostyMod mod = AvailableMods.Find(a => a.Filename == fileName);
                    if (mod == null)
                    {
                        // the mod may live inside a collection
                        foreach (FrostyModCollection collection in AvailableMods.OfType<FrostyModCollection>())
                        {
                            mod = collection.Mods.Find(a => a.Filename == fileName);
                            if (mod != null)
                                break;
                        }
                    }

                    pack.AddMod(mod, isEnabled, fileName);
                }
            }

            if (Packs.Count == 0)
                AddPack("Default", out _);
        }

        // ---- packs ---------------------------------------------------------------------------------

        /// <summary>Returns the new pack, or null (with an error message) if the name is taken.</summary>
        public Pack AddPack(string name, out string error)
        {
            if (Packs.Exists(a => a.Name.CompareTo(name) == 0))
            {
                error = "A pack with the same name already exists";
                return null;
            }

            Pack pack = new Pack(name);
            Packs.Add(pack);
            Config.Add(pack.Name, "", ConfigScope.Pack);

            error = null;
            return pack;
        }

        public void RemovePack(Pack pack)
        {
            Config.Remove(pack.Name, ConfigScope.Pack);
            Packs.Remove(pack);

            DirectoryInfo di = new DirectoryInfo(Fs.BasePath + "ModData\\" + pack.Name);
            if (di.Exists)
                di.Delete(true);
        }

        public Pack RenamePack(Pack oldPack, string newName, out string error)
        {
            if (Packs.Exists(a => a.Name.CompareTo(newName) == 0))
            {
                error = "A pack with the same name already exists";
                return null;
            }

            Config.Rename(oldPack.Name, newName, ConfigScope.Pack);

            Pack newPack = new Pack(newName);
            foreach (AppliedMod mod in oldPack.AppliedMods)
                newPack.AppliedMods.Add(mod);

            Packs.Add(newPack);
            Packs.Remove(oldPack);

            error = null;
            return newPack;
        }

        public Pack DuplicatePack(Pack oldPack, string newName, out string error)
        {
            if (Packs.Exists(a => a.Name.CompareTo(newName) == 0))
            {
                error = "A pack with the same name already exists";
                return null;
            }

            Pack newPack = new Pack(newName);
            foreach (AppliedMod mod in oldPack.AppliedMods)
                newPack.AppliedMods.Add(mod.IsFound ? new AppliedMod(mod.Mod, mod.IsEnabled) : new AppliedMod(mod.BackupFileName, mod.IsEnabled));

            Packs.Add(newPack);
            newPack.Refresh(); // writes the pack (and its mod list) to the config

            error = null;
            return newPack;
        }

        // ---- mod verification / loading ------------------------------------------------------------

        /// <summary>
        /// -1 unreadable, -2 wrong game, -3 invalid; otherwise bit 0 = different game version,
        /// 0x8000 = new mod format. (Return codes unchanged from the WPF implementation.)
        /// </summary>
        public int VerifyMod(Stream stream)
        {
            using (DbReader reader = new DbReader(stream, null))
            {
                ulong magic = reader.ReadULong();
                if (magic != FrostyMod.Magic)
                {
                    reader.Position = 0;

                    DbObject modObj = reader.ReadDbObject();
                    if (modObj == null)
                        return -1;

                    if (modObj.GetValue<string>("gameProfile").ToLower() != ProfilesLibrary.ProfileName.ToLower())
                        return -2;

                    if (modObj.GetValue<int>("gameVersion") != Fs.Head)
                        return 1;
                }
                else
                {
                    reader.Position = 0;
                    using (FrostyModReader modReader = new FrostyModReader(stream))
                    {
                        if (!modReader.IsValid)
                            return -1;

                        return modReader.GameVersion != Fs.Head ? 0x8001 : 0x8000;
                    }
                }
            }

            return 0;
        }

        // Update (volume) names for Need for Speed Unbound, keyed by game version.
        private static readonly Dictionary<int, string> UnboundVolumes = new Dictionary<int, string>
        {
            { 497535, "Volume 1 Hotfix 1 Update" },
            { 497685, "Volume 1 Hotfix 2 Update" },
            { 596592, "Volume 2 Update" },
            { 491756, "Volume 3 Update" },
            { 497876, "Volume 4 Update" },
            { 497877, "Volume 4 Hotfix Update" },
            { 502850, "Volume 5 Update" },
            { 502852, "Volume 5 Hotfix Update" },
            { 503116, "Volume 5.1.0 Update" },
            { 510750, "Volume 6.0.1 Update" },
            { 510749, "Volume 6.1.0 Update" },
            { 521529, "Volume 7.0.1 Update" },
            { 521533, "Volume 7.0.2 Update" },
            { 521663, "Volume 7.1.0 Update" },
            { 530988, "Volume 8.0.1(.2) Update" },
            { 530989, "Volume 8.0.1.1 Update" },
            { 541601, "Volume 9.0.1 Update" },
            { 541606, "Volume 9.0.2 Update" },
        };

        public FrostyMod AddMod(string modFilename, int format)
        {
            FrostyMod mod;
            if (format == 1)
            {
                mod = new FrostyMod(modFilename);
            }
            else
            {
                DbObject modObj;
                using (DbReader reader = new DbReader(new FileStream(modFilename, FileMode.Open, FileAccess.Read), null))
                    modObj = reader.ReadDbObject();

                mod = new FrostyMod(modFilename, modObj);
            }

            bool differentVersion = mod.GameVersion != Fs.Head;
            if (differentVersion)
            {
                if (ProfilesLibrary.IsLoaded(ProfileVersion.NeedForSpeedUnbound) && UnboundVolumes.TryGetValue(mod.GameVersion, out string volume))
                    mod.AddWarning("This mod was designed for " + volume);
                else
                    mod.AddWarning("Mod was designed for a different game version");
            }

            lock (Sync)
                AvailableMods.Add(mod);

            ModsChanged?.Invoke();
            return mod;
        }

        public FrostyModCollection AddCollection(string collectionFilename, out string error)
        {
            FrostyModCollection collection;
            try
            {
                collection = new FrostyModCollection(collectionFilename);
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }

            lock (Sync)
            {
                // mods that are part of the collection are shown inside it, not on their own
                foreach (FrostyMod mod in collection.Mods)
                {
                    int index = AvailableMods.FindIndex(a => a.Filename == mod.Filename);
                    if (index != -1)
                        AvailableMods.RemoveAt(index);
                }

                AvailableMods.Add(collection);
            }

            ModsChanged?.Invoke();
            error = null;
            return collection;
        }

        // ---- install / uninstall -------------------------------------------------------------------

        private static bool IsCompressed(FileInfo fi) =>
            fi.Extension == ".rar" || fi.Extension == ".zip" || fi.Extension == ".7z" || fi.Extension == ".fbpack";

        public InstallResult InstallMods(string[] filenames, TaskContext task)
        {
            InstallResult result = new InstallResult();
            InstallInto(filenames, task, result);
            return result;
        }

        private void InstallInto(string[] filenames, TaskContext task, InstallResult result)
        {
            foreach (string filename in filenames)
            {
                FileInfo fi = new FileInfo(filename);
                task.Update(fi.Name);

                try
                {
                    if (IsCompressed(fi))
                        InstallArchive(fi, task, result);
                    else if (fi.Extension == ".daimod")
                        InstallDaiMod(fi, result);
                    else
                        InstallLooseMod(fi, result);
                }
                catch (FrostyModLoadException e)
                {
                    result.Errors.Add(new ImportErrorInfo { Error = e.Message, Filename = fi.Name });
                    File.Delete(fi.FullName);
                }
            }
        }

        private void RemoveExisting(string modFilename, string archiveSearchPattern, bool deleteModFile)
        {
            FrostyMod existing = null;
            lock (Sync)
            {
                existing = AvailableMods.Find(a => a.Filename.ToLower().CompareTo(modFilename.ToLower()) == 0) as FrostyMod;
                if (existing != null)
                    AvailableMods.Remove(existing);
            }

            if (existing == null)
                return;

            DirectoryInfo di = new DirectoryInfo(ModsDirectory);
            foreach (FileInfo archiveFi in di.GetFiles(archiveSearchPattern))
                File.Delete(archiveFi.FullName);

            if (deleteModFile)
                File.Delete(di.FullName + "/" + modFilename);
        }

        private void InstallArchive(FileInfo fi, TaskContext task, InstallResult result)
        {
            List<string> mods = new List<string>();
            List<string> collections = new List<string>();
            List<int> format = new List<int>();
            List<string> archives = new List<string>();
            int fbpacks = 0;

            IDecompressor decompressor = null;
            if (fi.Extension == ".rar") decompressor = new RarDecompressor();
            else if (fi.Extension == ".zip" || fi.Extension == ".fbpack") decompressor = new ZipDecompressor();
            else if (fi.Extension == ".7z") decompressor = new SevenZipDecompressor();

            try
            {
                // search out fbmods in archive
                decompressor.OpenArchive(fi.FullName);
                foreach (CompressedFileInfo compressedFi in decompressor.EnumerateFiles())
                {
                    if (compressedFi.Extension == ".fbpack")
                    {
                        // nested pack: extract to a temp file and install it recursively
                        DirectoryInfo tempdir = new DirectoryInfo("temp/");
                        FileInfo tempfile = new FileInfo(tempdir + compressedFi.Filename);

                        tempdir.Create();
                        decompressor.DecompressToFile(tempfile.FullName);

                        InstallInto(new[] { tempfile.FullName }, task, result);

                        if (tempfile.Exists) tempfile.Delete();
                        if (tempdir.Exists) tempdir.Delete();

                        fbpacks++;
                    }
                    else if (compressedFi.Extension == ".fbcollection")
                    {
                        collections.Add(compressedFi.Filename);
                    }
                    else if (compressedFi.Extension == ".fbmod")
                    {
                        string modFilename = compressedFi.Filename;
                        byte[] buffer = decompressor.DecompressToMemory();

                        using (MemoryStream ms = new MemoryStream(buffer))
                        {
                            int retCode = VerifyMod(ms);
                            if (retCode >= 0)
                            {
                                if ((retCode & 1) != 0)
                                {
                                    result.Errors.Add(new ImportErrorInfo { Filename = modFilename, Error = "Mod was designed for a different game version, it may or may not work.", IsWarning = true });
                                }

                                mods.Add(compressedFi.Filename);
                                format.Add((retCode & 0x8000) != 0 ? 1 : 0);
                            }
                            // ignore RetCode -1 here
                            else if (retCode == -2)
                            {
                                result.Errors.Add(new ImportErrorInfo { Filename = modFilename, Error = "Mod was not designed for this game." });
                            }
                        }
                    }
                    else if (compressedFi.Extension == ".archive")
                    {
                        archives.Add(compressedFi.Filename);
                    }
                    else if (compressedFi.Filename == "manifest.json")
                    {
                        using (StreamReader reader = new StreamReader(compressedFi.Stream))
                            result.PackManifest = JsonConvert.DeserializeObject<PackManifest>(reader.ReadToEnd());
                    }
                }
                decompressor.CloseArchive();
            }
            catch
            {
                result.Errors.Add(new ImportErrorInfo { Filename = fi.Name, Error = "Failed to read Archive." });
            }

            if (mods.Count == 0 && fbpacks == 0)
            {
                // no point continuing with this archive
                result.Errors.Add(new ImportErrorInfo { Filename = fi.Name, Error = "Archive contains no installable mods." });
                return;
            }

            // remove any invalid mods
            for (int i = 0; i < mods.Count; i++)
            {
                string mod = mods[i];
                if (format[i] == 0 && !archives.Contains(mod.Replace(".fbmod", "_01.archive")))
                {
                    // old legacy format requires an archive
                    result.Errors.Add(new ImportErrorInfo { Filename = mod, Error = "Mod is missing the archive component." });
                    mods.RemoveAt(i);
                    format.RemoveAt(i); // the WPF code forgot this, which let formats drift out of step with mods
                    i--;
                    continue;
                }

                // replace an existing mod of the same name
                RemoveExisting(mod, mod.Replace(".fbmod", "") + "*.archive", deleteModFile: false);
            }

            // remove unreferenced .archives
            for (int i = 0; i < archives.Count; i++)
            {
                if (!mods.Contains(archives[i].Replace("_01.archive", ".fbmod")))
                {
                    archives.RemoveAt(i);
                    i--;
                }
            }

            if (mods.Count > 0)
            {
                ExtractFrom(decompressor, fi.FullName, name => mods.Contains(name) || archives.Contains(name));

                for (int i = 0; i < mods.Count; i++)
                    result.LastInstalledMod = AddMod(new FileInfo(ModsDirectory + mods[i]).FullName, format[i]);
            }

            if (collections.Count > 0)
            {
                ExtractFrom(decompressor, fi.FullName, name => collections.Contains(name));

                foreach (string collectionName in collections)
                {
                    FrostyModCollection collection = AddCollection(new FileInfo(ModsDirectory + collectionName).FullName, out string error);
                    if (collection != null)
                        result.LastInstalledMod = collection;
                    else
                        result.Errors.Add(new ImportErrorInfo { Filename = collectionName, Error = error });
                }
            }
        }

        private static void ExtractFrom(IDecompressor decompressor, string archivePath, Func<string, bool> wanted)
        {
            decompressor.OpenArchive(archivePath);
            foreach (CompressedFileInfo compressedFi in decompressor.EnumerateFiles())
            {
                if (wanted(compressedFi.Filename))
                    decompressor.DecompressToFile(ModsDirectory + compressedFi.Filename);
            }
            decompressor.CloseArchive();
        }

        private void InstallLooseMod(FileInfo fi, InstallResult result)
        {
            // dont allow any files without fbmod extension
            if (fi.Extension != ".fbmod")
            {
                if (fi.Extension == ".archive")
                    return;

                result.Errors.Add(new ImportErrorInfo { Filename = fi.Name, Error = "File is not a valid Frosty Mod." });
                return;
            }

            // make sure mod is designed for current profile
            bool newFormat;
            using (FileStream stream = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read))
            {
                int retCode = VerifyMod(stream);
                if ((retCode & 1) != 0)
                {
                    result.Errors.Add(new ImportErrorInfo { Filename = fi.Name, Error = "Mod was designed for a different game version, it may or may not work.", IsWarning = true });
                }
                else if (retCode == -1)
                {
                    result.Errors.Add(new ImportErrorInfo { Filename = fi.Name, Error = "File is not a valid Frosty Mod." });
                }
                else if (retCode == -2)
                {
                    result.Errors.Add(new ImportErrorInfo { Filename = fi.Name, Error = "Mod was not designed for this game." });
                    return;
                }
                else if (retCode == -3)
                {
                    result.Errors.Add(new ImportErrorInfo { Filename = fi.Name, Error = "Mod was found to be invalid and cannot be used" });
                    return;
                }

                newFormat = (retCode & 0x8000) != 0;
            }

            if (!newFormat && !File.Exists(fi.FullName.Replace(".fbmod", "_01.archive")))
            {
                result.Errors.Add(new ImportErrorInfo { Filename = fi.Name, Error = "Mod is missing the archive component." });
                return;
            }

            // replace an existing mod of the same name
            RemoveExisting(fi.Name, fi.Name.Replace(".fbmod", "") + "_*.archive", deleteModFile: true);

            // copy mod over
            File.Copy(fi.FullName, ModsDirectory + fi.Name);
            foreach (FileInfo archiveFi in fi.Directory.GetFiles(fi.Name.Replace(".fbmod", "") + "_*.archive"))
                File.Copy(archiveFi.FullName, ModsDirectory + archiveFi.Name);

            result.LastInstalledMod = AddMod(new FileInfo(ModsDirectory + fi.Name).FullName, newFormat ? 1 : 0);
        }

        /// <summary>
        /// Converts a Dragon Age: Inquisition .daimod into an .fbmod + archive pair.
        /// NOTE: .daimod files that ship a configuration script (the WPF ConfigWindow) are imported with the
        /// script's default selections and a warning, because that dialog has not been ported yet.
        /// </summary>
        private void InstallDaiMod(FileInfo fi, InstallResult result)
        {
            using (NativeReader reader = new NativeReader(new FileStream(fi.FullName, FileMode.Open)))
            {
                string magic = reader.ReadSizedString(8);
                if (magic != "DAIMODV2")
                {
                    result.Errors.Add(new ImportErrorInfo { Filename = fi.Name, Error = "File is not a valid DAI Mod." });
                    return;
                }

                reader.ReadInt(); // unknown
                reader.ReadNullTerminatedString(); // name
                string xml = reader.ReadNullTerminatedString();
                string code = reader.ReadNullTerminatedString();

                int resCount = reader.ReadInt();
                List<byte[]> resources = new List<byte[]>();
                List<bool> shouldWrite = new List<bool>();

                for (int i = 0; i < resCount; i++)
                {
                    resources.Add(reader.ReadBytes(reader.ReadInt()));
                    shouldWrite.Add(true);
                }

                string configValues = "";
                if (code != "")
                {
                    result.Errors.Add(new ImportErrorInfo
                    {
                        Filename = fi.Name,
                        Error = "This DAI mod has configuration options, which the ImGui Mod Manager can't show yet. All optional resources were included.",
                        IsWarning = true
                    });
                }

                System.Xml.XmlDocument xmlDoc = new System.Xml.XmlDocument();
                xmlDoc.LoadXml(xml);

                System.Xml.XmlElement elem = xmlDoc["daimod"]["details"];
                string newDesc = "(Converted from .daimod)\r\n\r\n" + elem["description"].InnerText + "\r\n\r\n" + configValues;

                DbObject modObject = new DbObject();
                modObject.AddValue("magic", "FBMODV2");
                modObject.AddValue("gameProfile", ProfilesLibrary.ProfileName);
                modObject.AddValue("gameVersion", 0);

                modObject.AddValue("title", elem["name"].InnerText);
                modObject.AddValue("author", elem["author"].InnerText);
                modObject.AddValue("category", "DAI Mods");
                modObject.AddValue("version", elem["version"].InnerText);
                modObject.AddValue("description", newDesc);

                DbObject resourcesList = new DbObject(false);
                DbObject actionsList = new DbObject(false);
                DbObject screenshotList = new DbObject(false);
                long offset = 0;

                int index = -1;
                int actualIndex = 0;

                foreach (System.Xml.XmlElement subElem in xmlDoc["daimod"]["resources"])
                {
                    index++;
                    if (subElem.GetAttribute("action") == "remove")
                        continue;

                    int resId = int.Parse(subElem.GetAttribute("resourceId"));
                    if (shouldWrite[resId] == false)
                        continue;

                    int resSize = resources[resId].Length;
                    string type = subElem.GetAttribute("type");

                    DbObject resource = new DbObject();
                    resource.AddValue("name", subElem.GetAttribute("name"));
                    resource.AddValue("type", type);

                    resource.AddValue("sha1", new Sha1(subElem.GetAttribute("sha1")));
                    resource.AddValue("originalSize", 0);
                    resource.AddValue("compressedSize", resSize);
                    resource.AddValue("archiveIndex", 1);
                    resource.AddValue("archiveOffset", offset);
                    resource.AddValue("shouldInline", false);

                    string actionString = subElem.GetAttribute("action");
                    if (type == "ebx" || type == "res")
                    {
                        resource.AddValue("uncompressedSize", int.Parse(subElem.GetAttribute("originalSize")));
                        if (actionString != "add")
                            resource.AddValue("originalSha1", new Sha1(subElem.GetAttribute("originalSha1")));
                        actionString = "modify";

                        if (type == "res")
                        {
                            resource.AddValue("resType", uint.Parse(subElem.GetAttribute("resType")));
                            if (resource.GetValue<uint>("resType") == 0x5C4954A6)
                                resource.SetValue("shouldInline", true);
                            resource.AddValue("resRid", (ulong)long.Parse(subElem.GetAttribute("resRid")));

                            string resMetaString = subElem.GetAttribute("meta");
                            byte[] resMeta = new byte[resMetaString.Length / 2];
                            for (int i = 0; i < resMeta.Length; i++)
                                resMeta[i] = byte.Parse(resMetaString.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier);

                            resource.AddValue("resMeta", resMeta);
                        }
                    }
                    else
                    {
                        string chunkid = subElem.GetAttribute("name");
                        byte[] chunkidBytes = new byte[chunkid.Length / 2];
                        for (int i = 0; i < chunkidBytes.Length; i++)
                            chunkidBytes[i] = byte.Parse(chunkid.Substring(i * 2, 2), NumberStyles.AllowHexSpecifier);

                        resource.SetValue("name", new Guid(chunkidBytes).ToString());
                        resource.AddValue("rangeStart", uint.Parse(subElem.GetAttribute("rangeStart")));
                        resource.AddValue("rangeEnd", uint.Parse(subElem.GetAttribute("rangeEnd")));
                        resource.AddValue("logicalOffset", uint.Parse(subElem.GetAttribute("logicalOffset")));
                        resource.AddValue("logicalSize", uint.Parse(subElem.GetAttribute("logicalSize")));

                        if (subElem.GetAttribute("meta") != "00")
                            resource.SetValue("firstMip", 3);

                        // add special chunks bundle
                        DbObject action = new DbObject();
                        action.AddValue("resourceId", resourcesList.Count - 1);
                        action.AddValue("type", "add");
                        action.AddValue("bundle", "chunks");
                        actionsList.Add(action);
                    }

                    foreach (System.Xml.XmlElement bundleElem in xmlDoc["daimod"]["bundles"])
                    {
                        foreach (System.Xml.XmlElement entryElem in bundleElem["entries"])
                        {
                            int id = int.Parse(entryElem.GetAttribute("id"));
                            if (id == index)
                            {
                                DbObject action = new DbObject();
                                action.AddValue("resourceId", actualIndex);
                                action.AddValue("type", actionString);
                                action.AddValue("bundle", bundleElem.GetAttribute("name"));
                                actionsList.Add(action);
                            }
                        }
                    }

                    resourcesList.Add(resource);
                    offset += resSize;
                    actualIndex++;
                }

                modObject.AddValue("screenshots", screenshotList);
                modObject.AddValue("resources", resourcesList);
                modObject.AddValue("actions", actionsList);

                using (DbWriter writer = new DbWriter(new FileStream(ModsDirectory + fi.Name.Replace(".daimod", ".fbmod"), FileMode.Create)))
                    writer.Write(modObject);
                using (NativeWriter writer = new NativeWriter(new FileStream(ModsDirectory + fi.Name.Replace(".daimod", "_01.archive"), FileMode.Create)))
                {
                    for (int i = 0; i < resources.Count; i++)
                    {
                        if (shouldWrite[i])
                            writer.Write(resources[i]);
                    }
                }

                result.LastInstalledMod = AddMod(new FileInfo(ModsDirectory + fi.Name.Replace(".daimod", ".fbmod")).FullName, 0);
            }
        }

        public void UninstallMods(IEnumerable<IFrostyMod> mods)
        {
            foreach (IFrostyMod mod in mods.ToList())
            {
                FileInfo fi = new FileInfo(mod.Path);

                lock (Sync)
                    AvailableMods.Remove(mod);

                // remove from current pack
                int idx = SelectedPack.AppliedMods.FindIndex(a => a.Mod == mod);
                if (idx != -1)
                    SelectedPack.AppliedMods.RemoveAt(idx);

                if (!fi.Exists)
                    continue;

                File.Delete(fi.FullName);

                if (mod is FrostyModCollection collection && Config.Get<bool>("DeleteCollectionMods", true))
                {
                    foreach (FrostyMod child in collection.Mods)
                        File.Delete(new FileInfo(child.Path).FullName);
                }
            }

            SelectedPack.Refresh();
            ModsChanged?.Invoke();
        }

        // ---- pack import/export --------------------------------------------------------------------

        public void ExportPack(string filename)
        {
            try
            {
                List<string> mods = new List<string>();
                foreach (AppliedMod mod in SelectedPack.AppliedMods)
                {
                    if (mod.IsFound && mod.IsEnabled)
                        mods.Add(mod.Mod.Filename);
                }

                PackManifest manifest = new PackManifest
                {
                    managerVersion = Frosty.Core.App.Version,
                    version = ManifestVersion,
                    name = SelectedPack.Name,
                    mods = mods
                };

                // the file dialog has already confirmed overwriting; Create mode would otherwise throw on an existing file
                if (File.Exists(filename))
                    File.Delete(filename);

                using (ZipArchive archive = ZipFile.Open(filename, ZipArchiveMode.Create))
                {
                    foreach (AppliedMod mod in SelectedPack.AppliedMods)
                    {
                        if (mod.Mod is FrostyModCollection)
                            continue;
                        archive.CreateEntryFromFile(((FrostyMod)mod.Mod).Path, mod.Mod.Filename);
                    }

                    ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
                    using (Stream stream = manifestEntry.Open())
                    {
                        byte[] buffer = Encoding.ASCII.GetBytes(JsonConvert.SerializeObject(manifest, Formatting.Indented));
                        stream.Write(buffer, 0, buffer.Length);
                    }
                }
            }
            catch
            {
                File.Delete(filename);
            }
        }

        // ---- affected files (description tab) ------------------------------------------------------

        public static List<AffectedFileInfo> GetAffectedFiles(IFrostyMod mod)
        {
            List<AffectedFileInfo> affected = new List<AffectedFileInfo>();

            FrostyMod[] localMods = mod is FrostyModCollection collection
                ? collection.Mods.ToArray()
                : new[] { mod as FrostyMod };

            foreach (FrostyMod localMod in localMods)
            {
                if (localMod == null)
                    continue;

                if (localMod.NewFormat)
                {
                    foreach (BaseModResource resource in localMod.Resources)
                    {
                        if (resource.Type == ModResourceType.Embedded)
                            continue;

                        string resType = resource.Type.ToString().ToUpper();
                        string resourceName = resource.Name;

                        if (resource.UserData != "")
                        {
                            string[] arr = resource.UserData.Split(';');
                            resType = arr[0].ToUpper();
                            resourceName = arr[1];
                        }

                        affected.Add(new AffectedFileInfo(resourceName, resType, resource.IsAdded, resource.IsModified));
                    }
                }
                else
                {
                    DbObject modObj;
                    using (DbReader reader = new DbReader(new FileStream(localMod.Path, FileMode.Open, FileAccess.Read), null))
                        modObj = reader.ReadDbObject();

                    int resourceId = -1;
                    foreach (DbObject resource in modObj.GetValue<DbObject>("resources"))
                    {
                        resourceId++;

                        string resourceType = resource.GetValue<string>("type");
                        if (resourceType == "embedded")
                            continue;

                        bool isModify = false;
                        bool isAdded = false;

                        foreach (DbObject action in modObj.GetValue<DbObject>("actions"))
                        {
                            if (action.GetValue<int>("resourceId") != resourceId)
                                continue;

                            switch (action.GetValue<string>("type"))
                            {
                                case "modify": isModify = true; break;
                                case "add": isAdded = true; break;
                            }
                        }

                        affected.Add(new AffectedFileInfo(resource.GetValue<string>("name"), resourceType.ToUpper(), isAdded, isModify));
                    }
                }
            }

            return affected;
        }

        // ---- conflicts / actions tab ---------------------------------------------------------------

        public ConflictsData BuildConflicts(bool onlyShowReplacements, TaskContext task)
        {
            ConflictsData data = new ConflictsData();
            List<ModResourceInfo> totalResourceList = data.Resources;
            CancellationToken token = task.Cancellation.Token;

            try
            {
                foreach (AppliedMod appliedMod in SelectedPack.AppliedMods)
                {
                    // mirrors the original check, which only skips mods that are both missing AND disabled
                    if (!appliedMod.IsFound && !appliedMod.IsEnabled)
                        continue;

                    FrostyMod[] mods = appliedMod.Mod is FrostyModCollection collection
                        ? collection.Mods.ToArray()
                        : new[] { appliedMod.Mod as FrostyMod };

                    foreach (FrostyMod mod in mods)
                    {
                        if (mod == null || !mod.NewFormat)
                            continue;

                        foreach (BaseModResource resource in mod.Resources)
                        {
                            if (resource.Type == ModResourceType.Embedded)
                                continue;

                            string resType = resource.Type.ToString().ToLower();
                            string resourceName = resource.Name;

                            if (resource.UserData != "")
                            {
                                string[] arr = resource.UserData.Split(';');
                                resType = arr[0].ToLower();
                                resourceName = arr[1];
                            }

                            ModResourceInfo info = FindOrAdd(totalResourceList, resourceName, resType);
                            token.ThrowIfCancellationRequested();

                            ModPrimaryActionType primaryAction = ModPrimaryActionType.None;
                            if (resource.HasHandler)
                            {
                                if ((uint)resource.Handler == 0xBD9BFB65)
                                {
                                    primaryAction = ModPrimaryActionType.Merge;
                                }
                                else
                                {
                                    ICustomActionHandler handler = null;
                                    if (resource.Type == ModResourceType.Ebx)
                                        handler = Frosty.Core.App.PluginManager.GetCustomHandler((uint)resource.Handler);
                                    else if (resource.Type == ModResourceType.Res)
                                        handler = Frosty.Core.App.PluginManager.GetCustomHandler((ResourceType)(resource as ResResource).ResType);
                                    else if (resource.Type == ModResourceType.FsFile)
                                        handler = (ICustomActionHandler)Frosty.Core.App.PluginManager.GetCustomAssetHandler("fs");

                                    if (handler.Usage == HandlerUsage.Merge)
                                    {
                                        foreach (string actionString in handler.GetResourceActions(resource.Name, mod.GetResourceData(resource)))
                                        {
                                            string[] arr = actionString.Split(';');
                                            FindOrAdd(totalResourceList, arr[0], arr[1])
                                                .AddMod(mod.Filename, (ModPrimaryActionType)Enum.Parse(typeof(ModPrimaryActionType), arr[2]), null);
                                        }
                                        primaryAction = ModPrimaryActionType.Merge;
                                    }
                                    else
                                    {
                                        primaryAction = ModPrimaryActionType.Modify;
                                    }
                                }
                            }
                            else if (resource.IsAdded) primaryAction = ModPrimaryActionType.Add;
                            else if (resource.IsModified) primaryAction = ModPrimaryActionType.Modify;

                            info.AddMod(mod.Filename, primaryAction, resource.AddedBundles);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                data.Cancelled = true;
                return data;
            }

            if (onlyShowReplacements)
                totalResourceList.RemoveAll(item => item.ModCount <= 1);

            foreach (AppliedMod appliedMod in SelectedPack.AppliedMods)
            {
                if (!appliedMod.IsFound && !appliedMod.IsEnabled)
                    continue;

                data.Columns.Add(appliedMod);
            }

            // original sorts by the second character of the type string, then by name
            totalResourceList.Sort((a, b) =>
            {
                int result = a.Type[1].CompareTo(b.Type[1]);
                return result == 0 ? a.Name.CompareTo(b.Name) : result;
            });

            return data;
        }

        private static ModResourceInfo FindOrAdd(List<ModResourceInfo> list, string name, string type)
        {
            ModResourceInfo info = list.Find(a => a.Matches(type + "/" + name));
            if (info == null)
            {
                info = new ModResourceInfo(name, type);
                list.Add(info);
            }

            return info;
        }

        // ---- launch --------------------------------------------------------------------------------

        /// <summary>Runs the game with the selected pack applied. Returns the executor's exit code, or -1 if cancelled.</summary>
        public int Launch(TaskContext task, string additionalArgs)
        {
            Config.Save();

            // same order as the WPF launchButton_Click: fresh file system, then the pack name, then the mod list
            PrepareFileSystemForLaunch();
            Frosty.Core.App.SelectedPack = SelectedPack.Name;

            List<string> modPaths = new List<string>();
            foreach (AppliedMod mod in SelectedPack.AppliedMods)
            {
                if (mod.IsFound && mod.IsEnabled)
                    modPaths.Add(mod.Mod.Filename);
            }

            CancellationToken token = task.Cancellation.Token;
            int retCode;

            try
            {
                foreach (var executionAction in Frosty.Core.App.PluginManager.ExecutionActions)
                    executionAction.PreLaunchAction(task.TaskLogger, PluginManagerType.ModManager, token);

                FrostyModExecutor modExecutor = new FrostyModExecutor();
                retCode = modExecutor.Run(Fs, token, task.TaskLogger, $"Mods/{ProfilesLibrary.ProfileName}/",
                    Frosty.Core.App.SelectedPack, additionalArgs.Trim(), modPaths.ToArray());

                foreach (var executionAction in Frosty.Core.App.PluginManager.ExecutionActions)
                    executionAction.PostLaunchAction(task.TaskLogger, PluginManagerType.ModManager, token);
            }
            catch (OperationCanceledException)
            {
                retCode = -1;

                foreach (var executionAction in Frosty.Core.App.PluginManager.ExecutionActions)
                    executionAction.PostLaunchAction(task.TaskLogger, PluginManagerType.ModManager, token);

                Frosty.Core.App.Logger.Log("Launch Cancelled");
            }

            return retCode;
        }

        /// <summary>Re-initializes the file system for the selected game right before launching (as the WPF version did).</summary>
        public void PrepareFileSystemForLaunch()
        {
            Frosty.Core.App.FileSystemManager = new FileSystemManager(Config.Get<string>("GamePath", "", ConfigScope.Game));
            foreach (FileSystemSource source in ProfilesLibrary.Sources)
                Frosty.Core.App.FileSystemManager.AddSource(source.Path, source.SubDirs);
            Frosty.Core.App.FileSystemManager.Initialize(KeyManager.Instance.GetKey("Key1"));
        }
    }
}
