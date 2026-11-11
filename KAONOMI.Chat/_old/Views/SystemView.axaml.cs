using Avalonia.Controls;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>CPU / GPU / memory activity with graphs, hardware models and drives (<c>/system</c>).</summary>
public partial class SystemView : UserControl
{
    /// <summary>Initializes the view.</summary>
    public SystemView()
    {
        InitializeComponent();
    }
}
