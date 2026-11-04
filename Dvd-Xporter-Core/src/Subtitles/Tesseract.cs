using System.ComponentModel;
using System.Diagnostics;

namespace DvdXporter.Core.Subtitles;

/// <summary>Problems running Tesseract, with a message suitable for the user.</summary>
public sealed class TesseractException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// The Tesseract OCR program, run from PATH like ffmpeg. Each language it reads needs its data installed (e.g. the
/// tesseract-ocr-fra package, or a tick in the Windows installer).
/// </summary>
public static class Tesseract
{
    private const string Program = "tesseract";

    public const string InstallHelp = "Install Tesseract OCR (on Linux the tesseract-ocr package, on Windows the installer from "
        + "github.com/UB-Mannheim/tesseract) with the languages you need, and make sure it's on the PATH.";

    /// <summary>Its version, e.g. "5.3.4", or null if it can't be run.</summary>
    public static async Task<string?> GetVersionAsync()
    {
        try
        {
            var (output, errors, _) = await RunAsync(["--version"], null, default);
            // Older versions write it to the error stream
            var line = (output.Length > 0 ? output : errors).Split('\n')[0].Trim();
            return line.StartsWith(Program, StringComparison.OrdinalIgnoreCase) ? line[Program.Length..].Trim() : line;
        }
        catch (TesseractException)
        {
            return null;
        }
    }

    /// <summary>The languages it has data for, e.g. "eng" and "fra" (its own "osd" left out).</summary>
    /// <exception cref="TesseractException">It can't be run.</exception>
    public static async Task<List<string>> ListLanguagesAsync()
    {
        var (output, _, _) = await RunAsync(["--list-langs"], null, default);
        // "List of available languages in "/usr/share/tesseract-ocr/5/tessdata/" (2):", then one a line
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Where(l => l != "osd")
            .ToList();
    }

    /// <summary>The text in a picture (a PNG of dark text on a light background), its lines as Tesseract found them.</summary>
    /// <exception cref="TesseractException">It can't be run, or couldn't read the picture.</exception>
    public static async Task<string> ReadAsync(byte[] png, string language, CancellationToken cancellation)
    {
        // A block of text (a caption's line or two), read from the input to the output
        var (output, errors, exitCode) = await RunAsync(["stdin", "stdout", "-l", language, "--psm", "6"], png, cancellation);
        if (exitCode != 0)
            throw new TesseractException($"Tesseract couldn't read a caption: {errors.Trim()}");
        return output;
    }

    private static async Task<(string Output, string Errors, int ExitCode)> RunAsync(IReadOnlyList<string> args, byte[]? input,
        CancellationToken cancellation)
    {
        var info = new ProcessStartInfo(Program)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        // Captions are read several at once, a process each, which goes faster than one process using every core
        info.Environment["OMP_THREAD_LIMIT"] = "1";

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new TesseractException($"Tesseract couldn't be started. {InstallHelp}");
        }
        catch (Win32Exception ex)
        {
            throw new TesseractException($"Tesseract isn't installed, or isn't on the PATH. {InstallHelp}", ex);
        }

        using (process)
        {
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(cancellation);
                var errors = process.StandardError.ReadToEndAsync(cancellation);
                if (input is not null)
                {
                    await process.StandardInput.BaseStream.WriteAsync(input, cancellation);
                    process.StandardInput.Close();
                }

                await process.WaitForExitAsync(cancellation);
                return (await output, await errors, process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                throw;
            }
            catch (IOException ex)
            {
                throw new TesseractException($"Tesseract stopped unexpectedly: {ex.Message}", ex);
            }
        }
    }
}
