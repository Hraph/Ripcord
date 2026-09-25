using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Replication;

/// `GetReplicationStatisticsEx` may hand its statistics back as CIM-XML text rather than as an
/// object; the adapter cannot be run off Windows, so the reading is tested here.
public class EmbeddedInstanceTextTests
{
    private const string Statistics =
        """
        <INSTANCE CLASSNAME="Msvm_ReplicationStatistics">
          <PROPERTY NAME="Caption" TYPE="string"></PROPERTY>
          <PROPERTY NAME="ReplicationHealth" TYPE="uint16"><VALUE>1</VALUE></PROPERTY>
          <PROPERTY.ARRAY NAME="ReplicationHealthDetails" TYPE="string">
            <VALUE.ARRAY></VALUE.ARRAY>
          </PROPERTY.ARRAY>
          <PROPERTY NAME="PendingReplicationSize" TYPE="uint64"><VALUE>123456789</VALUE></PROPERTY>
          <PROPERTY NAME="LastReplicationTime" TYPE="datetime">
            <VALUE>20260925101500.000000+000</VALUE>
          </PROPERTY>
        </INSTANCE>
        """;

    [Fact]
    public void Reads_a_property_from_a_dtd20_instance()
    {
        Assert.Equal(
            "123456789", EmbeddedInstanceText.Property(Statistics, "PendingReplicationSize"));
    }

    [Fact]
    public void Matches_the_property_name_case_insensitively()
    {
        Assert.Equal(
            "123456789", EmbeddedInstanceText.Property(Statistics, "pendingreplicationsize"));
    }

    [Fact]
    public void Reads_through_an_xml_declaration_and_a_byte_order_mark()
    {
        string text = "﻿<?xml version=\"1.0\" encoding=\"utf-16\"?>" + Statistics;

        Assert.Equal("123456789", EmbeddedInstanceText.Property(text, "PendingReplicationSize"));
    }

    [Fact]
    public void Returns_null_for_an_absent_property()
    {
        Assert.Null(EmbeddedInstanceText.Property(Statistics, "ReplicationSize"));
    }

    [Fact]
    public void Returns_null_for_a_property_without_a_value()
    {
        Assert.Null(EmbeddedInstanceText.Property(Statistics, "Caption"));
        Assert.Null(EmbeddedInstanceText.Property(
            "<INSTANCE CLASSNAME=\"X\"><PROPERTY NAME=\"Caption\" TYPE=\"string\"/></INSTANCE>",
            "Caption"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("<INSTANCE CLASSNAME=\"Msvm_ReplicationStatistics\"><PROPERTY NAME=\"Pending")]
    public void Returns_null_for_text_that_is_not_xml(string? text)
    {
        Assert.Null(EmbeddedInstanceText.Property(text, "PendingReplicationSize"));
    }

    [Fact]
    public void Ignores_a_doctype_and_resolves_no_entity()
    {
        string text =
            "<!DOCTYPE INSTANCE [<!ENTITY x SYSTEM \"http://example.invalid/x\">]>"
            + "<INSTANCE CLASSNAME=\"X\">"
            + "<PROPERTY NAME=\"Other\" TYPE=\"string\"><VALUE>&x;</VALUE></PROPERTY>"
            + "<PROPERTY NAME=\"Size\" TYPE=\"uint64\"><VALUE>42</VALUE></PROPERTY>"
            + "</INSTANCE>";

        Assert.Null(EmbeddedInstanceText.Property(text, "Other"));
    }
}
