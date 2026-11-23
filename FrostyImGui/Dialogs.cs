using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using FrostySdk.Interfaces;
using ImGuiNET;

namespace Frosty.ImGuiKit
{
    public enum DialogResult { None, Ok, Yes, No, Cancel }

    public enum DialogButtons { Ok, OkCancel, YesNo, YesNoCancel }

    /// <summary>
    /// Base class for modal dialogs. ImGui is immediate-mode, so unlike WPF's ShowDialog()
    /// these never block: callers pass a callback that runs once the dialog closes.
    /// </summary>
    public abstract class Dialog
    {
        private static int nextId;

        private readonly int id = Interlocked.Increment(ref nextId);
        private bool openRequested = true;

        public string Title { get; protected set; }
        public bool IsClosed { get; private set; }

        /// <summary>Window flags; dialogs auto-size by default.</summary>
        protected virtual ImGuiWindowFlags Flags => ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings;

        protected virtual Vector2? FixedSize => null;

        /// <summary>Whether the title bar shows a close (X) button.</summary>
        protected virtual bool ShowCloseButton => true;

        protected abstract void DrawContents();

        protected void Close() => IsClosed = true;

        internal void Draw()
        {
            string popupId = Title + "###dlg" + id;

            if (openRequested)
            {
                ImGui.OpenPopup(popupId);
                openRequested = false;
            }

            ImGuiViewportPtr viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));

            ImGuiWindowFlags flags = Flags;
            if (FixedSize.HasValue)
            {
                ImGui.SetNextWindowSize(FixedSize.Value, ImGuiCond.Appearing);
                flags &= ~ImGuiWindowFlags.AlwaysAutoResize;
            }

            bool open = true;
            bool began = ShowCloseButton
                ? ImGui.BeginPopupModal(popupId, ref open, flags)
                : ImGui.BeginPopupModal(popupId, flags);

            if (began)
            {
                DrawContents();

                // A dialog opened while this one is up is drawn from inside it, so ImGui
                // treats it as a nested popup instead of closing this one.
                Dialogs.DrawFrom(Dialogs.IndexOf(this) + 1);

                if (IsClosed)
                    ImGui.CloseCurrentPopup();

                ImGui.EndPopup();
            }
            else if (!openRequested)
            {
                // Popup vanished (e.g. closed with the title-bar X).
                IsClosed = true;
            }

            if (!open)
                OnDismissed();
        }

        /// <summary>Called when the user closes the dialog with the window X / Escape.</summary>
        protected virtual void OnDismissed() => IsClosed = true;
    }

    public static class Dialogs
    {
        private static readonly List<Dialog> stack = new List<Dialog>();

        public static bool AnyOpen => stack.Count > 0;

        /// <summary>Show a dialog. Safe to call from any thread.</summary>
        public static void Show(Dialog dialog) => ImGuiHost.Post(() => stack.Add(dialog));

        internal static void DrawAll()
        {
            DrawFrom(0);
            stack.RemoveAll(d => d.IsClosed);
        }

        internal static int IndexOf(Dialog dialog) => stack.IndexOf(dialog);

        /// <summary>Draws the dialog at <paramref name="index"/>; it draws any later ones from inside itself.</summary>
        internal static void DrawFrom(int index)
        {
            if (index >= 0 && index < stack.Count)
                stack[index].Draw();
        }

        public static void Message(string text, string title = "Frosty Mod Manager", DialogButtons buttons = DialogButtons.Ok, Action<DialogResult> onClose = null)
            => Show(new MessageDialog(title, text, buttons, onClose));

        public static void Prompt(string title, string label, string initial, Action<string> onOk, Func<string, string> validate = null)
            => Show(new PromptDialog(title, label, initial, onOk, validate));

        /// <summary>
        /// Runs <paramref name="work"/> on a worker thread behind a progress dialog (replaces FrostyTaskWindow.Show).
        /// <paramref name="onDone"/> runs on the UI thread afterwards, and is where code that used to follow
        /// the blocking ShowDialog() call must go.
        /// </summary>
        public static void RunTask(string title, string initialStatus, Action<TaskContext> work,
            bool cancellable = false, Action<TaskContext> onDone = null, ILogger forwardTo = null)
            => Show(new TaskDialog(title, initialStatus, work, cancellable, onDone, forwardTo));

        public static void OpenFile(string title, string filter, bool multiselect, Action<string[]> onPicked)
            => Show(new FileDialog(title, filter, FileDialog.DialogMode.Open, multiselect, null, onPicked));

        public static void SaveFile(string title, string filter, string defaultExtension, Action<string> onPicked)
            => Show(new FileDialog(title, filter, FileDialog.DialogMode.Save, false, defaultExtension, files => onPicked(files[0])));
    }

    internal sealed class MessageDialog : Dialog
    {
        private readonly string text;
        private readonly DialogButtons buttons;
        private readonly Action<DialogResult> onClose;
        private bool finished;

        public MessageDialog(string title, string text, DialogButtons buttons, Action<DialogResult> onClose)
        {
            Title = title;
            this.text = text;
            this.buttons = buttons;
            this.onClose = onClose;
        }

        protected override void DrawContents()
        {
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            switch (buttons)
            {
                case DialogButtons.Ok:
                    if (ImGui.Button("OK", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Enter))
                        Finish(DialogResult.Ok);
                    break;
                case DialogButtons.OkCancel:
                    if (ImGui.Button("OK", new Vector2(80, 0)))
                        Finish(DialogResult.Ok);
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                        Finish(DialogResult.Cancel);
                    break;
                case DialogButtons.YesNo:
                    if (ImGui.Button("Yes", new Vector2(80, 0)))
                        Finish(DialogResult.Yes);
                    ImGui.SameLine();
                    if (ImGui.Button("No", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                        Finish(DialogResult.No);
                    break;
                case DialogButtons.YesNoCancel:
                    if (ImGui.Button("Yes", new Vector2(80, 0)))
                        Finish(DialogResult.Yes);
                    ImGui.SameLine();
                    if (ImGui.Button("No", new Vector2(80, 0)))
                        Finish(DialogResult.No);
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                        Finish(DialogResult.Cancel);
                    break;
            }
        }

        protected override void OnDismissed()
        {
            // Closing with X counts as the negative answer, like WPF's MessageBox.
            Finish(buttons == DialogButtons.YesNo ? DialogResult.No
                : buttons == DialogButtons.OkCancel || buttons == DialogButtons.YesNoCancel ? DialogResult.Cancel
                : DialogResult.Ok);
        }

        private void Finish(DialogResult result)
        {
            if (finished)
                return;

            finished = true;
            Close();
            onClose?.Invoke(result);
        }
    }

    /// <summary>Single-line text prompt (replaces AddProfileWindow).</summary>
    internal sealed class PromptDialog : Dialog
    {
        private readonly string label;
        private readonly Action<string> onOk;
        private readonly Func<string, string> validate;
        private string value;
        private string error;
        private bool focusPending = true;

        public PromptDialog(string title, string label, string initial, Action<string> onOk, Func<string, string> validate)
        {
            Title = title;
            this.label = label;
            this.onOk = onOk;
            this.validate = validate;
            value = initial ?? "";
        }

        protected override void DrawContents()
        {
            ImGui.TextUnformatted(label);
            ImGui.SetNextItemWidth(300);

            if (focusPending)
            {
                ImGui.SetKeyboardFocusHere();
                focusPending = false;
            }

            bool enter = ImGui.InputText("##value", ref value, 128, ImGuiInputTextFlags.EnterReturnsTrue);

            if (error != null)
                ImGui.TextColored(Theme.Error, error);

            ImGui.Spacing();

            bool ok = ImGui.Button("OK", new Vector2(80, 0)) || enter;
            ImGui.SameLine();
            bool cancel = ImGui.Button("Cancel", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape);

            if (ok)
            {
                string trimmed = value.Trim();
                error = trimmed.Length == 0 ? "Please enter a name." : validate?.Invoke(trimmed);
                if (error == null)
                {
                    Close();
                    onOk(trimmed);
                }
            }
            else if (cancel)
            {
                Close();
            }
        }
    }

    /// <summary>Handle given to background work; replaces the FrostyTaskWindow instance passed to task callbacks.</summary>
    public sealed class TaskContext
    {
        private readonly object sync = new object();
        private string status;
        private double progress;

        public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
        public ILogger TaskLogger { get; }
        public Exception Exception { get; internal set; }
        public bool Indeterminate { get; private set; } = true;

        public string Status { get { lock (sync) return status; } }
        public double Progress { get { lock (sync) return progress; } }

        internal TaskContext(string initialStatus, ILogger forwardTo)
        {
            status = initialStatus;
            TaskLogger = new ContextLogger(this, forwardTo);
        }

        /// <summary>Same contract as FrostyTaskWindow.Update: null leaves a value unchanged. Progress is 0-100.</summary>
        public void Update(string newStatus = null, double? newProgress = null)
        {
            lock (sync)
            {
                if (newStatus != null)
                    status = newStatus;

                if (newProgress.HasValue)
                {
                    progress = newProgress.Value;
                    Indeterminate = false;
                }
            }
        }

        public void SetIndeterminate(bool indeterminate)
        {
            lock (sync)
                Indeterminate = indeterminate;
        }

        private sealed class ContextLogger : ILogger
        {
            private readonly TaskContext owner;
            private readonly ILogger forward;

            public ContextLogger(TaskContext owner, ILogger forward)
            {
                this.owner = owner;
                this.forward = forward;
            }

            public void Log(string text, params object[] vars)
            {
                owner.Update(Format(text, vars));
                forward?.Log(text, vars);
            }

            public void LogWarning(string text, params object[] vars)
            {
                owner.Update(Format(text, vars));
                forward?.LogWarning(text, vars);
            }

            public void LogError(string text, params object[] vars)
            {
                owner.Update(Format(text, vars));
                forward?.LogError(text, vars);
            }

            private static string Format(string text, object[] vars)
            {
                try { return vars != null && vars.Length > 0 ? string.Format(text, vars) : text; }
                catch (FormatException) { return text; }
            }
        }
    }

    internal sealed class TaskDialog : Dialog
    {
        private readonly TaskContext context;
        private readonly bool cancellable;
        private readonly Action<TaskContext> onDone;
        private readonly string heading;
        private volatile bool completed;
        private bool cancelRequested;

        protected override Vector2? FixedSize => new Vector2(420, 0);

        protected override bool ShowCloseButton => false;

        protected override ImGuiWindowFlags Flags =>
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse;

        public TaskDialog(string title, string initialStatus, Action<TaskContext> work, bool cancellable,
            Action<TaskContext> onDone, ILogger forwardTo)
        {
            Title = title;
            heading = title;
            this.cancellable = cancellable;
            this.onDone = onDone;
            context = new TaskContext(initialStatus, forwardTo);

            Task.Run(() =>
            {
                try
                {
                    work(context);
                }
                catch (OperationCanceledException)
                {
                    // cancellation is a normal outcome; callers inspect Cancellation.IsCancellationRequested
                }
                catch (Exception e)
                {
                    context.Exception = e;
                }
                finally
                {
                    completed = true;
                }
            });
        }

        protected override void DrawContents()
        {
            ImGui.TextUnformatted(heading);
            ImGui.Spacing();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
            ImGui.TextUnformatted(context.Status ?? "");
            ImGui.PopTextWrapPos();

            if (context.Indeterminate)
            {
                // ImGui has no indeterminate bar; sweep a fraction back and forth instead.
                float t = (float)(ImGui.GetTime() % 2.0) / 2f;
                ImGui.ProgressBar(t < 0.5f ? t * 2f : (1f - t) * 2f, new Vector2(-1, 0), "");
            }
            else
            {
                float fraction = (float)Math.Clamp(context.Progress / 100.0, 0.0, 1.0);
                ImGui.ProgressBar(fraction, new Vector2(-1, 0));
            }

            if (cancellable)
            {
                ImGui.Spacing();
                ImGui.BeginDisabled(cancelRequested);
                if (ImGui.Button("Cancel", new Vector2(80, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    cancelRequested = true;
                    context.Cancellation.Cancel();
                }
                ImGui.EndDisabled();
            }

            if (completed)
            {
                Close();
                if (context.Exception != null && onDone == null)
                    Dialogs.Message(context.Exception.Message, "Error");
                else
                    onDone?.Invoke(context);
            }
        }

        // Task dialogs cannot be dismissed by the user; Cancel is the only way out.
        protected override void OnDismissed() { }
    }
}
