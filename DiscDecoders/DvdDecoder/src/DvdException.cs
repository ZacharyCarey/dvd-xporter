namespace DiscDecoders.DvdDecoder;

/// <summary>
/// The DVD couldn't be read or extracted: its files are missing or unreadable, or its structure isn't valid.
/// The message says what went wrong; <see cref="Exception.InnerException"/> holds the underlying error, if any.
/// </summary>
public class DvdException : Exception
{
    public DvdException(string message) : base(message)
    {
    }

    public DvdException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
