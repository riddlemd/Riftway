namespace Oriel.Tests;

public sealed class KeyMapTests
{
    [Theory]
    [InlineData("a", false, "a", "KeyA", 65)]
    [InlineData("A", false, "a", "KeyA", 65)]
    [InlineData("a", true, "A", "KeyA", 65)]
    [InlineData("7", false, "7", "Digit7", 55)]
    [InlineData("F1", false, "F1", "F1", 112)]
    [InlineData("F12", false, "F12", "F12", 123)]
    [InlineData("f24", false, "F24", "F24", 135)]
    [InlineData("Enter", false, "Enter", "Enter", 13)]
    [InlineData("return", false, "Enter", "Enter", 13)]
    [InlineData("Up", false, "ArrowUp", "ArrowUp", 38)]
    [InlineData("Space", false, " ", "Space", 32)]
    public void Resolve_MapsElectronNames(string name, bool shift, string key, string code, int vk)
        => Assert.Equal(new KeyMap.Key(key, code, vk), KeyMap.Resolve(name, shift));

    [Theory]
    [InlineData("F0")]
    [InlineData("F25")]
    [InlineData("Fx")]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("é")]
    public void Resolve_RejectsUnknownNames(string name) => Assert.Throws<ArgumentException>(() => KeyMap.Resolve(name, false));

    [Theory]
    [InlineData(InputModifiers.None, 0)]
    [InlineData(InputModifiers.Alt, 1)]
    [InlineData(InputModifiers.Control, 2)]
    [InlineData(InputModifiers.Meta, 4)]
    [InlineData(InputModifiers.Shift, 8)]
    [InlineData(InputModifiers.Shift | InputModifiers.Control | InputModifiers.IsKeypad, 10)]
    public void DevToolsModifiers_UsesTheProtocolBits(InputModifiers modifiers, int bits) => Assert.Equal(bits, KeyMap.DevToolsModifiers(modifiers));
}
