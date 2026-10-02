namespace CookieLuncher.Core;

/// <summary>加密模块的通用错误。</summary>
public class CryptoException : Exception
{
    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    public CryptoException(string message)
        : base(message)
    {
    }

    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    /// <param name="inner">内部异常。</param>
    public CryptoException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>解密失败（通常是密码错误或数据损坏）。</summary>
public sealed class DecryptException : CryptoException
{
    /// <summary>初始化错误。</summary>
    public DecryptException()
        : base("密码错误或数据损坏")
    {
    }

    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    public DecryptException(string message)
        : base(message)
    {
    }

    /// <summary>初始化错误。</summary>
    /// <param name="message">错误信息。</param>
    /// <param name="inner">内部异常。</param>
    public DecryptException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
