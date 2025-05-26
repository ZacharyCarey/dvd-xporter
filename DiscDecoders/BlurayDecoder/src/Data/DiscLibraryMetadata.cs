using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Xml;
using DiscDecoders.Common;

namespace DiscDecoders.BlurayDecoder;

/// <summary>A title's name in the disc library metadata.</summary>
/// <param name="TitleNumber">The title's number in index.bdmv (from 1).</param>
public sealed record MetadataTitleName(uint TitleNumber, string Name);

/// <summary>A picture of the disc, for a player's disc library.</summary>
/// <param name="Path">The image's path, from BDMV/META/DL.</param>
/// <param name="Width">Its width in pixels, or 0 if the disc doesn't say.</param>
/// <param name="Height">Its height in pixels, or 0 if the disc doesn't say.</param>
public sealed record MetadataThumbnail(string Path, uint Width, uint Height);

/// <summary>
/// The disc library metadata in one language (BDMV/META/DL/bdmt_xxx.xml): the disc's name and its place in a set, for
/// a player's disc library. It's optional, and informational only: a file that can't be read leaves its values empty.
/// </summary>
public class DiscLibraryMetadata
{
    /// <summary>The file's name, e.g. "bdmt_eng.xml".</summary>
    public readonly string FileName;

    /// <summary>The ISO 639-2 language code it's in, from its file name.</summary>
    public readonly string LanguageCode;

    /// <summary>The disc's name, or null if it doesn't say.</summary>
    public readonly string? Name;

    public readonly string? AlternativeName;

    /// <summary>How many discs the set it's part of has, or 0 if it doesn't say.</summary>
    public readonly byte NumberOfSets;

    /// <summary>Which disc of the set it is, or 0 if it doesn't say.</summary>
    public readonly byte SetNumber;

    public readonly ReadOnlyCollection<MetadataTitleName> TitleNames;

    public readonly ReadOnlyCollection<MetadataThumbnail> Thumbnails;

    internal DiscLibraryMetadata(string fileName, byte[]? data, Action<string>? onWarning)
    {
        FileName = fileName;
        LanguageCode = fileName[5..8].ToLowerInvariant();

        var titleNames = new List<MetadataTitleName>();
        var thumbnails = new List<MetadataThumbnail>();
        TitleNames = titleNames.AsReadOnly();
        Thumbnails = thumbnails.AsReadOnly();
        if (data is null || data.Length == 0)
            return;

        var document = new XmlDocument();
        try
        {
            using var stream = new MemoryStream(data);
            document.Load(stream);
        }
        catch (XmlException ex)
        {
            onWarning?.Invoke($"{fileName} couldn't be read: {ex.Message}");
            return;
        }

        // Elements are found by name wherever they are, the way libbluray finds them
        foreach (XmlElement element in document.GetElementsByTagName("*"))
        {
            switch (element.ParentNode?.LocalName, element.LocalName)
            {
                case ("title", "name"):
                    Name = element.InnerText;
                    break;
                case ("title", "alternative"):
                    AlternativeName = element.InnerText;
                    break;
                case ("title", "numSets"):
                    NumberOfSets = (byte)ParseOrZero(element.InnerText);
                    break;
                case ("title", "setNumber"):
                    SetNumber = (byte)ParseOrZero(element.InnerText);
                    break;
                case ("tableOfContents", "titleName") when element.HasAttribute("titleNumber"):
                    titleNames.Add(new MetadataTitleName(ParseOrZero(element.GetAttribute("titleNumber")), element.InnerText));
                    break;
                case ("description", "thumbnail") when element.HasAttribute("href"):
                    var size = Regex.Match(element.GetAttribute("size"), @"^\s*(\d+)x(\d+)\s*$", RegexOptions.IgnoreCase);
                    thumbnails.Add(size.Success
                        ? new MetadataThumbnail(element.GetAttribute("href"), ParseOrZero(size.Groups[1].Value), ParseOrZero(size.Groups[2].Value))
                        : new MetadataThumbnail(element.GetAttribute("href"), 0, 0));
                    break;
            }
        }
    }

    /// <summary>Whether a file in BDMV/META/DL is disc library metadata: bdmt_ and a language code, e.g. "bdmt_eng.xml".</summary>
    internal static bool IsMetadataFile(string fileName) =>
        fileName.StartsWith("bdmt_", StringComparison.OrdinalIgnoreCase) && fileName.Length == 12;

    // Discs are often authored with empty fields such as <numSets/>, which count as 0
    private static uint ParseOrZero(string? text) => uint.TryParse(text?.Trim(), out uint value) ? value : 0;
}

/// <summary>
/// The disc's identity (CERTIFICATE/id.bdmv), which BD-J programs and AACS use: who made it and its own ID, as hex.
/// </summary>
public class DiscIdentity
{
    /// <summary>The content provider's organization ID, 8 hex digits.</summary>
    public readonly string OrganizationId;

    /// <summary>The disc's ID, 32 hex digits.</summary>
    public readonly string DiscId;

    internal DiscIdentity(BitReader reader)
    {
        NavigationFile.ReadHeader(reader, "BDID");
        reader.Seek(40);
        OrganizationId = Convert.ToHexStringLower(reader.ReadBytes(4));
        DiscId = Convert.ToHexStringLower(reader.ReadBytes(16));
    }
}
