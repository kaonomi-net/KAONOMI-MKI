using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace KAONOMI.Chat.Services;

/// <summary>A project as the windows name it: its id (never shown) and its name.</summary>
public sealed record ProjectRef(string Id, string Name);

/// <summary>
/// The projects of the server (<c>/project</c>), by name, for the selector every window of a document shows in its
/// header: a document is attached to a project by its id (<c>projectId</c>), chosen by its name. Loaded once when the
/// chat starts and again each time a project is saved or deleted (<see cref="RefreshAsync"/>).
/// </summary>
public static class ProjectCatalog
{
    private static KaonomiAssistantService? _assistant;
    private static IReadOnlyList<ProjectRef> _projects = [];

    /// <summary>The projects, by name.</summary>
    public static IReadOnlyList<ProjectRef> Projects => _projects;

    /// <summary>Raised when the list changed (on the thread that asked for it: the views go back to theirs).</summary>
    public static event Action? Changed;

    /// <summary>Sets the server the projects come from, and loads them.</summary>
    public static void Initialize(KaonomiAssistantService assistant)
    {
        _assistant = assistant;
        _ = RefreshAsync();
    }

    /// <summary>Reads the projects again; a server that does not answer leaves the list as it is.</summary>
    public static async Task RefreshAsync()
    {
        if (_assistant is not { } assistant)
            return;
        try
        {
            var found = await assistant.SearchProjectsAsync("*", 500);
            _projects = found.Where(p => !string.IsNullOrWhiteSpace(p.Id) && !p.IsDeleted)
                .Select(p => new ProjectRef(p.Id!, string.IsNullOrWhiteSpace(p.Name) ? "(no name)" : p.Name.Trim()))
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            ActivityLog.Error("Project", "The projects could not be loaded for the selector of the windows", ex);
        }
    }

    /// <summary>The name of the project <paramref name="id"/>; empty when there is none, "(unknown project)" when it is not in the list (deleted).</summary>
    public static string NameOf(string? id) =>
        string.IsNullOrWhiteSpace(id) ? string.Empty : _projects.FirstOrDefault(p => p.Id == id)?.Name ?? "(unknown project)";
}
