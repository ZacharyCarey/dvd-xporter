namespace DiscDecoders;

public enum DiscMessageLevel
{
    /// <summary>Progress information, e.g. which stage is running.</summary>
    Info,

    /// <summary>Something went wrong but the operation carried on, e.g. a clip was skipped.</summary>
    Warning,
}

/// <summary>A message reported while a <see cref="Disc"/> is being read or prepared.</summary>
public sealed record DiscMessage(DiscMessageLevel Level, string Text);
