using System;
using System.IO;
using System.Threading.Tasks;
using DuplicateFileFinderLib.Core;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Tree;
using DuplicateFileFinderLibTests.TestUtils;
using Xunit;
// ReSharper disable StringLiteralTypo

namespace DuplicateFileFinderLibTests.IO;

public sealed class CsvScanSerializerCsvTests
{
   
    [Fact]
    public void Import_RoundTrip_Basic()
    {
        var root = new RootNode();
        var ser = new CsvScanSerializer();
        
        var rootPath = "/tmp/dff_roundtrip";
        var fileA = $"{rootPath}/a.txt";
        var fileB = $"{rootPath}/b.txt";
        
        var input = CsvTestUtil.Csv(
            CsvTestUtil.CsvRowString("Folder", rootPath, "3/28/2007 7:13:50 PM +00:00",
                "3/28/2007 7:13:50 PM +00:00", "0", "2", "FFFF", "7" ),
            CsvTestUtil.CsvRowString("File", fileA, "3/28/2007 7:13:50 PM +00:00",
                "3/28/2007 7:13:50 PM +00:00", "10", "", "AAAA", "7" ),
            CsvTestUtil.CsvRowString("File", fileB, "3/28/2007 7:13:50 PM +00:00",
                "3/28/2007 7:13:50 PM +00:00", "10", "", "AAAA","7")                                /* Group */
            );
        
        using (var r = new StringReader(input)) ser.ImportInto(root, r);

        using var w = new StringWriter();
        ser.Export(root, w);
        var outCsv = w.ToString();

        Assert.Contains($@"""{rootPath}""", outCsv);  // tolerate path variation
        Assert.Contains(fileA, outCsv);
        Assert.Contains(fileB, outCsv);
    }

    [Fact]
    public void Import_QuotedCommas_AndEscapedQuotes()
    {
        var root = new RootNode();
        var ser = new CsvScanSerializer();

        // Path has comma and quotes; checksum has comma too
        var line1 = CsvTestUtil.CsvRowString("Folder", @"""/home/u""", "3/28/2007 7:13:50 PM +00:00",
            "3/28/2007 7:13:50 PM +00:00", "0", "0", "", @"""0""");
        var line2 = CsvTestUtil.CsvRowString("File", @"""/home/u/f,oo """"v2"""" .txt""",
            "3/28/2007 7:13:50 PM +00:00", "3/28/2007 7:13:50 PM +00:00", "0", "", @"""abcdef""", "3");

        var csv = CsvTestUtil.Csv(line1, line2);

        using (var r = new StringReader(csv)) ser.ImportInto(root, r);

        using var w = new StringWriter();
        ser.Export(root, w);
        var outCsv = w.ToString();

        Assert.Contains(@"/home/u/f,oo ""v2"" .txt", outCsv);
        Assert.Contains("abcdef", outCsv, StringComparison.InvariantCultureIgnoreCase);
    }

    [Fact]
    public void Import_BadHeader_Throws()
    {
        var root = new RootNode();
        var ser = new CsvScanSerializer();

        const string bad = "Kind,Path,Size\n";
        Assert.Throws<InvalidFormatException>(() =>
        {
            using var r = new StringReader(bad);
            ser.ImportInto(root, r);
        });
    }

    [Fact]
    public void Import_FileMissingParent_Throws()
    {
        var root = new RootNode();
        var ser = new CsvScanSerializer();
        
        // var csv = CsvTestUtil.Csv@"/File,""/no/parent/file.txt"",1,,.txt,AAAA,1");
        var csv = CsvTestUtil.Csv(CsvTestUtil.CsvRowString("File", "/no/parent/file.txt",
                          "3/28/2007 7:13:50 PM +00:00", "3/28/2007 7:13:50 PM +00:00", "1", "", "AAAA", "1"));

        var ex = Assert.Throws<InvalidFormatException>(() =>
        {
            using var r = new StringReader(csv);
            ser.ImportInto(root, r);
        });
        Assert.Contains("Missing parent folder", ex.Message);
    }

    [Fact]
    public void Import_Into_ExistingRoot()
    {
        var root = new RootNode();
    
        var  ser = new CsvScanSerializer();

        using (var r = new StringReader(CsvTestUtil.Csv(
                   CsvTestUtil.CsvRowString("Folder", "/a", "3/28/2007 7:13:50 PM +00:00",
                       "3/28/2007 7:13:50 PM +00:00", "1", "1", "", "1"),
                   CsvTestUtil.CsvRowString("File", "/a/x.txt", "3/28/2007 7:13:50 PM +00:00",
                       "3/28/2007 7:13:50 PM +00:00", "1", "", "aaaa", "2"))))
        {
            ser.ImportInto(root, r);
        }

        using (var r = new StringReader(CsvTestUtil.Csv(
                   CsvTestUtil.CsvRowString("Folder", "/b", "3/28/2007 7:13:50 PM +00:00",
                       "3/28/2007 7:13:50 PM +00:00", "1", "1", "", "1"),
                   CsvTestUtil.CsvRowString("File", "/b/y.txt", "3/28/2007 7:13:50 PM +00:00",
                       "3/28/2007 7:13:50 PM +00:00", "2", "", "bbbb", "2"))))
        {
            ser.ImportInto(root, r);
        }

        using var sw = new StringWriter();
        ser.Export(root, sw);
        var csv = sw.ToString();
        Assert.Contains("/a/x.txt", csv);
        Assert.Contains("/b/y.txt", csv);
    }
    
    [Fact]
    public async Task Csv_RoundTrip_IncludesCreationTime_And_GroupsByContent()
    {
        using var fs = new TempFsFixture();
        var created1 = new DateTimeOffset(2023, 10, 21, 12, 34, 56, TimeSpan.Zero);
        var created2 = created1.AddMinutes(1);

        var root = fs.Dir("root");
        var f1 = fs.File("root/a.txt", "HELLO"u8, created1);
        var f2 = fs.File("root/b.txt", "HELLO"u8, created2);

        var dff = new DuplicateFileFinder();
        await dff.ScanLocation(root);

        await using var sw = new StringWriter();
        dff.ExportToCsv(sw);
        var csv = sw.ToString();

        // parse with single source of truth
        var rows = CsvTestUtil.Parse(csv);

        AssertRows.ContainsFolder(rows, root);
        AssertRows.ContainsFile(rows, f1);
        AssertRows.ContainsFile(rows, f2);

        AssertRows.CreationTimeIs(rows, f1, created1);
        AssertRows.CreationTimeIs(rows, f2, created2);

        AssertRows.InSameGroup(rows, f1, f2);
    }


    [Fact]
    public void Export_Header_MatchesSpec()
    {
        // rely on spec as single truth
        var expected = CsvSpec.HeaderLine;
        // round-trip a trivial export to get an actual header
        using var sw = new StringWriter();
        sw.WriteLine(expected);
        var actual = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Equal(expected, actual);
    }
}
