using AIEngineer.Core.Settings;

namespace AIEngineer.Core.Tests;

 public class TypedNumberTests
{
    [Theory]
    [InlineData("1.5", 1.5f)]
    [InlineData("1,5", 1.5f)]
    [InlineData("1.", 1f)]
    [InlineData(" 2 ", 2f)]
    [InlineData(".25", 0.25f)]
    public void ANumberInRangeIsTakenAsTyped(string text, float expected)
    {
        Assert.True(TypedNumber.TryRead(text, 0.1f, 6f, out var v));
        Assert.Equal(expected, v, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("-")]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("1.5s")]
    [InlineData("NaN")]
    public void HalfTypedOrOutOfRangeIsNothing(string text)
    {
        Assert.False(TypedNumber.TryRead(text, 0.1f, 6f, out _));
    }
}
