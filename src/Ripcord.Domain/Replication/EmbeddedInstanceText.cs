using System.Xml;

namespace Ripcord.Domain.Replication;

/// Reads one property out of an embedded instance that WMI hands back as CIM-XML text
/// (DTD 2.0: `INSTANCE/PROPERTY[@NAME]/VALUE`) rather than as an object.
///
/// Never throws: the text comes from a provider nobody can run off Windows, and anything it
/// cannot read degrades to unknown.
public static class EmbeddedInstanceText
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static string? Property(string? text, string propertyName)
    {
        // A UTF-16 decode can leave the byte-order mark in the string; XML refuses it there.
        string trimmed = text?.TrimStart('﻿') ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return null;
        }

        try
        {
            using XmlReader reader = XmlReader.Create(new StringReader(trimmed), Settings);
            while (reader.Read())
            {
                // CIM names are case-insensitive, the CIM-XML element names are not.
                if (reader.NodeType == XmlNodeType.Element
                    && reader.Name == "PROPERTY"
                    && string.Equals(
                        reader.GetAttribute("NAME"),
                        propertyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return ValueOf(reader);
                }
            }
        }
        catch (XmlException)
        {
        }

        return null;
    }

    private static string? ValueOf(XmlReader property)
    {
        if (property.IsEmptyElement)
        {
            return null;
        }

        using XmlReader subtree = property.ReadSubtree();
        while (subtree.Read())
        {
            if (subtree.NodeType == XmlNodeType.Element && subtree.Name == "VALUE")
            {
                return subtree.ReadElementContentAsString();
            }
        }

        return null;
    }
}
