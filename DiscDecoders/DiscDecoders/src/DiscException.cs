namespace DiscDecoders;

/// <summary>
/// A disc couldn't be read or prepared. The message is suitable to show the user;
/// <see cref="Exception.InnerException"/> holds the underlying error, if any.
/// </summary>
public class DiscException : Exception
{
    public DiscException(string message) : base(message)
    {
    }

    public DiscException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
