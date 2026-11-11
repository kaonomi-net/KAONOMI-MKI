using System.Collections.Generic;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Windows open when the application was closed with <c>/bye</c> (<c>screens.yml</c> next to the executable),
/// reopened as they were at the next start.
/// </summary>
public sealed class ScreenLayout
{
    /// <summary>Screen the chat is docked on (always on its right edge).</summary>
    public ScreenInfo? ChatScreen { get; set; }

    /// <summary>Window of the tabs, or null when it was closed.</summary>
    public WorkspaceLayout? Workspace { get; set; }

    /// <summary>Tools opened in their own window (<c>/n</c> commands).</summary>
    public List<WindowLayout> Windows { get; set; } = [];
}

/// <summary>Window of the tabs: its place and its tabs, in order.</summary>
public sealed class WorkspaceLayout
{
    /// <summary>Place of the window.</summary>
    public WindowPlacement? Placement { get; set; }

    /// <summary>Tool of each tab (<c>cache</c>, <c>ontology</c>, <c>system</c>…); code tabs are not kept.</summary>
    public List<string> Tabs { get; set; } = [];

    /// <summary>Tool of the selected tab.</summary>
    public string? Selected { get; set; }

    /// <summary>
    /// Split layout (4 panes on a 4K screen, 2 or 3 columns on an ultrawide one): the pane of each tool, left to right
    /// then top to bottom (2 × 2: 0 top left, 1 top right, 2 bottom left, 3 bottom right); empty for the single tab strip.
    /// </summary>
    public Dictionary<string, int> Panes { get; set; } = [];

    /// <summary>Split layout: the tool selected in each pane (in pane order, null for an empty pane).</summary>
    public List<string?> PaneSelections { get; set; } = [];

    /// <summary>Split layout: share of the width taken by each column of panes, left to right; null when not saved.</summary>
    public List<double>? ColumnShares { get; set; }

    /// <summary>Split layout: share of the height taken by each row of panes, top to bottom; null when not saved.</summary>
    public List<double>? RowShares { get; set; }

    /// <summary>Older files, 4K layout: share of the width taken by the left panes; read only (now <see cref="ColumnShares"/>).</summary>
    public double? SplitX { get; set; }

    /// <summary>Older files, 4K layout: share of the height taken by the top panes; read only (now <see cref="RowShares"/>).</summary>
    public double? SplitY { get; set; }
}

/// <summary>A tool opened in its own window.</summary>
public sealed class WindowLayout
{
    /// <summary>Tool shown (<c>cache</c>, <c>logDashboard</c>…).</summary>
    public string Tool { get; set; } = string.Empty;

    /// <summary>Place of the window.</summary>
    public WindowPlacement? Placement { get; set; }
}

/// <summary>Place of a window: its screen, position (screen pixels), size (device-independent pixels) and state.</summary>
public sealed class WindowPlacement
{
    /// <summary>Screen the window is on.</summary>
    public ScreenInfo? Screen { get; set; }

    /// <summary>Left edge, frame included, in screen pixels.</summary>
    public int X { get; set; }

    /// <summary>Top edge, frame included, in screen pixels.</summary>
    public int Y { get; set; }

    /// <summary>Client width, in device-independent pixels.</summary>
    public double Width { get; set; }

    /// <summary>Client height, in device-independent pixels.</summary>
    public double Height { get; set; }

    /// <summary>True when the window was maximized (the size and position are then the ones it gets back).</summary>
    public bool Maximized { get; set; }
}

/// <summary>Identity of a screen: its name and bounds (screen pixels), used to find it again.</summary>
public sealed class ScreenInfo
{
    /// <summary>Name given by the system (may be empty).</summary>
    public string? Name { get; set; }

    /// <summary>Left edge of the screen, in screen pixels.</summary>
    public int X { get; set; }

    /// <summary>Top edge of the screen, in screen pixels.</summary>
    public int Y { get; set; }

    /// <summary>Width of the screen, in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Height of the screen, in pixels.</summary>
    public int Height { get; set; }
}
