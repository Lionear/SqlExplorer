using Avalonia.Controls;

namespace SqlExplorer.App.ViewModels;

/// <summary>One plugin-contributed panel (SE-164 <c>panel</c> seam): its <see cref="ToolWindow"/> carries the
/// toggle/visibility/size the host chrome binds to, and <see cref="Content"/> is the Avalonia control the
/// plugin built (rendered in the bottom panel region when the window is visible).</summary>
public sealed record SubsystemPanel(ToolWindow Window, Control Content);
