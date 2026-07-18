using System.Collections.Generic;
using SqlExplorer.Core.Localization;

namespace SqlExplorer.App.ViewModels;

/// <summary>
/// Backs the plugin-changelog dialog (SE-138 phase 2): one or more plugins, each with a heading
/// (name + version) and optional markdown release notes. The window renders each section's notes via
/// <c>MiniMarkdown</c>, or a "no changelog" line when a version carries no notes.
/// </summary>
public sealed class PluginChangelogViewModel
{
    public sealed record Section(string Heading, string? Notes);

    public PluginChangelogViewModel(string title, IReadOnlyList<Section> sections, ILocalizer localizer)
    {
        Title = title;
        Sections = sections;
        Loc = localizer;
    }

    public string Title { get; }

    public IReadOnlyList<Section> Sections { get; }

    public ILocalizer Loc { get; }
}
