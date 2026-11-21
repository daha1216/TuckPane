using Microsoft.UI.Xaml;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class ConsoleWindow
{
    private ThemeTarget _manageCategory = ThemeTarget.Organizer;

    private OrganizerPlacementMode ManagedPlacementMode =>
        ManageForm.ResolvePlacementMode(_manageCategory, _editing?.PlacementMode);

    internal void SetDockSize(Guid id, double size)
    {
        if (_host.State.Organizers.FirstOrDefault(item => item.Id == id) is
            { PlacementMode: OrganizerPlacementMode.Dock } dock)
            ChangeDockSettings(dock, dock.DockOrientation, size, dock.DockSpacingFactor);
    }

    private void ChangeDockSettings(OrganizerDefinition dock, DockOrientation direction, double size, double spacingFactor)
    {
        if (!_host.DockSettings.Apply(dock, direction, size, spacingFactor)) return;
        _host.Windows.FirstOrDefault(window => window.OrganizerId == dock.Id)?.ApplyDefinition(OrganizerVisualChange.Layout);
        RefreshDockSettings(dock);
        RefreshManageListItemMetadata(dock);
        _manageChangeVersion++;
        _stateSaveTimer.Stop();
        _stateSaveTimer.Start();
    }

    internal void RefreshDockSettings(OrganizerDefinition dock)
    {
        if (_selectedId != dock.Id || _editing is null) return;
        bool loading = _loadingEditor;
        _loadingEditor = true;
        try
        {
            _editing.DockIconSizeDip = dock.DockIconSizeDip;
            _editing.DockOrientation = dock.DockOrientation;
            _editing.DockSpacingFactor = dock.DockSpacingFactor;
            ManageForm.DockIconSize.SetModelValue(dock.DockIconSizeDip);
            ManageForm.DockSpacing.SetModelValue(dock.DockSpacingFactor);
            ManageForm.DockOrientation.SelectedIndex = (int)dock.DockOrientation;
            ManageForm.DockIconSizeLabel.Text = $"{dock.DockIconSizeDip:0} DIP";
            ManageForm.DockSpacingLabel.Text = $"{dock.DockSpacingFactor:0.00}";
        }
        finally { _loadingEditor = loading; }
    }

    private void SelectManageCategory(Guid? organizerId)
    {
        if (_host.State.Organizers.FirstOrDefault(item => item.Id == organizerId) is { } organizer)
            _manageCategory = OrganizerKinds.ThemeFor(organizer.PlacementMode);
        RootNavigation.SelectedItem = _manageCategory switch
        {
            ThemeTarget.Station => StationNavItem,
            ThemeTarget.Dock => DockNavItem,
            _ => ManageNavItem
        };
        UpdateManageCategoryHeader();
    }

    private void UpdateManageCategoryHeader()
    {
        ManagePageTitle.Text = AppStrings.Get(_manageCategory switch
        {
            ThemeTarget.Station => "NavStations",
            ThemeTarget.Dock => "NavDocks",
            _ => "NavOrganizers"
        });
        if (DeleteOrganizerButton is not null)
        {
            string deleteKey = _manageCategory switch
            {
                ThemeTarget.Station => "DeleteStation",
                ThemeTarget.Dock => "DeleteDock",
                _ => "DeleteOrganizer"
            };
            DeleteOrganizerButton.Tag = "loc:" + deleteKey;
            DeleteOrganizerButton.Content = AppStrings.Get(deleteKey);
        }
    }

    private bool UpdateDockAddControls()
    {
        bool dock = AddedPlacementMode == OrganizerPlacementMode.Dock;
        if (!_addNameWasEdited && !_loadingCreation)
        {
            _loadingDefaultName = true;
            AddForm.NameInput.Text = dock ? AppStrings.Get("NavDocks") : _defaultAddName;
            _loadingDefaultName = false;
        }
        if (!AddForm.UpdateDockControls(AddedPlacementMode, sourceMode: null, _creationCategory))
            return false;
        CreateOrganizerButton.IsEnabled = true;
        CreateLimitText.Visibility = Visibility.Collapsed;
        return true;
    }

    private bool UpdateDockManageControls() =>
        ManageForm.UpdateDockControls(ManagedPlacementMode, _editing?.PlacementMode, _manageCategory);
}
