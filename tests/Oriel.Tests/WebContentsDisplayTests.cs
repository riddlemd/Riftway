using Oriel.Cef.Interop;

namespace Oriel.Tests;

public sealed class WebContentsDisplayTests
{
    [Theory]
    [InlineData(cef_log_severity_t.LOGSEVERITY_DEBUG, ConsoleMessageLevel.Debug)]
    [InlineData(cef_log_severity_t.LOGSEVERITY_INFO, ConsoleMessageLevel.Info)]
    [InlineData(cef_log_severity_t.LOGSEVERITY_WARNING, ConsoleMessageLevel.Warning)]
    [InlineData(cef_log_severity_t.LOGSEVERITY_ERROR, ConsoleMessageLevel.Error)]
    public void LevelOf_UndoesCefsMappingOfBlinksLevels(cef_log_severity_t severity, ConsoleMessageLevel level)
        => Assert.Equal(level, WebContents.LevelOf(severity));

    [Fact]
    public void CursorName_NamesEveryCursorAsElectron()
    {
        var expected = new[]
        {
            "pointer", "crosshair", "hand", "text", "wait", "help", "e-resize", "n-resize", "ne-resize", "nw-resize", "s-resize",
            "se-resize", "sw-resize", "w-resize", "ns-resize", "ew-resize", "nesw-resize", "nwse-resize", "col-resize", "row-resize",
            "m-panning", "e-panning", "n-panning", "ne-panning", "nw-panning", "s-panning", "se-panning", "sw-panning", "w-panning",
            "move", "vertical-text", "cell", "context-menu", "alias", "progress", "nodrop", "copy", "none", "not-allowed", "zoom-in",
            "zoom-out", "grab", "grabbing", "m-panning-vertical", "m-panning-horizontal", "custom", "drag-drop-none",
            "drag-drop-move", "drag-drop-copy", "drag-drop-link",
        };
        Assert.Equal(expected, Enum.GetValues<cef_cursor_type_t>().Where(t => t != cef_cursor_type_t.CT_NUM_VALUES).Select(WebContents.CursorName));
    }
}
