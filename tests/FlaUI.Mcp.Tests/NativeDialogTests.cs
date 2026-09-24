using PlaywrightWindows.Mcp.Core.Dialogs;
using Xunit;

namespace FlaUI.Mcp.Tests;

public class NativeDialogTests
{
    private static NativeControl Ctl(
        string r, NativeControlKind kind, string text, int id = 0, bool visible = true, bool isDefault = false) =>
        new(r, 0, 0, id, kind, "Button", text, visible, true, isDefault, null);

    private static readonly NativeControl[] MessageBox =
    {
        Ctl("c1", NativeControlKind.Text, "Delete the selected item?", id: 0xFFFF),
        Ctl("c2", NativeControlKind.Button, "&Yes", id: 6, isDefault: true),
        Ctl("c3", NativeControlKind.Button, "&No", id: 7),
    };

    [Fact]
    public void FindButton_MatchesTextIgnoringAmpersandAndCase()
    {
        Assert.Equal("c2", NativeDialog.FindButton(MessageBox, "yes")!.Ref);
        Assert.Equal("c3", NativeDialog.FindButton(MessageBox, "&No")!.Ref);
    }

    [Fact]
    public void FindButton_ByRef()
    {
        Assert.Equal("c3", NativeDialog.FindButton(MessageBox, "C3")!.Ref);
    }

    [Fact]
    public void FindButton_ByStandardId_WhenTextIsLocalized()
    {
        var german = new[]
        {
            Ctl("c1", NativeControlKind.Button, "&Ja", id: 6),
            Ctl("c2", NativeControlKind.Button, "&Nein", id: 7),
        };
        Assert.Equal("c2", NativeDialog.FindButton(german, "no")!.Ref);
    }

    [Fact]
    public void FindButton_ExactBeatsPrefix_AndPrefixIsFallback()
    {
        var controls = new[]
        {
            Ctl("c1", NativeControlKind.Button, "Save As..."),
            Ctl("c2", NativeControlKind.Button, "Save"),
            Ctl("c3", NativeControlKind.Button, "Don't Save"),
        };
        Assert.Equal("c2", NativeDialog.FindButton(controls, "save")!.Ref);
        Assert.Equal("c3", NativeDialog.FindButton(controls, "don't")!.Ref);
    }

    [Fact]
    public void FindButton_IgnoresHiddenAndNonButtons()
    {
        var controls = new[]
        {
            Ctl("c1", NativeControlKind.Text, "OK"),
            Ctl("c2", NativeControlKind.Button, "OK", visible: false),
        };
        Assert.Null(NativeDialog.FindButton(controls, "ok"));
    }

    [Fact]
    public void Describe_ShowsKindTextStateAndId()
    {
        Assert.Equal("c2 button \"&Yes\" [default] id=6", NativeDialog.Describe(MessageBox[1]));
        Assert.Equal("c1 text \"Delete the selected item?\"", NativeDialog.Describe(MessageBox[0]));
    }
}
