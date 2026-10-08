using HomeAssistant.Desktop.Services;
using Xunit;

namespace HomeAssistant.Desktop.Tests;

public class HotkeyBindingTests
{
    private const uint VkH = 0x48;
    private const uint VkF9 = 0x78;

    [Fact]
    public void NamesTheModifiersInTheOrderWindowsWritesThem()
    {
        var binding = new HotkeyBinding { Win = true, Control = true, Alt = true, Shift = true, Key = VkH };

        Assert.Equal("Win + Ctrl + Alt + Shift + H", binding.ToString());
    }

    [Fact]
    public void SaysNoneWhenNothingIsBound()
    {
        Assert.Equal("None", new HotkeyBinding().ToString());

        // Modifiers on their own are not a binding.
        Assert.Equal("None", new HotkeyBinding { Control = true, Shift = true }.ToString());
    }

    [Fact]
    public void NeedsAModifierUnlessItIsAFunctionKey()
    {
        Assert.False(new HotkeyBinding { Key = VkH }.IsUsable);
        Assert.True(new HotkeyBinding { Control = true, Alt = true, Key = VkH }.IsUsable);
    }

    [Fact]
    public void IsNotUsableWithoutAKey()
    {
        Assert.False(new HotkeyBinding { Control = true, Key = 0 }.IsUsable);
    }

    [Theory]
    [InlineData(0x30, "0")]
    [InlineData(0x39, "9")]
    [InlineData(0x41, "A")]
    [InlineData(0x5A, "Z")]
    public void NamesTheLettersAndDigitsAsThemselves(uint key, string expected)
    {
        Assert.Equal(expected, HotkeyBinding.KeyName(key));
    }

    [Theory]
    [InlineData(0x70, "F1")]
    [InlineData(0x78, "F9")]
    [InlineData(0x7B, "F12")]
    [InlineData(0x87, "F24")]
    public void NumbersTheFunctionKeysFromOne(uint key, string expected)
    {
        Assert.Equal(expected, HotkeyBinding.KeyName(key));
    }

    [Theory]
    [InlineData(0x20, "Space")]
    [InlineData(0x1B, "Esc")]
    [InlineData(0x21, "Page Up")]
    [InlineData(0xBF, "/")]
    public void NamesTheNamedKeys(uint key, string expected)
    {
        Assert.Equal(expected, HotkeyBinding.KeyName(key));
    }

    [Fact]
    public void FallsBackToTheCodeForAKeyItDoesNotKnow()
    {
        // Better a picker that shows "Key 250" than one that shows a blank box.
        Assert.Equal("Key 250", HotkeyBinding.KeyName(250));
    }

    [Fact]
    public void CloningCopiesEveryPartAndSharesNothing()
    {
        var original = new HotkeyBinding { Control = true, Shift = true, Key = VkF9 };
        var copy = original.Clone();

        Assert.Equal(original.ToString(), copy.ToString());

        // The settings dialog edits a clone and throws it away on Cancel, so a change
        // to the copy must not reach the binding the app is still using.
        copy.Control = false;
        copy.Key = VkH;

        Assert.True(original.Control);
        Assert.Equal(VkF9, original.Key);
    }
}
