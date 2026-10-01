using CookieLuncher.Core;

namespace CookieLuncher.Tests;

/// <summary>密码规则测试。</summary>
public sealed class PasswordRulesTests
{
    [Theory]
    [InlineData(null, "密码不能为空。")]
    [InlineData("", "密码不能为空。")]
    public void ValidateNonEmpty_ShouldRejectEmpty(string? input, string expected)
        => Assert.Equal(expected, PasswordRules.ValidateNonEmpty(input));

    [Fact]
    public void ValidateNonEmpty_ShouldAcceptAnyNonEmptyValue()
        => Assert.Null(PasswordRules.ValidateNonEmpty("a"));

    [Fact]
    public void ValidateNew_ShouldApplyLengthWhitespaceAndConfirmRules()
    {
        Assert.Equal("密码不能为空。", PasswordRules.ValidateNew(string.Empty, string.Empty));
        Assert.Equal("密码长度不能少于 6 位。", PasswordRules.ValidateNew("12345", "12345"));
        Assert.Equal("密码首尾不能包含空格。", PasswordRules.ValidateNew(" 123456", " 123456"));
        Assert.Equal("两次输入的密码不一致。", PasswordRules.ValidateNew("123456", "123457"));
        Assert.Null(PasswordRules.ValidateNew("123456", "123456"));
    }
}
