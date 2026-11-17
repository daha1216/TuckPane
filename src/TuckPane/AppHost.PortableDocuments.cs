using Microsoft.UI.Xaml;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class AppHost
{
    private readonly PortableDocumentCoordinator _portableDocuments = new();

    internal static IPortableDocumentWindow WrapPortableWindow(NoteWindow window) => window;

    internal static IPortableDocumentWindow WrapPortableWindow(TodoWindow window) => window;

    private IEnumerable<NoteWindow> OpenExternalNoteWindows =>
        _portableDocuments.NoteWindows.Select(item => (NoteWindow)item.UnderlyingWindow);

    private IEnumerable<TodoWindow> OpenExternalTodoWindows =>
        _portableDocuments.TodoWindows.Select(item => (TodoWindow)item.UnderlyingWindow);

    private IEnumerable<Window> OpenPortableUiWindows =>
        _portableDocuments.AllWindows.Select(item => (Window)item.UnderlyingWindow);

    internal Task OpenExternalNoteAsync(string path) =>
        _portableDocuments.OpenAsync(
            PortableDocumentKind.Note,
            path,
            () => IsPreparingUpdate,
            async fullPath =>
            {
                PortableNoteDocument portable = await _noteStore.LoadPortableAsync(fullPath);
                if (portable.Theme != State.GlobalSettings.NoteTheme)
                {
                    portable.Theme = State.GlobalSettings.NoteTheme;
                    await _noteStore.SavePortableAsync(fullPath, portable);
                }
                if (IsPreparingUpdate) return null;
                var definition = new NoteDefinition
                {
                    Name = Path.GetFileNameWithoutExtension(fullPath),
                    Theme = State.GlobalSettings.NoteTheme,
                    FontSize = portable.FontSize,
                    ShowRuledLines = portable.ShowRuledLines,
                    Placement = FromPortablePlacement(portable.Placement)
                };
                var window = new NoteWindow(this, definition, _noteStore, fullPath, portable);
                window.InitializeHostWindow();
                return WrapPortableWindow(window);
            },
            fullPath => ReportOperationError(
                "TuckPane",
                AppStrings.Format("PortableNoteOpenErrorFormat", Path.GetFileName(fullPath), AppStrings.Get("PortableNoteExtensionError"))),
            (fullPath, ex) =>
            {
                AppLogger.Error($"无法打开便携便签：{fullPath}", ex);
                ReportOperationError("TuckPane", AppStrings.Format("PortableNoteOpenErrorFormat", Path.GetFileName(fullPath), ex.Message));
            });

    internal Task OpenExternalTodoAsync(string path) =>
        _portableDocuments.OpenAsync(
            PortableDocumentKind.Todo,
            path,
            () => IsPreparingUpdate,
            async fullPath =>
            {
                PortableTodoDocument document = await _noteStore.LoadTodoAsync(fullPath);
                if (document.Theme != State.GlobalSettings.NoteTheme)
                {
                    document.Theme = State.GlobalSettings.NoteTheme;
                    await _noteStore.SaveTodoAsync(fullPath, document);
                }
                if (IsPreparingUpdate) return null;
                var window = new TodoWindow(this, _noteStore, fullPath, document);
                window.InitializeHostWindow();
                return WrapPortableWindow(window);
            },
            fullPath => ReportOperationError(
                "TuckPane",
                AppStrings.Format("PortableTodoOpenErrorFormat", Path.GetFileName(fullPath), AppStrings.Get("PortableTodoExtensionError"))),
            (fullPath, ex) =>
            {
                AppLogger.Error($"无法打开便携待办：{fullPath}", ex);
                ReportOperationError("TuckPane", AppStrings.Format("PortableTodoOpenErrorFormat", Path.GetFileName(fullPath), ex.Message));
            });

    internal Task<string> RenameExternalNoteAsync(string path, string name) =>
        _portableDocuments.RenameAsync(
            PortableDocumentKind.Note,
            path,
            name,
            State.Organizers,
            SaveStateAsync,
            async (organizerId, targetPath) =>
            {
                if (!_windows.TryGetValue(organizerId, out MainWindow? owner)) return;
                try { await owner.RefreshNotesAsync(); }
                catch (Exception ex) { AppLogger.Error($"便携便签改名后刷新失败：{targetPath}", ex); }
            });

    internal Task<string> RenameExternalTodoAsync(string path, string name) =>
        _portableDocuments.RenameAsync(
            PortableDocumentKind.Todo,
            path,
            name,
            State.Organizers,
            SaveStateAsync,
            async (organizerId, targetPath) =>
            {
                if (!_windows.TryGetValue(organizerId, out MainWindow? owner)) return;
                try { await owner.RefreshNotesAsync(); }
                catch (Exception ex) { AppLogger.Error($"便携待办改名后刷新失败：{targetPath}", ex); }
            });

    internal void RebindPortableWindowAfterMove(string oldPath, string newPath) =>
        _portableDocuments.RebindAfterMove(oldPath, newPath);

    internal Task<bool> FlushPortableWindowAsync(string path) =>
        _portableDocuments.FlushWindowAsync(path);

    internal void ClosePortableWindowWithoutSave(string path) =>
        _portableDocuments.CloseWithoutSave(path);
}
