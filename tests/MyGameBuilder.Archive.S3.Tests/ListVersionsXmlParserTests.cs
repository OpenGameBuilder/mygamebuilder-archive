using Xunit;

namespace MyGameBuilder.Archive.S3.Tests;

public sealed class ListVersionsXmlParserTests
{
    [Fact]
    public void ParseReadsVersionsAndDeleteMarkers()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ListVersionsResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Name>JGI_test1</Name>
              <Version>
                <Key>alice/project1/tile/Brick</Key>
                <VersionId>null</VersionId>
                <IsLatest>true</IsLatest>
                <LastModified>2011-09-15T22:58:53.000Z</LastModified>
                <ETag>&quot;abc123&quot;</ETag>
                <Size>4</Size>
                <StorageClass>STANDARD</StorageClass>
              </Version>
              <DeleteMarker>
                <Key>alice/project1/tile/Old</Key>
                <VersionId>real-version</VersionId>
                <IsLatest>false</IsLatest>
                <LastModified>2011-09-16T22:58:53.000Z</LastModified>
              </DeleteMarker>
              <IsTruncated>true</IsTruncated>
              <NextKeyMarker>next-key</NextKeyMarker>
              <NextVersionIdMarker>next-version</NextVersionIdMarker>
            </ListVersionsResult>
            """;

        var page = ListVersionsXmlParser.Parse(xml, firstOrdinal: 10);

        Assert.True(page.IsTruncated);
        Assert.Equal("next-key", page.NextKeyMarker);
        Assert.Equal("next-version", page.NextVersionIdMarker);
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(10, page.Entries[0].SourceListOrdinal);
        Assert.Null(page.Entries[0].ArchiveVersionId);
        Assert.Equal("abc123", page.Entries[0].ETag);
        Assert.False(page.Entries[0].IsDeleteMarker);
        Assert.True(page.Entries[1].IsDeleteMarker);
        Assert.Equal("real-version", page.Entries[1].ArchiveVersionId);
    }

    [Fact]
    public void ParseRejectsTruncatedPageWithoutNextKeyMarker()
    {
        const string xml = """
            <ListVersionsResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <IsTruncated>true</IsTruncated>
            </ListVersionsResult>
            """;

        Assert.Throws<ArchiveFatalException>(() => ListVersionsXmlParser.Parse(xml, firstOrdinal: 0));
    }

    [Fact]
    public void ParseDecodesUrlEncodedKeysAndMarkers()
    {
        const string xml = """
            <ListVersionsResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <EncodingType>url</EncodingType>
              <NextKeyMarker>alice/project1/tile/Hello+World%2BPlus</NextKeyMarker>
              <IsTruncated>true</IsTruncated>
              <Version>
                <Key>alice/project1/tile/Hello+World%2BPlus</Key>
                <VersionId>null</VersionId>
                <IsLatest>true</IsLatest>
                <LastModified>2011-09-15T22:58:53.000Z</LastModified>
                <ETag>&quot;abc123&quot;</ETag>
                <Size>4</Size>
                <StorageClass>STANDARD</StorageClass>
              </Version>
            </ListVersionsResult>
            """;

        var page = ListVersionsXmlParser.Parse(xml, firstOrdinal: 0);

        Assert.Equal("alice/project1/tile/Hello World+Plus", page.Entries[0].Key);
        Assert.Equal("alice/project1/tile/Hello World+Plus", page.NextKeyMarker);
        Assert.Contains("Hello+World%2BPlus", page.Entries[0].SourceListXml, StringComparison.Ordinal);
    }
}
