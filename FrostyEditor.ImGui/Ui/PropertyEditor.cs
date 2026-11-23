using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Frosty.Core.Controls;
using Frosty.ImGuiKit;
using FrostySdk.Ebx;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using ImGuiNET;

namespace FrostyEditor.Gui.Ui
{
    /// <summary>
    /// Reflection-driven property editor for EBX objects. Replaces FrostyPropertyGrid (a WPF TreeView built from
    /// control templates). Objects are generated SDK classes, so everything is discovered through PropertyInfo and the
    /// FrostySdk.Attributes metadata, exactly like the WPF grid did.
    ///
    /// Not ported yet: undo/redo, the property-name filter, type overrides from plugins, pointer pickers, class
    /// selectors for new array elements and the plugin-provided per-property editors.
    /// </summary>
    internal sealed class PropertyEditor
    {
        private const int MaxDepth = 24;
        private const int PageSize = 100;

        private sealed class PropMeta
        {
            public PropertyInfo Info;
            public string Name;
            public string Description;
            public bool ReadOnly;
            public bool FixedSize;
        }

        private static readonly Dictionary<Type, PropMeta[]> metaCache = new Dictionary<Type, PropMeta[]>();

        private readonly Action<EbxAssetEntry> openAsset;
        private readonly Dictionary<uint, int> listLimits = new Dictionary<uint, int>();

        /// <summary>Set whenever the user changes a value. The owner clears it after saving the change into the asset.</summary>
        public bool Changed { get; set; }

        public PropertyEditor(Action<EbxAssetEntry> openAsset)
        {
            this.openAsset = openAsset;
        }

        /// <summary>Draws all properties of <paramref name="obj"/> (an EBX object) at the current cursor.</summary>
        public void DrawObject(object obj, bool readOnly = false)
        {
            DrawProperties(obj, readOnly, 0);
        }

        // ---- metadata ------------------------------------------------------------------------------

        private static PropMeta[] GetMeta(Type type)
        {
            lock (metaCache)
            {
                if (metaCache.TryGetValue(type, out PropMeta[] cached))
                    return cached;

                PropertyInfo[] properties = type.GetProperties();
                Array.Sort(properties, new PropertyComparer());

                List<PropMeta> result = new List<PropMeta>(properties.Length);
                foreach (PropertyInfo pi in properties)
                {
                    if (pi.GetIndexParameters().Length != 0)
                        continue;

                    if (pi.GetCustomAttribute<FrostySdk.Attributes.IsHiddenAttribute>() != null)
                        continue;

                    PropMeta meta = new PropMeta { Info = pi, Name = pi.Name };

                    FrostySdk.Attributes.DisplayNameAttribute display = pi.GetCustomAttribute<FrostySdk.Attributes.DisplayNameAttribute>();
                    if (display != null)
                        meta.Name = display.Name;

                    FrostySdk.Attributes.DescriptionAttribute description = pi.GetCustomAttribute<FrostySdk.Attributes.DescriptionAttribute>();
                    if (description != null)
                        meta.Description = description.Description;

                    meta.ReadOnly = pi.GetCustomAttribute<FrostySdk.Attributes.IsReadOnlyAttribute>() != null || !pi.CanWrite;
                    meta.FixedSize = pi.GetCustomAttribute<FrostySdk.Attributes.FixedSizeArrayAttribute>() != null;

                    result.Add(meta);
                }

                PropMeta[] array = result.ToArray();
                metaCache[type] = array;
                return array;
            }
        }

        // ---- objects -------------------------------------------------------------------------------

        private void DrawProperties(object obj, bool readOnly, int depth)
        {
            if (obj == null || depth > MaxDepth)
                return;

            foreach (PropMeta meta in GetMeta(obj.GetType()))
            {
                object value;
                try
                {
                    value = meta.Info.GetValue(obj);
                }
                catch
                {
                    continue;
                }

                ImGui.PushID(meta.Info.Name);

                PropMeta captured = meta;
                DrawValue(meta.Name, value, meta.Info.PropertyType, readOnly || meta.ReadOnly, meta.FixedSize, meta.Description, depth,
                    v => captured.Info.SetValue(obj, v), null);

                ImGui.PopID();
            }
        }

        // ---- values --------------------------------------------------------------------------------

        private void Label(string label, string description, Action onRemove)
        {
            if (onRemove != null)
            {
                if (ImGui.SmallButton("-"))
                    onRemove();

                ImGui.SameLine();
            }

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(label);

            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);

            ImGui.SameLine(Math.Max(220f, ImGui.GetWindowWidth() * 0.38f));
            ImGui.SetNextItemWidth(-1f);
        }

        private bool Node(string label, string summary, string description, Action onRemove, ImGuiTreeNodeFlags extra = ImGuiTreeNodeFlags.None)
        {
            if (onRemove != null)
            {
                if (ImGui.SmallButton("-"))
                    onRemove();

                ImGui.SameLine();
            }

            bool open = ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.SpanAvailWidth | extra);

            if (!string.IsNullOrEmpty(description) && ImGui.IsItemHovered())
                ImGui.SetTooltip(description);

            if (!string.IsNullOrEmpty(summary))
            {
                ImGui.SameLine(Math.Max(220f, ImGui.GetWindowWidth() * 0.38f));
                ImGui.TextDisabled(summary);
            }

            return open;
        }

        private void Apply(Action<object> set, object value)
        {
            try
            {
                set(value);
                Changed = true;
            }
            catch (Exception e)
            {
                Frosty.Core.App.Logger.LogError("Couldn't set value: {0}", e.Message);
            }
        }

        private void DrawValue(string label, object value, Type declaredType, bool readOnly, bool fixedSize, string description,
            int depth, Action<object> set, Action onRemove)
        {
            Type type = value?.GetType() ?? declaredType;

            if (type == typeof(bool))
            {
                Label(label, description, onRemove);
                ImGui.BeginDisabled(readOnly);
                bool v = value is bool b && b;
                if (ImGui.Checkbox("##v", ref v))
                    Apply(set, v);
                ImGui.EndDisabled();
            }
            else if (type == typeof(string))
            {
                Label(label, description, onRemove);
                ImGui.BeginDisabled(readOnly);
                string s = (string)value ?? "";
                if (ImGui.InputText("##v", ref s, 1 << 16))
                    Apply(set, s);
                ImGui.EndDisabled();
            }
            else if (type == typeof(CString))
            {
                Label(label, description, onRemove);
                ImGui.BeginDisabled(readOnly);
                string s = value is CString cs ? (cs.ToString() ?? "") : "";
                if (ImGui.InputText("##v", ref s, 1 << 16))
                    Apply(set, new CString(s));
                ImGui.EndDisabled();
            }
            else if (type == typeof(FileRef))
            {
                Label(label, description, onRemove);
                ImGui.BeginDisabled(readOnly);
                string s = value is FileRef fr ? ((string)fr ?? "") : "";
                if (ImGui.InputText("##v", ref s, 1 << 16))
                    Apply(set, new FileRef(s));
                ImGui.EndDisabled();
            }
            else if (type == typeof(TypeRef))
            {
                Label(label, description, onRemove);
                ImGui.BeginDisabled(readOnly);
                string s = (value as TypeRef)?.Name ?? "";
                if (ImGui.InputText("##v", ref s, 1024))
                    Apply(set, new TypeRef(s));
                ImGui.EndDisabled();
            }
            else if (type == typeof(Guid))
            {
                Label(label, description, onRemove);
                Guid g = value is Guid gv ? gv : Guid.Empty;

                float buttonWidth = readOnly ? 0f : 60f;
                ImGui.SetNextItemWidth(-buttonWidth - (readOnly ? 0f : ImGui.GetStyle().ItemSpacing.X));
                string text = g.ToString();
                ImGui.InputText("##v", ref text, 64, ImGuiInputTextFlags.ReadOnly);

                if (!readOnly)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("New", new Vector2(buttonWidth, 0)))
                        Apply(set, Guid.NewGuid());
                }
            }
            else if (type == typeof(ResourceRef))
            {
                Label(label, description, onRemove);
                string text = value != null ? value.ToString() : "0";
                ImGui.InputText("##v", ref text, 32, ImGuiInputTextFlags.ReadOnly);
            }
            else if (type.IsEnum)
            {
                DrawEnum(label, value, type, readOnly, description, set, onRemove);
            }
            else if (type == typeof(float))
            {
                Label(label, description, onRemove);
                ImGui.BeginDisabled(readOnly);
                float v = value is float f ? f : 0f;
                if (Scalar(ImGuiDataType.Float, ref v, "%.9g"))
                    Apply(set, v);
                ImGui.EndDisabled();
            }
            else if (type == typeof(double))
            {
                Label(label, description, onRemove);
                ImGui.BeginDisabled(readOnly);
                double v = value is double d ? d : 0d;
                if (Scalar(ImGuiDataType.Double, ref v, "%.17g"))
                    Apply(set, v);
                ImGui.EndDisabled();
            }
            else if (type == typeof(byte)) DrawInteger<byte>(label, value, ImGuiDataType.U8, readOnly, description, set, onRemove);
            else if (type == typeof(sbyte)) DrawInteger<sbyte>(label, value, ImGuiDataType.S8, readOnly, description, set, onRemove);
            else if (type == typeof(short)) DrawInteger<short>(label, value, ImGuiDataType.S16, readOnly, description, set, onRemove);
            else if (type == typeof(ushort)) DrawInteger<ushort>(label, value, ImGuiDataType.U16, readOnly, description, set, onRemove);
            else if (type == typeof(int)) DrawInteger<int>(label, value, ImGuiDataType.S32, readOnly, description, set, onRemove);
            else if (type == typeof(uint)) DrawInteger<uint>(label, value, ImGuiDataType.U32, readOnly, description, set, onRemove);
            else if (type == typeof(long)) DrawInteger<long>(label, value, ImGuiDataType.S64, readOnly, description, set, onRemove);
            else if (type == typeof(ulong)) DrawInteger<ulong>(label, value, ImGuiDataType.U64, readOnly, description, set, onRemove);
            else if (type == typeof(PointerRef))
            {
                DrawPointer(label, value is PointerRef pr ? pr : default, readOnly, description, depth, set, onRemove);
            }
            else if (type == typeof(BoxedValueRef))
            {
                DrawBoxed(label, (BoxedValueRef)value, readOnly, description, depth, onRemove);
            }
            else if (value is IList list)
            {
                DrawList(label, list, type, readOnly, fixedSize, description, depth, onRemove);
            }
            else if (value != null && type.GetProperties().Length > 0)
            {
                // struct or class instance: expand to its own properties
                if (Node(label, type.Name, description, onRemove))
                {
                    DrawProperties(value, readOnly, depth + 1);
                    ImGui.TreePop();
                }
            }
            else
            {
                Label(label, description, onRemove);
                ImGui.TextDisabled(value == null ? "(null)" : value.ToString());
            }
        }

        private static unsafe bool Scalar<T>(ImGuiDataType dataType, ref T value, string format = null) where T : unmanaged
        {
            fixed (T* p = &value)
            {
                return format == null
                    ? ImGui.InputScalar("##v", dataType, (IntPtr)p)
                    : ImGui.InputScalar("##v", dataType, (IntPtr)p, IntPtr.Zero, IntPtr.Zero, format);
            }
        }

        private void DrawInteger<T>(string label, object value, ImGuiDataType dataType, bool readOnly, string description,
            Action<object> set, Action onRemove) where T : unmanaged
        {
            Label(label, description, onRemove);
            ImGui.BeginDisabled(readOnly);

            T v = value is T t ? t : default;
            if (Scalar(dataType, ref v))
                Apply(set, v);

            ImGui.EndDisabled();
        }

        private void DrawEnum(string label, object value, Type type, bool readOnly, string description, Action<object> set, Action onRemove)
        {
            Label(label, description, onRemove);
            ImGui.BeginDisabled(readOnly);

            string current = value != null ? (Enum.GetName(type, value) ?? value.ToString()) : "";
            if (ImGui.BeginCombo("##v", current))
            {
                foreach (string name in Enum.GetNames(type))
                {
                    if (ImGui.Selectable(name, name == current) && name != current)
                        Apply(set, Enum.Parse(type, name));
                }

                ImGui.EndCombo();
            }

            ImGui.EndDisabled();
        }

        // ---- references ----------------------------------------------------------------------------

        private void DrawPointer(string label, PointerRef pointer, bool readOnly, string description, int depth, Action<object> set, Action onRemove)
        {
            switch (pointer.Type)
            {
                case PointerRefType.Internal:
                {
                    string className = pointer.Internal?.GetType().Name ?? "?";
                    bool open = Node(label, "-> " + className, description, onRemove);

                    if (open)
                    {
                        if (!readOnly && ImGui.SmallButton("Clear"))
                            Apply(set, new PointerRef(Guid.Empty));

                        DrawProperties(pointer.Internal, readOnly, depth + 1);
                        ImGui.TreePop();
                    }

                    break;
                }

                case PointerRefType.External:
                {
                    EbxAssetEntry target = Frosty.Core.App.AssetManager.GetEbxEntry(pointer.External.FileGuid);

                    Label(label, description, onRemove);
                    string text = target != null ? target.Name : pointer.External.FileGuid.ToString();

                    float buttons = (target != null ? 52f : 0f) + (readOnly ? 0f : 56f);
                    ImGui.SetNextItemWidth(-buttons - ImGui.GetStyle().ItemSpacing.X * ((target != null ? 1 : 0) + (readOnly ? 0 : 1)));
                    ImGui.InputText("##v", ref text, 1024, ImGuiInputTextFlags.ReadOnly);

                    if (target != null)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Open", new Vector2(52f, 0)))
                            openAsset(target);
                    }

                    if (!readOnly)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Clear", new Vector2(56f, 0)))
                            Apply(set, new PointerRef(Guid.Empty));
                    }

                    break;
                }

                default:
                    Label(label, description, onRemove);
                    ImGui.TextDisabled("(null)");
                    break;
            }
        }

        private void DrawBoxed(string label, BoxedValueRef boxed, bool readOnly, string description, int depth, Action onRemove)
        {
            if (boxed == null)
            {
                Label(label, description, onRemove);
                ImGui.TextDisabled("(null)");
                return;
            }

            if (Node(label, boxed.TypeString, description, onRemove))
            {
                DrawValue("Value", boxed.Value, boxed.Value?.GetType() ?? typeof(object), readOnly, false, null, depth + 1,
                    v => boxed.SetValue(v), null);
                ImGui.TreePop();
            }
        }

        // ---- lists ---------------------------------------------------------------------------------

        private void DrawList(string label, IList list, Type listType, bool readOnly, bool fixedSize, string description, int depth, Action onRemove)
        {
            Type elementType = listType.IsGenericType ? listType.GetGenericArguments()[0] : typeof(object);
            bool canResize = !readOnly && !fixedSize;

            bool open = Node(label, list.Count + (list.Count == 1 ? " item" : " items"), description, onRemove);
            if (!open)
                return;

            uint listId = ImGui.GetID("##list");
            if (!listLimits.TryGetValue(listId, out int limit))
                limit = PageSize;

            int removeIndex = -1;
            int shown = Math.Min(limit, list.Count);

            for (int i = 0; i < shown; i++)
            {
                int index = i;
                ImGui.PushID(index);

                DrawValue("[" + index + "]", list[index], elementType, readOnly, false, null, depth + 1,
                    v => list[index] = v, canResize ? () => removeIndex = index : (Action)null);

                ImGui.PopID();
            }

            if (shown < list.Count)
            {
                if (ImGui.Button("Show " + Math.Min(PageSize, list.Count - shown) + " more (" + (list.Count - shown) + " hidden)"))
                    listLimits[listId] = limit + PageSize;
            }

            if (canResize)
            {
                if (ImGui.SmallButton("Add"))
                    AddElement(list, elementType);

                if (list.Count > 0)
                {
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Clear"))
                    {
                        list.Clear();
                        Changed = true;
                    }
                }
            }

            if (removeIndex >= 0 && removeIndex < list.Count)
            {
                list.RemoveAt(removeIndex);
                Changed = true;
            }

            ImGui.TreePop();
        }

        private void AddElement(IList list, Type elementType)
        {
            try
            {
                object element;
                if (elementType == typeof(string))
                    element = "";
                else if (elementType == typeof(object))
                    return;
                else
                    element = Activator.CreateInstance(elementType);

                list.Add(element);
                Changed = true;
            }
            catch (Exception e)
            {
                // e.g. abstract or constructor-less classes; the WPF grid offered a class picker here
                Dialogs.Message("Couldn't create a new " + elementType.Name + ":\n" + e.Message +
                                "\n\nPicking a class for new elements hasn't been ported yet.", "Frosty Editor");
            }
        }
    }
}
