namespace DiscDecoders.BlurayDecoder;

/// <summary>
/// The Blu-ray can't be read, for example because its video is still encrypted.
/// The message says what went wrong.
/// </summary>
public class BlurayException : Exception
{
    public BlurayException(string message) : base(message)
    {
    }

    public BlurayException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
