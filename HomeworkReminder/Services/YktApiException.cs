using System;

namespace HomeworkReminder.Services;

/// <summary>接口调用失败。</summary>
public class YktApiException : Exception
{
    public YktApiException(string message, int? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    public int? ErrorCode { get; }
}

/// <summary>登录态失效，需要重新扫码登录。</summary>
public sealed class YktAuthExpiredException : YktApiException
{
    public YktAuthExpiredException(string message, int? errorCode = null)
        : base(message, errorCode)
    {
    }
}
