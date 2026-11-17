using TuckPane.Core;
using TuckPane.Models;

namespace TuckPane;

public sealed partial class AppHost
{
    private readonly SemaphoreSlim _nameVisibilityGate = new(1, 1);

    internal async Task SetOrganizerNameVisibilityAsync(Guid id, bool hideName)
    {
        await _nameVisibilityGate.WaitAsync();
        try
        {
            if (State.Organizers.FirstOrDefault(item => item.Id == id) is not OrganizerDefinition organizer) return;
            try { await OrganizerNameVisibility.SetAsync(organizer, hideName, SaveStateAsync); }
            finally
            {
                if (_windows.TryGetValue(id, out MainWindow? window)) window.RefreshOrganizerName();
            }
        }
        finally { _nameVisibilityGate.Release(); }
    }
}
