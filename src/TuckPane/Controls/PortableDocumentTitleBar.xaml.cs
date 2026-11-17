using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace TuckPane;

public sealed partial class PortableDocumentTitleBar : UserControl
{
    public PortableDocumentTitleBar()
    {
        InitializeComponent();
    }

    internal Grid DragTitleBar => DragTitleBarRoot;
    internal Grid TitleDragSurface => TitleDragSurfaceElement;
    internal TextBlock TitleText => TitleTextElement;
    internal TextBox TitleEditor => TitleEditorElement;
    internal Button ColorButton => ColorButtonElement;
    internal ToggleButton OptionToggleButton => OptionToggleButtonElement;
    internal Button CloseButton => CloseButtonElement;
}
