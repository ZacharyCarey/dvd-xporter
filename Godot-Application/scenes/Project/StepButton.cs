using Godot;
using System;

public enum ProjectStep
{
    Information = 1,
    ClipRenamer = 2,
    Transcoding = 3,
    Export = 4,
}

[Tool]
public partial class StepButton : Button
{
	[Export]
	public ProjectStep StepNumber
	{
		get => _StepNumber;
		set
		{
			_StepNumber = value;
			var node = GetNodeOrNull<Label>("%Number");
			if (node != null)
				node.Text = ((int)value).ToString();
		}
	}
	ProjectStep _StepNumber = ProjectStep.Information;

	[Export]
	public string Title
	{
		get => GetNodeOrNull<Label>("%Title")?.Text ?? "";
		set
		{
			var node = GetNodeOrNull<Label>("%Title");
			if (node != null)
				node.Text = value;
		}
	}

	[Export]
	public string Description
	{
		get => GetNodeOrNull<Label>("%Description")?.Text ?? "";
		set
		{
			var node = GetNodeOrNull<Label>("%Description");
			if (node != null)
				node.Text = value;
		}
	}

	[Export]
	public Texture2D? StepIcon
	{
		get => GetNodeOrNull<TextureRect>("%StepIcon")?.Texture;
		set
		{
			var node = GetNodeOrNull<TextureRect>("%StepIcon");
			if (node != null)
				node.Texture = value;
		}
	}
}
