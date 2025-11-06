using System.IO;
using System.Linq;
using DuplicateFileFinderLib.IO;
using DuplicateFileFinderLib.Tree;
using Xunit;

namespace DuplicateFileFinderLibTests.IO;

public sealed class CsvScanSerializerCsvTests
{
    private static string Csv(params string[] lines)
        => string.Join('\n',  new[] { CsvScanSerializer.CsvHeaderRow }.Concat(lines));

    [Fact]
    public void Import_RoundTrip_Basic()
    {
        var root = new RootNode();
        var ser = new CsvScanSerializer();

        var rootPath = "/tmp/dff_roundtrip";
        var fileA = $"{rootPath}/a.txt";
        var fileB = $"{rootPath}/b.txt";

        var input = Csv(
            string.Join(",",
                "Folder",                          /* Kind */
                rootPath,                          /* Path */
                "0",                               /* Size */
                "0",                               /* FileCount */
                "FFFF",                            /* Checksum */
                "7"                                /* Group */
            ),
            string.Join(",",
                "File",                            /* Kind */
                fileA,                             /* Path */
                "10",                              /* Size */
                "",                                /* FileCount */
                "AAAA",                            /* Checksum */
                "7"                                /* Group */
            ),
            string.Join(",",
                "File",                            /* Kind */
                fileB,                             /* Path */
                "10",                              /* Size */
                "",                                /* FileCount */
                "AAAA",                            /* Checksum */
                "7"                                /* Group */
            ));
        
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
        // var line = @"File,""/home/u/f,oo """"v2"""" .txt"",0,,.txt,""abcdef"",3";
        var line = string.Join(",",
            "File",                                 /* Kind */
            @"""/home/u/f,oo """"v2"""" .txt""",    /* Path */
            "0",                                   /* Size */
            "",                                    /* FileCount */
            @"""abcdef""",                         /* Checksum */
            "3"                                    /* Group */
        );
        var csv = Csv(string.Join(",",
            "Folder",                          /* Kind */
            @"""/home/u""",                    /* Path */
            "0",                               /* Size */
            "0",                               /* FileCount */
            "",                                /* Checksum */
            @"""0"""                           /* Group */
        ), line);

        using (var r = new StringReader(csv)) ser.ImportInto(root, r);

        using var w = new StringWriter();
        ser.Export(root, w);
        var outCsv = w.ToString();

        Assert.Contains(@"/home/u/f,oo ""v2"" .txt", outCsv);
        Assert.Contains("abcdef", outCsv, System.StringComparison.InvariantCultureIgnoreCase);
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
        
        // var csv = Csv(@"/File,""/no/parent/file.txt"",1,,.txt,AAAA,1");
        var csv = Csv(string.Join(',',
            "File",                /*Kind*/
            "/no/parent/file.txt",  /*Path*/
            "1",                    /*Size*/
            "",                     /*FileCount*/
            "AAAA",                 /*Checksum*/
            "1"                     /*Group*/));

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
        
        using (var r = new StringReader(Csv(
                   string.Join(",",
                       "Folder",                      /* Kind */
                       "/a",                          /* Path */
                       "1",                           /* Size */
                       "1",                           /* FileCount */
                       "",                            /* Checksum */
                       "1"                            /* Group */
                   ),
                   string.Join(",",
                       "File",                        /* Kind */
                       "/a/x.txt",                    /* Path */
                       "1",                           /* Size */
                       "",                            /* FileCount */
                       "aaaa",                        /* Checksum */
                       "2"                            /* Group */
                   ))))
            ser.ImportInto(root, r);

        using (var r = new StringReader(Csv(
                   string.Join(",",
                       "Folder",                      /* Kind */
                       "/b",                          /* Path */
                       "1",                           /* Size */
                       "1",                           /* FileCount */
                       "",                            /* Checksum */
                       "1"                            /* Group */
                   ),
                   string.Join(",",
                       "File",                        /* Kind */
                       "/b/y.txt",                    /* Path */
                       "2",                           /* Size */
                       "",                            /* FileCount */
                       "bbbb",                        /* Checksum */
                       "2"                            /* Group */
                   ))))
            ser.ImportInto(root, r);
    
        using var sw = new StringWriter();
        ser.Export(root, sw);
        var csv = sw.ToString();
        Assert.Contains("/a/x.txt", csv);
        Assert.Contains("/b/y.txt", csv);
    }
}
