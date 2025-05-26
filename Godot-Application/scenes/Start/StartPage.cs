using System;
using System.Collections.Generic;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Settings;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Main menu shown when no project is open: create/open buttons and the recent projects list.
/// </summary>
public partial class StartPage : CenterContainer
{
    [Signal]
    public delegate void CreateProjectEventHandler(string name, string location, ProjectKind kind);

    [Signal]
    public delegate void OpenProjectEventHandler(string path);

    [Signal]
    public delegate void ClearRecentProjectsEventHandler();


    private MenuButton _newProjectButton = null!;
    private ItemList _recentList = null!;
    private Label _recentEmptyLabel = null!;
    private NewProjectDialog _newProjectDialog = null!;

    public override void _Ready()
    {
        _newProjectButton = GetNode<MenuButton>("%NewProjectButton");
        _recentList = GetNode<ItemList>("%RecentList");
        _recentEmptyLabel = GetNode<Label>("%RecentEmptyLabel");
        _newProjectDialog = GetNode<NewProjectDialog>("%NewProjectDialog");

        var popup = _newProjectButton.GetPopup();
        popup.AddItem("Movie", (int)ProjectKind.Movie);
        popup.AddItem("TV Show", (int)ProjectKind.TvShow);
        popup.IdPressed += ShowNewProjectDialog;

        GetNode<FileDialog>("%OpenProjectDialog").AddFilter($"*{ProjectFolder.ProjectFileExtension}", "DVD Xporter Project");
        GetNode<FileDialog>("%ImportProjectDialog").AddFilter($"*{ProjectPackage.Extension}", "DVD Xporter Project Package");
    }

    List<RecentProject> projectList = new();

    public void SetRecentProjects(IReadOnlyList<RecentProject> projects)
    {
        _recentList.Clear();
        projectList.Clear();
        foreach (var project in projects)
        {
            var index = _recentList.AddItem($"{project.Name}  —  {project.Kind.DisplayName()}");
            _recentList.SetItemTooltip(index, $"{project.ProjectFilePath}\nLast opened {project.LastOpened:g}");
            projectList.Add(project);
        }

        var hasRecent = projects.Count > 0;
        _recentList.Visible = hasRecent;
        _recentEmptyLabel.Visible = !hasRecent;
    }

    private void On_ClearRecentButton_Pressed() => EmitSignal(SignalName.ClearRecentProjects);

    private void On_RecentProjectActivated(int index)
    {
        if (index < 0 || index >= projectList.Count)
            return;
        var project = projectList[index];
        On_OpenProject(project.ProjectFilePath);
    }

    private void ShowNewProjectDialog(long kind) =>
        _newProjectDialog.PopupFor((ProjectKind)kind, AppSettings.Current.LastProjectLocation ?? AppPaths.DefaultProjectLocation);

    private void On_CreateProject()
    {
        var name = _newProjectDialog.ProjectName;
        var location = _newProjectDialog.ProjectLocation;
        EmitSignal(SignalName.CreateProject, name, location, (int)_newProjectDialog.Kind);
    }

    // TODO open and import should both go through the "OpenProject" signal, just needs properly hooked up in main
    private void On_OpenProject(string path) => EmitSignal(SignalName.OpenProject, path);
}
