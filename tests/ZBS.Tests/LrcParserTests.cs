using ZBS.Library;
using Xunit;

namespace ZBS.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parses_Basic_Lines_Sorted()
    {
        var lrc = "[00:12.50]Вторая\n[00:01.00]Первая\n[ar:Артист]\n";
        var lines = LrcParser.Parse(lrc)!;
        Assert.Equal(2, lines.Count);
        Assert.Equal("Первая", lines[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), lines[0].Time);
        Assert.Equal("Вторая", lines[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(12.5), lines[1].Time);
    }

    [Fact]
    public void Multiple_Timestamps_Per_Line()
    {
        var lines = LrcParser.Parse("[00:05][01:05]Припев")!;
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal("Припев", l.Text));
        Assert.Equal(TimeSpan.FromSeconds(65), lines[1].Time);
    }

    [Fact]
    public void Offset_Shifts_Earlier()
    {
        var lines = LrcParser.Parse("[offset:+500]\n[00:10.00]Строка")!;
        Assert.Equal(TimeSpan.FromSeconds(9.5), lines[0].Time);
    }

    [Fact]
    public void Plain_Text_Returns_Null()
    {
        Assert.Null(LrcParser.Parse("Просто текст песни\nбез таймкодов"));
        Assert.Null(LrcParser.Parse(null));
        Assert.Null(LrcParser.Parse("  "));
    }

    [Fact]
    public void IndexFor_Binary_Search()
    {
        var lines = LrcParser.Parse("[00:01]a\n[00:05]b\n[00:10]c")!;
        Assert.Equal(-1, LrcParser.IndexFor(lines, TimeSpan.Zero));
        Assert.Equal(0, LrcParser.IndexFor(lines, TimeSpan.FromSeconds(1)));
        Assert.Equal(1, LrcParser.IndexFor(lines, TimeSpan.FromSeconds(7)));
        Assert.Equal(2, LrcParser.IndexFor(lines, TimeSpan.FromSeconds(99)));
    }

    [Fact]
    public void StripTimestamps_Removes_Tags_And_Meta()
    {
        var plain = LrcParser.StripTimestamps("[ti:Название]\n[00:01.00]Первая\n[00:02]Вторая");
        Assert.Equal("Первая" + Environment.NewLine + "Вторая", plain);
    }

    [Fact]
    public void Empty_Line_Kept_As_Pause()
    {
        var lines = LrcParser.Parse("[00:01]Куплет\n[00:20]\n[00:30]Дальше")!;
        Assert.Equal(3, lines.Count);
        Assert.Equal("", lines[1].Text);
    }

    [Fact]
    public void Seconds_Over_59_Ignored()
    {
        Assert.Null(LrcParser.Parse("[12:87]счёт а не таймкод"));
    }

    [Fact]
    public void Bom_Does_Not_Break_Offset_And_Meta()
    {
        var lines = LrcParser.Parse("﻿[offset:+500]\n[00:10.00]Строка")!;
        Assert.Equal(TimeSpan.FromSeconds(9.5), lines[0].Time);
        Assert.Equal("Строка", LrcParser.StripTimestamps("﻿[ar:Артист]\n[00:01]Строка"));
    }

    [Fact]
    public void Offset_Applies_To_Whole_File_Even_When_Tag_Is_Last()
    {
        var lines = LrcParser.Parse("[00:10.00]Строка\n[offset:+1000]")!;
        Assert.Equal(TimeSpan.FromSeconds(9), lines[0].Time);
    }

    [Fact]
    public void Cr_Only_Line_Endings_Split_Correctly()
    {
        var lines = LrcParser.Parse("[00:01]Первая\r[00:05]Вторая")!;
        Assert.Equal(2, lines.Count);
        Assert.Equal("Первая", lines[0].Text);
        Assert.Equal("Вторая", lines[1].Text);
    }

    [Fact]
    public void Enhanced_Lrc_Word_Timestamps_Stripped()
    {
        var lines = LrcParser.Parse("[00:12.00]<00:12.20>Слово <00:12.85>Слово2")!;
        Assert.Equal("Слово Слово2", lines[0].Text);
        Assert.Equal("Слово Слово2", LrcParser.StripTimestamps("[00:12.00]<00:12.20>Слово <00:12.85>Слово2"));
    }

    [Theory]
    [InlineData("utf-16")]     // FF FE + LE
    [InlineData("utf-16BE")]   // FE FF + BE
    public void Sidecar_Utf16_With_Bom_Decoded(string encName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zbs-lrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var track = Path.Combine(dir, "song.mp3");
            var enc = System.Text.Encoding.GetEncoding(encName);
            File.WriteAllBytes(Path.Combine(dir, "song.lrc"),
                enc.GetPreamble().Concat(enc.GetBytes("[00:01.00]Кириллица")).ToArray());
            var text = LrcParser.ReadSidecar(track);
            var lines = LrcParser.Parse(text)!;
            Assert.Equal("Кириллица", lines[0].Text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Sidecar_Utf16_Without_Bom_Detected_By_Nuls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zbs-lrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var track = Path.Combine(dir, "song.mp3");
            File.WriteAllBytes(Path.Combine(dir, "song.lrc"),
                System.Text.Encoding.Unicode.GetBytes("[00:01.00]hello"));
            var lines = LrcParser.Parse(LrcParser.ReadSidecar(track))!;
            Assert.Equal("hello", lines[0].Text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
