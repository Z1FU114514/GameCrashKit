using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using GameCrashKit.Core.Models;

namespace GameCrashKit.Core.Parsers;

/// <summary>
/// Defensive, locale-independent parser for Windows Event XML (<c>EventRecord.ToXml()</c>).
/// Strictly prohibits DTDs and external entity resolution.
/// </summary>
public static class EventXmlParser
{
    private const long MaxDocumentCharacters = 1_048_576; // 1 MiB character cap per single event XML

    private static readonly XmlReaderSettings SafeReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxDocumentCharacters,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = true
    };

    /// <summary>
    /// Parses a raw Windows Event XML string into a structured <see cref="ParsedEventRecord"/>.
    /// </summary>
    public static ParsedEventRecord Parse(string rawXml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawXml);

        using var stringReader = new StringReader(rawXml);
        using var xmlReader = XmlReader.Create(stringReader, SafeReaderSettings);
        var doc = XDocument.Load(xmlReader, LoadOptions.None);

        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "Event", StringComparison.Ordinal))
        {
            throw new FormatException("Invalid Windows Event XML: root element must be <Event>.");
        }

        var systemElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == "System")
            ?? throw new FormatException("Invalid Windows Event XML: missing <System> element.");

        var providerName = systemElement.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Provider")?
            .Attribute("Name")?.Value ?? string.Empty;

        var eventIdText = systemElement.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "EventID")?.Value;
        if (!int.TryParse(eventIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var eventId))
        {
            throw new FormatException($"Invalid Windows Event XML: missing or non-numeric <EventID> ('{eventIdText}').");
        }

        var versionText = systemElement.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
        int? version = int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVersion)
            ? parsedVersion
            : null;

        var levelText = systemElement.Elements().FirstOrDefault(e => e.Name.LocalName == "Level")?.Value;
        int? level = int.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLevel)
            ? parsedLevel
            : null;

        var recordIdText = systemElement.Elements().FirstOrDefault(e => e.Name.LocalName == "EventRecordID")?.Value;
        long? recordId = long.TryParse(recordIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRecordId)
            ? parsedRecordId
            : null;

        var channel = systemElement.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "Channel")?.Value ?? string.Empty;

        DateTimeOffset? timeCreatedUtc = null;
        var systemTimeAttr = systemElement.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "TimeCreated")?
            .Attribute("SystemTime")?.Value;
        if (!string.IsNullOrWhiteSpace(systemTimeAttr) &&
            DateTimeOffset.TryParse(systemTimeAttr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTime))
        {
            timeCreatedUtc = parsedTime;
        }

        var namedFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positionalFields = new List<string>();
        var hasUnnamedData = false;

        var eventDataElement = root.Elements().FirstOrDefault(e => e.Name.LocalName == "EventData");
        if (eventDataElement is not null)
        {
            foreach (var dataNode in eventDataElement.Elements().Where(e => e.Name.LocalName == "Data"))
            {
                var value = dataNode.Value;
                positionalFields.Add(value);

                var nameAttr = dataNode.Attribute("Name")?.Value;
                if (!string.IsNullOrWhiteSpace(nameAttr))
                {
                    namedFields[nameAttr] = value;
                }
                else
                {
                    hasUnnamedData = true;
                }
            }
        }

        return new ParsedEventRecord(
            Channel: channel,
            ProviderName: providerName,
            EventId: eventId,
            Version: version,
            Level: level,
            RecordId: recordId,
            TimeCreatedUtc: timeCreatedUtc,
            NamedFields: namedFields,
            PositionalFields: positionalFields,
            HasUnnamedData: hasUnnamedData);
    }
}
